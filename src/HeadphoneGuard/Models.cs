namespace HeadphoneGuard;

/// <summary>Bluetooth-устройство с известным уровнем заряда.</summary>
public sealed record BtDeviceInfo(
    string Id,
    string Name,
    string Address,
    int? BatteryPercent,
    bool Connected)
{
    public bool HasBattery => BatteryPercent.HasValue;
}

/// <summary>Аудио-выход (render endpoint) Windows.</summary>
public sealed record AudioEndpointInfo(
    string Id,
    string FriendlyName,
    string DeviceDescription,
    bool IsActive);

/// <summary>
/// Зафиксированный короткий звук: аудиосессия, которая ненадолго активировалась.
/// Это и есть «источник неприятного звука».
/// </summary>
public sealed record SoundEvent(
    DateTime Timestamp,
    string EndpointName,
    string SessionId,
    string SessionName,
    int ProcessId,
    string ProcessName,
    TimeSpan Duration,
    double PeakVolume);

public enum GuardDecision
{
    /// <summary>Сигнал: уровень заряда упал ниже порога.</summary>
    LowBattery,

    /// <summary>Заряд восстановился — снимаем глушение.</summary>
    Recovered,

    /// <summary>Ничего не изменилось.</summary>
    None
}

public sealed record GuardEvent(
    GuardDecision Decision,
    BtDeviceInfo? Device,
    AudioEndpointInfo? Endpoint,
    SoundEvent? Culprit,
    IReadOnlyList<SoundEvent> Candidates);
