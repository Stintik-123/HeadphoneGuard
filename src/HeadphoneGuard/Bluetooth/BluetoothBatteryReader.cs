using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace HeadphoneGuard.Bluetooth;

/// <summary>
/// Читает уровень заряда Bluetooth-устройств через стандартный GATT Battery Service
/// (0x180F / Battery Level 0x2A19) — тем же путём, которым Windows формирует
/// собственное уведомление «устройство разряжается».
///
/// Дополнительно сканируется DeviceInformation: некоторые драйверы кладут заряд
/// в свойства устройства (ключи с подстрокой «Battery»).
/// </summary>
public sealed class BluetoothBatteryReader
{
    private static readonly Guid BatteryServiceUuid = new("0000180f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid BatteryLevelUuid = new("00002a19-0000-1000-8000-00805f9b34fb");

    /// <summary>Все сопряжённые Bluetooth-устройства с известным (по возможности) зарядом.</summary>
    public async Task<IReadOnlyList<BtDeviceInfo>> ReadAsync(CancellationToken ct = default)
    {
        var result = new List<BtDeviceInfo>();

        string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector).AsTask(ct).ConfigureAwait(false);

        foreach (var di in devices)
        {
            ct.ThrowIfCancellationRequested();

            string name = di.Name ?? "Unknown device";
            string address = string.Empty;
            int? percent = null;
            bool connected = false;

            try
            {
                var btDevice = await BluetoothDevice.FromIdAsync(di.Id).AsTask(ct).ConfigureAwait(false);
                if (btDevice is not null)
                {
                    connected = btDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    address = FormatAddress(btDevice.BluetoothAddress);
                    name = btDevice.Name ?? name;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Dev($"BluetoothDevice.FromIdAsync failed for {di.Id}: {ex.Message}");
            }

            // 1) Свойства DeviceInformation (драйвер может отдавать заряд прямо там).
            percent ??= FindBatteryProperty(di);

            // 2) GATT Battery Service (основной путь для LE-устройств).
            if (percent is null && address.Length > 0)
                percent = await ReadGattBatteryAsync(address, ct).ConfigureAwait(false);

            if (percent is null)
                Diagnostics.Dev($"Battery level unknown for '{name}' ({address})");

            result.Add(new BtDeviceInfo(di.Id, name, address, percent, connected));
        }

        return result;
    }

    private static int? FindBatteryProperty(DeviceInformation info)
    {
        try
        {
            foreach (var prop in info.Properties)
            {
                string key = prop.Key;
                if (key is null || !key.Contains("Battery", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (prop.Value is int i && i is >= 0 and <= 100)
                    return i;
                if (prop.Value is uint u && u <= 100)
                    return (int)u;
                if (prop.Value is double d && d is >= 0 and <= 100)
                    return (int)d;
                if (prop.Value is string s && int.TryParse(s, out int parsed) && parsed is >= 0 and <= 100)
                    return parsed;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Dev($"Property scan failed: {ex.Message}");
        }

        return null;
    }

    private static async Task<int?> ReadGattBatteryAsync(string addressHex, CancellationToken ct)
    {
        try
        {
            if (!ulong.TryParse(addressHex, out ulong address))
                return null;

            var leDevice = await BluetoothLEDevice
                .FromBluetoothAddressAsync(address)
                .AsTask(ct)
                .ConfigureAwait(false);

            if (leDevice is null)
                return null;

            var services = await leDevice
                .GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask(ct)
                .ConfigureAwait(false);

            GattDeviceService? batteryService = null;
            foreach (var candidate in services.Services)
            {
                if (candidate.Uuid.Equals(BatteryServiceUuid))
                {
                    batteryService = candidate;
                    break;
                }
            }

            if (batteryService is null)
                return null;

            var chars = await batteryService
                .GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                .AsTask(ct)
                .ConfigureAwait(false);

            GattCharacteristic? level = null;
            foreach (var candidate in chars.Characteristics)
            {
                if (candidate.Uuid.Equals(BatteryLevelUuid))
                {
                    level = candidate;
                    break;
                }
            }

            if (level is null)
                return null;

            var read = await level.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
            var bytes = read?.Value is { } buffer ? buffer.ToArray() : null;

            if (bytes is null || bytes.Length == 0)
                return null;

            int raw = bytes[0];
            return raw is >= 0 and <= 100 ? raw : null;
        }
        catch (Exception ex)
        {
            Diagnostics.Dev($"GATT battery read failed for {addressHex}: {ex.Message}");
            return null;
        }
    }

    private static string FormatAddress(ulong address)
    {
        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
            bytes[i] = (byte)((address >> (8 * i)) & 0xFF);
        Array.Reverse(bytes);
        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }
}
