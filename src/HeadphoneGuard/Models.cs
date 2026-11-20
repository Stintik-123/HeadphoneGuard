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
///
/// <see cref="IsOneShot"/> — признак одиночного оповещения: сессия жила недолго и
/// целиком. Игровые звуки приходят «всплесками» внутри долгоживущей сессии,
/// поэтому помечаются как не-one-shot и не глушатся.
/// </summary>
public sealed record SoundEvent(
    DateTime Timestamp,
    string EndpointName,
    string SessionId,
    string SessionName,
    int ProcessId,
    string ProcessName,
    TimeSpan Duration,
    double PeakVolume,
    bool IsOneShot)
{
    public string Describe() =>
        $"{ProcessName} ({SessionName}) — {Duration.TotalMilliseconds:F0} мс";
}
