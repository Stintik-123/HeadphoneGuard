using System.Diagnostics;
using HeadphoneGuard.Audio;
using HeadphoneGuard.Bluetooth;

namespace HeadphoneGuard.Core;

/// <summary>
/// Главный сценарий: следит за зарядом Bluetooth-устройств, ловит момент, когда
/// Windows проигрывает «ваш наушник разряжается», определяет, какая именно
/// аудиосессия издала этот звук, и заглушает только её — звук игры не страдает.
/// </summary>
public sealed class GuardEngine : IDisposable
{
    private readonly Settings _settings;
    private readonly AudioCatalog _catalog = new();
    private readonly SoundSourceTracker _tracker;
    private readonly ToastService _toast = new();
    private readonly BluetoothBatteryReader _gattFallback = new();

    private readonly Dictionary<string, int> _lastBattery = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deviceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mutedThisCycle = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _batteryTimer;
    private DateTime _armedUntil = DateTime.MinValue;
    private bool _notificationReady;

    public event Action<string>? StatusChanged;

    public GuardEngine(Settings settings)
    {
        _settings = settings;
        _tracker = new SoundSourceTracker { ShortSoundMaxMs = settings.ShortSoundMaxMs };
        _tracker.SoundDetected += OnSoundDetected;
    }

    public SoundSourceTracker Tracker => _tracker;

    public IReadOnlyList<AudioEndpointInfo> Endpoints => _catalog.ListActiveRenderEndpoints();

    public void Start()
    {
        _notificationReady = _toast.Initialize();
        _tracker.Start();
        _batteryTimer = new Timer(
            _ => _ = PollBatteryAsync(),
            null,
            0,
            Math.Max(5000, _settings.BatteryPollSeconds * 1000));
    }

    private void SetStatus(string text)
    {
        Diagnostics.Log(text);
        StatusChanged?.Invoke(text);
    }

    // ---------------------------------------------------------------- батарея

