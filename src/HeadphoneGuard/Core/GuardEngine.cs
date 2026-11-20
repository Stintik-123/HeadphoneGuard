using HeadphoneGuard.Audio;

namespace HeadphoneGuard.Core;

/// <summary>
/// Главный сценарий:
///   1) следит за зарядом любых беспроводных устройств (PnP, vendor-HID, GATT);
///   2) ловит момент, когда прозвучало одиночное оповещение (а не игровой звук);
///   3) глушит именно его — звук игры не страдает;
///   4) показывает своё уведомление Windows один раз за цикл разряда;
///   5) снимает глушение, когда заряд восстановился.
///
/// Тихий режим включается, пока хоть одно устройство «на дне»: в этом состоянии
/// любое одиночное оповещение (голос производителя, системный сигнал) заглушается,
/// а игровые звуки — внутри долгоживущих сессий — остаются нетронутыми.
/// </summary>
public sealed class GuardEngine : IDisposable
{
    private sealed class DeviceState
    {
        public int? LastPercent { get; set; }
        public bool Notified { get; set; }
        public DateTime ArmedUntil { get; set; } = DateTime.MinValue;
    }

    private readonly Settings _settings;
    private readonly AudioCatalog _catalog = new();
    private readonly SoundSourceTracker _tracker;
    private readonly ToastService _toast = new();
    private readonly BatteryService _battery = new();
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mutedSessions = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _pollTimer;
    private bool _notificationReady;

    public event Action<string>? StatusChanged;

    public GuardEngine(Settings settings)
    {
        _settings = settings;
        _tracker = new SoundSourceTracker
        {
            ShortSoundMaxMs = settings.ShortSoundMaxMs,
            QuietGapMs = settings.QuietGapSeconds * 1000,
        };
        _tracker.SoundDetected += OnSoundDetected;
    }

    public SoundSourceTracker Tracker => _tracker;

    public BatteryService Batteries => _battery;

    public IReadOnlyList<AudioEndpointInfo> Endpoints => _catalog.ListActiveRenderEndpoints();

    /// <summary>Активен ли тихий режим (есть устройство на низком заряде).</summary>
    public bool QuietModeActive => _devices.Values.Any(d =>
        d.LastPercent is { } p && p <= _settings.BatteryThreshold && d.ArmedUntil > DateTime.Now);

    /// <summary>Текущий заряд известных устройств.</summary>
    public IReadOnlyDictionary<string, int> Devices => _battery.Current;

    /// <summary>Добавить процесс в список «глушить всегда».</summary>
    public void AlwaysMute(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return;

        if (!_settings.MutedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            _settings.MutedProcesses.Add(processName);
            _settings.Save();
        }

        SetStatus($"Процесс {processName} добавлен в список «глушить всегда».");
    }

    public void Start()
    {
        _notificationReady = _toast.Initialize();
        _tracker.Start();
        _battery.Start();
        _battery.BatteryUpdated += OnBatteryUpdated;

        _ = PollAsync();
        _pollTimer = new Timer(
            _ => _ = PollAsync(),
            null,
            TimeSpan.FromSeconds(Math.Max(5, _settings.BatteryPollSeconds)),
            TimeSpan.FromSeconds(Math.Max(5, _settings.BatteryPollSeconds)));
    }

    private void SetStatus(string text)
    {
        Diagnostics.Log(text);
        StatusChanged?.Invoke(text);
    }

    // ---------------------------------------------------------------- батарея

    public async Task PollAsync()
    {
        try
        {
            await _battery.PollAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Ошибка опроса батареи: {ex.Message}");
        }
    }

    private void OnBatteryUpdated(BatteryService.DeviceBattery battery)
    {
        var state = GetState(battery.Name);
        int? previous = state.LastPercent;
        state.LastPercent = battery.Percent;

        bool below = battery.Percent <= _settings.BatteryThreshold;

        if (below)
        {
            // Тихий режим держим, пока устройство на дне.
            state.ArmedUntil = DateTime.Now.AddMinutes(_settings.QuietModeMinutes);

            if (!state.Notified)
            {
                state.Notified = true;
                HandleLowBattery(battery.Name, battery.Percent);
            }
        }
        else if (previous is not null && previous <= _settings.BatteryThreshold &&
                 battery.Percent > _settings.BatteryThreshold + 5)
        {
            HandleRecovered(battery.Name);
        }
    }

