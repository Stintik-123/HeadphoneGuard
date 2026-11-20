using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HeadphoneGuard.Audio;

/// <summary>
/// Следит за аудиосессиями Windows и определяет, кто именно издал звук.
///
/// Логика та же, что в open-source проекте windows_sound_tracker: перечисляем
/// сессии каждого выхода, снимаем пиковый уровень; «всплеск» — это отдельный
/// короткий звук внутри сессии (так ведут себя OEM-сервисы с голосовыми
/// оповещениями), а не просто «сессия активна».
/// </summary>
public sealed class SoundSourceTracker : IDisposable
{
    private const double OnsetThreshold = 0.02;
    private const double QuietThreshold = 0.002;
    private const int QuietDebounceMs = 120;

    private sealed class TrackedSession
    {
        public required AudioSessionControl Control { get; init; }
        public required string EndpointName { get; init; }
        public DateTime? OnsetAt { get; set; }
        public DateTime? QuietAt { get; set; }
        public double Peak { get; set; }
        public bool IsActive { get; set; }
    }

    private readonly ConcurrentDictionary<string, TrackedSession> _sessions = new();
    private readonly List<SoundEvent> _events = new();
    private readonly object _eventsGate = new();
    private readonly Dictionary<string, AudioSessionControl> _mutedSessions = new();
    private readonly object _mutedGate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly List<MMDevice> _devices = new();
    private readonly List<AudioSessionManager> _managers = new();
    private readonly List<string> _endpointNames = new();

    private Timer? _timer;
    private int _shortSoundMaxMs = 4000;

    public event Action<SoundEvent>? SoundDetected;

    public int ShortSoundMaxMs
    {
        get => _shortSoundMaxMs;
        set => _shortSoundMaxMs = Math.Max(200, value);
    }

    /// <summary>Последние зафиксированные звуки (свежие сверху).</summary>
    public IReadOnlyList<SoundEvent> RecentEvents
    {
        get
        {
            lock (_eventsGate)
                return _events.ToArray();
        }
    }

    public IReadOnlyList<string> MutedSessionNames
    {
        get
        {
            lock (_mutedGate)
                return _mutedSessions.Keys.ToArray();
        }
    }

    public void Start()
    {
        RefreshDevices();
        _timer = new Timer(_ => Poll(), null, 0, 120);
    }