    public async Task PollBatteryAsync()
    {
        try
        {
            var readings = await ReadBatteryLevels().ConfigureAwait(false);
            var now = DateTime.Now;

            foreach (var reading in readings)
            {
                string key = string.IsNullOrEmpty(reading.Address) ? reading.Name : reading.Address;
                _deviceNames[key] = reading.Name;

                int? previous = _lastBattery.TryGetValue(key, out var p) ? p : null;
                DateTime previousSeen = _lastSeen.TryGetValue(key, out var s) ? s : now.AddSeconds(-30);
                _lastBattery[key] = reading.Percent ?? 0;
                _lastSeen[key] = now;

                if (reading.Percent is not { } percent)
                    continue;

                bool wasAbove = previous is null || previous > _settings.BatteryThreshold;
                bool isBelow = percent <= _settings.BatteryThreshold;

                if (wasAbove && isBelow)
                {
                    await HandleLowBatteryAsync(reading, previousSeen, now).ConfigureAwait(false);
                }
                else if (previous is not null && previous <= _settings.BatteryThreshold && percent > _settings.BatteryThreshold + 5)
                {
                    HandleRecovered(reading);
                }

                // «Взводим» защиту: если заряд уже почти разряжен, следующий
                // короткий звук почти наверняка — это оповещение о батарее.
                if (percent <= _settings.BatteryThreshold + 5)
                    _armedUntil = now.AddSeconds(120);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Ошибка опроса батареи: {ex.Message}");
        }
    }

    private sealed record BatteryReading(string Name, string Address, int? Percent, bool Connected);

    private async Task<IReadOnlyList<BatteryReading>> ReadBatteryLevels()
    {
        var result = new List<BatteryReading>();

        // 1) Классические Bluetooth-устройства: свойство PnP-дерева.
        foreach (var node in DeviceTree.ReadBluetoothBatteryNodes())
        {
            string name = string.IsNullOrWhiteSpace(node.FriendlyName) ? node.Address : node.FriendlyName;
            result.Add(new BatteryReading(name, node.Address, node.BatteryPercent, node.Connected ?? true));
        }

        // 2) LE-устройства без PnP-батареи: GATT Battery Service.
        if (result.Count == 0)
        {
            foreach (var device in await _gattFallback.ReadAsync().ConfigureAwait(false))
            {
                if (device.BatteryPercent is { } percent)
                    result.Add(new BatteryReading(device.Name, device.Address, percent, device.Connected));
            }
        }

        return result;
    }

    // ------------------------------------------------------- срабатывание

    private async Task HandleLowBatteryAsync(BatteryReading reading, DateTime previousSeen, DateTime now)
    {
        var endpoints = Endpoints;
        var endpoint = EndpointMatcher.Match(endpoints, reading.Name, _settings.EndpointOverrides);

        SetStatus($"{reading.Name}: заряд {reading.Percent}% — сработало.");

        // Кандидаты: короткие звуки, прозвучавшие между прошлым и текущим опросом.
        var candidates = _tracker.RecentEvents
            .Where(e => e.Timestamp >= previousSeen.AddSeconds(-3) && e.Timestamp <= now.AddSeconds(5))
            .Where(e => e.Duration.TotalMilliseconds <= _settings.ShortSoundMaxMs)
            .Where(e => !IsProtected(e.ProcessName))
            .OrderByDescending(e => _settings.MutedProcesses.Any(p =>
                e.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(e => e.Timestamp)
            .ToList();

        if (candidates.Count > 0)
        {
            var culprit = candidates[0];
            if (_tracker.MuteSession(culprit.SessionId))
            {
                _mutedThisCycle.Add(culprit.SessionId);
                SetStatus($"Заглушён источник «{culprit.ProcessName}».");
                Notify(
                    "Наушники разряжаются",
                    $"Заряд {reading.Percent}%. Заглушён источник: {culprit.ProcessName}.");
                return;
            }
        }

        // Фолбэк: источник не нашли — гасим сам выход ненадолго.
        if (endpoint is not null)
        {
            MuteEndpointBriefly(endpoint.Id, 8);
            Notify(
                "Наушники разряжаются",
                $"Заряд {reading.Percent}%. Источник звука не определён — приглушён выход «{endpoint.FriendlyName}».");
            return;
        }

        Notify("Наушники разряжаются", $"Заряд {reading.Percent}%.");
    }

    private void HandleRecovered(BatteryReading reading)
    {
        if (_mutedThisCycle.Count == 0)
            return;

        _tracker.UnmuteAll();
        _mutedThisCycle.Clear();
        SetStatus($"{reading.Name}: заряд восстановлен — глушение снято.");
        Notify("Заряд восстановлен", $"{reading.Name} снова заряжен. Звук возвращён.");
    }

    /// <summary>
    /// Срабатывает сразу на короткий звук, когда устройство уже «на дне» —
    /// так мы ловим оповещение Windows даже между опросами заряда.
    /// </summary>
    private void OnSoundDetected(SoundEvent evt)
    {
        if (DateTime.Now > _armedUntil)
            return;

        if (evt.Duration.TotalMilliseconds > _settings.ShortSoundMaxMs)
            return;

        if (IsProtected(evt.ProcessName))
            return;

        bool known = _settings.MutedProcesses.Any(p =>
            evt.ProcessName.Contains(p, StringComparison.OrdinalIgnoreCase));

        // Известный «нарушитель» глушим всегда, остальных — только когда заряд на исходе.
        if (!known && DateTime.Now > _armedUntil)
            return;

        if (_tracker.MuteSession(evt.SessionId))
        {
            _mutedThisCycle.Add(evt.SessionId);
            SetStatus($"Заглушён источник «{evt.ProcessName}» (заряд на исходе).");
            Notify("Заглушён раздражающий звук", $"Источник: {evt.ProcessName}.");
        }
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

    public void UnmuteEverything() => _tracker.UnmuteAll();

    public void Dispose()
    {
        _batteryTimer?.Dispose();
        _tracker.Dispose();
    }
}