    private DeviceState GetState(string name)
    {
        if (!_devices.TryGetValue(name, out var state))
        {
            state = new DeviceState();
            _devices[name] = state;
        }

        return state;
    }

    // ------------------------------------------------------- срабатывание

    private void HandleLowBattery(string deviceName, int percent)
    {
        SetStatus($"{deviceName}: заряд {percent}% — сработало.");

        // Ищем одиночное оповещение, прозвучавшее недавно: обычно именно оно
        // и есть «голос о разряде».
        var candidates = _tracker.RecentEvents
            .Where(e => e.IsOneShot)
            .Where(e => e.Timestamp >= DateTime.Now.AddSeconds(-_settings.LookbackSeconds))
            .Where(e => !IsProtected(e.ProcessName))
            .OrderByDescending(e => e.Timestamp)
            .ToList();

        if (candidates.Count > 0)
        {
            var culprit = candidates[0];
            if (MuteSession(culprit))
            {
                Notify(
                    "Устройство разряжается",
                    $"{deviceName}: {percent}%. Заглушён источник: {culprit.ProcessName}.");
                return;
            }
        }

        Notify(
            "Устройство разряжается",
            $"{deviceName}: {percent}%. Тихий режим включён — оповещения будут заглушены.");
    }

    private void HandleRecovered(string deviceName)
    {
        bool hadMutes = _mutedSessions.Count > 0;
        UnmuteAll();

        var state = GetState(deviceName);
        state.Notified = false;
        state.ArmedUntil = DateTime.MinValue;

        if (hadMutes)
            Notify("Заряд восстановлен", $"{deviceName} снова заряжен. Звук возвращён.");
    }

    /// <summary>
    /// Реагирует на короткий звук: глушит одиночные оповещения, пока тихий режим активен.
    /// Звуки внутри долгоживущих сессий (игра, музыка) не трогаем.
    /// </summary>
    private void OnSoundDetected(SoundEvent evt)
    {
        if (!evt.IsOneShot)
            return;

        if (IsProtected(evt.ProcessName))
            return;

        bool alwaysMute = _settings.MutedProcesses.Any(p =>
            !string.IsNullOrWhiteSpace(p) &&
            evt.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase));

        if (!alwaysMute && !QuietModeActive)
            return;

        if (!MuteSession(evt))
            return;

        SetStatus($"Заглушён источник «{evt.ProcessName}».");
        Notify("Заглушено оповещение", $"Источник: {evt.ProcessName}.");
    }

    private bool MuteSession(SoundEvent evt)
    {
        if (!_tracker.MuteSession(evt.SessionId))
            return false;

        _mutedSessions.Add(evt.SessionId);
        return true;
    }

    private bool IsProtected(string processName)
    {
        return _settings.ProtectedProcesses.Any(p =>
            !string.IsNullOrWhiteSpace(p) &&
            processName.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private void Notify(string title, string message)
    {
        if (_settings.ShowNotification && _notificationReady)
            _toast.Show(title, message);
    }

    // ------------------------------------------------------------- mute

    public void MuteEndpointBriefly(string endpointId, int seconds)
    {
        var device = _catalog.Open(endpointId);
        if (device is null)
            return;

        try
        {
            var volume = device.AudioEndpointVolume;
            bool wasMuted = volume.Mute;
            float wasVolume = volume.MasterVolumeLevelScalar;

            volume.Mute = true;
            Diagnostics.Log($"Выход «{device.FriendlyName}» приглушён на {seconds} с.");

            _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ =>
            {
                try
                {
                    volume.Mute = wasMuted;
                    if (!wasMuted)
                        volume.MasterVolumeLevelScalar = wasVolume;
                }
                catch (Exception ex)
                {
                    Diagnostics.Dev($"Не удалось вернуть громкость: {ex.Message}");
                }
                finally
                {
                    device.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Не удалось приглушить выход: {ex.Message}");
            device.Dispose();
        }
    }

    public void UnmuteAll()
    {
        _tracker.UnmuteAll();
        _mutedSessions.Clear();
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _tracker.Dispose();
        _battery.Dispose();
    }
}