    private void RefreshDevices()
    {
        try
        {
            foreach (var manager in _managers)
                manager.Dispose();
            foreach (var device in _devices)
                device.Dispose();
            _managers.Clear();
            _devices.Clear();
            _endpointNames.Clear();

            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                _devices.Add(device);
                _managers.Add(device.AudioSessionManager);
                _endpointNames.Add(device.FriendlyName);
            }

            Diagnostics.Log($"Слежу за {_devices.Count} аудио-выходами.");
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Не удалось получить список выходов: {ex.Message}");
        }
    }

    private void Poll()
    {
        try
        {
            for (int e = 0; e < _managers.Count; e++)
                PollEndpoint(_managers[e], _endpointNames[e]);
        }
        catch (Exception ex)
        {
            Diagnostics.Dev($"Ошибка опроса сессий: {ex.Message}");
        }
    }

    private void PollEndpoint(AudioSessionManager manager, string endpointName)
    {
        manager.RefreshSessions();

        var sessions = manager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            string key = session.GetSessionInstanceIdentifier;

            bool active;
            double peak;
            try
            {
                active = session.State == AudioSessionState.AudioSessionStateActive;
                peak = session.AudioMeterInformation.MasterPeakValue;
            }
            catch
            {
                continue;
            }

            if (!_sessions.TryGetValue(key, out var tracked))
            {
                tracked = new TrackedSession { Control = session, EndpointName = endpointName };
                _sessions[key] = tracked;
            }

            if (!active)
            {
                tracked.IsActive = false;
                Flush(tracked, endpointName);
                continue;
            }

            tracked.IsActive = true;

            if (peak >= OnsetThreshold)
            {
                bool wasQuiet = tracked.QuietAt is { } quiet && (DateTime.Now - quiet).TotalMilliseconds > QuietDebounceMs;

                if (tracked.OnsetAt is null)
                {
                    tracked.OnsetAt = DateTime.Now;
                }
                else if (wasQuiet)
                {
                    // Новый звук внутри той же сессии — предыдущий всплеск закончен.
                    Flush(tracked, endpointName);
                    tracked.OnsetAt = DateTime.Now;
                }

                tracked.QuietAt = null;
                tracked.Peak = Math.Max(tracked.Peak, peak);
            }
            else if (peak < QuietThreshold)
            {
                if (tracked.OnsetAt is not null && tracked.QuietAt is null)
                    tracked.QuietAt = DateTime.Now;
                else if (tracked.QuietAt is { } quiet && (DateTime.Now - quiet).TotalMilliseconds > QuietDebounceMs)
                    Flush(tracked, endpointName);
            }
        }
    }

    /// <summary>Фиксирует накопленный всплеск как отдельное событие звука.</summary>
    private void Flush(TrackedSession tracked, string endpointName)
    {
        if (tracked.OnsetAt is not { } onset)
        {
            tracked.Peak = 0;
            return;
        }

        DateTime end = tracked.QuietAt ?? DateTime.Now;
        var duration = end - onset;
        double peak = tracked.Peak;

        tracked.OnsetAt = null;
        tracked.QuietAt = null;
        tracked.Peak = 0;

        if (duration.TotalMilliseconds < 40)
            return;

        var evt = Describe(tracked.Control, endpointName, duration, peak);
        Record(evt);
    }

    private SoundEvent Describe(AudioSessionControl control, string endpointName, TimeSpan duration, double peak)
    {
        uint pid = 0;
        string display = string.Empty;
        bool systemSounds = false;

        try
        {
            pid = control.GetProcessID;
            display = control.DisplayName ?? string.Empty;
            systemSounds = control.IsSystemSoundsSession;
        }
        catch
        {
            // ignore
        }

        string processName = ResolveProcessName(pid, systemSounds);

        return new SoundEvent(
            DateTime.Now,
            endpointName,
            control.GetSessionInstanceIdentifier,
            string.IsNullOrWhiteSpace(display) ? processName : display,
            (int)pid,
            processName,
            duration,
            peak);
    }

    private static string ResolveProcessName(uint pid, bool systemSounds)
    {
        if (systemSounds)
            return "Системные звуки Windows";

        if (pid == 0)
            return "Неизвестный процесс";

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    private void Record(SoundEvent evt)
    {
        lock (_eventsGate)
        {
            _events.Insert(0, evt);
            if (_events.Count > 200)
                _events.RemoveRange(200, _events.Count - 200);
        }

        Diagnostics.Log($"Звук: {evt.ProcessName} ({evt.SessionName}) на «{evt.EndpointName}», {evt.Duration.TotalMilliseconds:F0} мс");
        SoundDetected?.Invoke(evt);
    }

    /// <summary>Заглушает конкретную аудиосессию — тот самый источник неприятного звука.</summary>
    public bool MuteSession(string sessionInstanceId)
    {
        try
        {
            AudioSessionControl? control;
            lock (_mutedGate)
            {
                if (!_mutedSessions.TryGetValue(sessionInstanceId, out var existing))
                {
                    control = FindSession(sessionInstanceId);
                    if (control is null)
                        return false;
                    _mutedSessions[sessionInstanceId] = control;
                }
                else
                {
                    control = existing;
                }
            }

            control.SimpleAudioVolume.Mute = true;
            Diagnostics.Log($"Заглушена сессия {control.DisplayName} (PID {control.GetProcessID}).");
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Не удалось заглушить сессию: {ex.Message}");
            return false;
        }
    }

    /// <summary>Снимает глушение со всех ранее заглушённых сессий.</summary>
    public void UnmuteAll()
    {
        lock (_mutedGate)
        {
            foreach (var control in _mutedSessions.Values)
            {
                try
                {
                    control.SimpleAudioVolume.Mute = false;
                }
                catch
                {
                    // ignore
                }
            }

            _mutedSessions.Clear();
        }
    }

    private AudioSessionControl? FindSession(string sessionInstanceId)
    {
        foreach (var manager in _managers)
        {
            try
            {
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    if (sessions[i].GetSessionInstanceIdentifier == sessionInstanceId)
                        return sessions[i];
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        UnmuteAll();
        foreach (var manager in _managers)
            manager.Dispose();
        foreach (var device in _devices)
            device.Dispose();
        _enumerator.Dispose();
    }
}
