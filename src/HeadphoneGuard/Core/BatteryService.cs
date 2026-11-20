using HeadphoneGuard.Bluetooth;
using HeadphoneGuard.Hid;

namespace HeadphoneGuard.Core;

/// <summary>
/// Единая точка чтения заряда: собирает данные из всех доступных источников.
///   • PnP-свойство DEVPKEY_BatteryLevel — то, что Windows знает о Bluetooth-гарнитурах;
///   • vendor-HID — то, что Windows не знает, но отдаёт сам донгл (MCHOSE, Logitech, Razer…);
///   • GATT Battery Service 0x180F — Low Energy-устройства.
/// </summary>
public sealed class BatteryService : IDisposable
{
    public sealed record DeviceBattery(string Name, int Percent, string Source);

    private readonly HidBatteryMonitor _hid = new();
    private readonly BluetoothBatteryReader _gatt = new();
    private readonly Dictionary<string, int> _current = new(StringComparer.OrdinalIgnoreCase);

    public event Action<DeviceBattery>? BatteryUpdated;

    public IReadOnlyDictionary<string, int> Current => _current;

    public void Start()
    {
        Bluetooth.DeviceNames.Refresh();

        _hid.BatteryUpdated += (name, percent) =>
            Update(new DeviceBattery(name, percent, "HID"));
        _hid.Start();
    }

    public async Task<IReadOnlyList<DeviceBattery>> PollAsync(CancellationToken ct = default)
    {
        var result = new List<DeviceBattery>();

        // 1) Реестр: заряд Bluetooth-гарнитур, который Windows публикует в BTHENUM.
        foreach (var reading in RegistryBatteryReader.Read())
            Update(new DeviceBattery(reading.Name, reading.Percent, "PnP"));

        // 2) Vendor-HID: гарнитуры и мыши с собственным протоколом.
        foreach (var (name, percent) in _hid.Batteries)
            Update(new DeviceBattery(name, percent, "HID"));

        // 3) GATT — если остальные источники молчат (BLE-устройства).
        if (_current.Count == 0)
        {
            foreach (var device in await _gatt.ReadAsync(ct).ConfigureAwait(false))
            {
                if (device.BatteryPercent is { } percent)
                    Update(new DeviceBattery(device.Name, percent, "GATT"));
            }
        }

        foreach (var (name, percent) in _current)
            result.Add(new DeviceBattery(name, percent, SourceOf(name)));

        return result;
    }

    private string SourceOf(string name) =>
        _hid.Batteries.ContainsKey(name) ? "HID" : "PnP";

    private void Update(DeviceBattery battery)
    {
        if (_current.TryGetValue(battery.Name, out int previous) && previous == battery.Percent)
            return;

        _current[battery.Name] = battery.Percent;
        Diagnostics.Log($"Заряд: {battery.Name} — {battery.Percent}% ({battery.Source})");
        BatteryUpdated?.Invoke(battery);
    }

    public void Dispose() => _hid.Dispose();
}
