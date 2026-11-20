using Microsoft.Win32;

namespace HeadphoneGuard.Bluetooth;

/// <summary>
/// Чтение заряда Bluetooth-устройств из реестра Windows.
///
/// Windows публикует заряд классических гарнитур в свойстве
/// DEVPKEY_Bluetooth_BatteryLevel ({104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2)
/// и кэширует его в ветке устройства:
/// HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM\{...}\{...}\Properties\{104EA319-...}\2
///
/// Там же лежат имя устройства и Bluetooth-адрес.
/// </summary>
public static class RegistryBatteryReader
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\BTHENUM";
    private const string BatteryKey = @"{104EA319-6EE2-4701-BD47-8DDBF425BBE5}\2";
    private const string AddressKey = @"{2BD67D8B-8BEB-48D5-87E0-6CDA3428040A}\1";
    private const string ConnectedKey = @"{83DA6326-97A6-4088-9453-A1923F573B29}\15";

    public sealed record Reading(string Name, string Address, int Percent, bool? Connected);

    public static IReadOnlyList<Reading> Read()
    {
        var result = new List<Reading>();

        using var root = Registry.LocalMachine.OpenSubKey(EnumRoot);
        if (root is null)
            return result;

        foreach (string serviceKey in root.GetSubKeyNames())
        {
            using var service = root.OpenSubKey(serviceKey);
            if (service is null)
                continue;

            foreach (string instanceKey in service.GetSubKeyNames())
            {
                using var instance = service.OpenSubKey(instanceKey);
                if (instance is null)
                    continue;

                int? battery = ReadInt(instance, @"Properties\" + BatteryKey);
                if (battery is not { } percent || percent is < 0 or > 100)
                    continue;

                string name = ReadString(instance, "FriendlyName")
                    ?? ReadString(instance, "DeviceDesc")
                    ?? serviceKey;

                string address = ReadString(instance, @"Properties\" + AddressKey) ?? string.Empty;
                bool? connected = ReadInt(instance, @"Properties\" + ConnectedKey) is { } c ? c != 0 : null;

                result.Add(new Reading(CleanName(name), address, percent, connected));
            }
        }

        return result;
    }

    private static string CleanName(string name)
    {
        // DeviceDesc часто выглядит как "@%SystemRoot%\inf\oem12.inf,%X%;MCHOSE V9 PRO".
        int semicolon = name.LastIndexOf(';');
        if (semicolon >= 0 && semicolon < name.Length - 1)
            name = name[(semicolon + 1)..];

        int percent = name.IndexOf('%');
        if (percent == 0)
            name = name[(name.IndexOf(';') + 1)..];

        return name.Trim();
    }

    private static int? ReadInt(RegistryKey key, string path)
    {
        try
        {
            using var sub = key.OpenSubKey(path);
            if (sub is null)
                return null;

            object? value = sub.GetValue(null);
            return value switch
            {
                int i => i,
                uint u => (int)u,
                byte b => b,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(RegistryKey key, string path)
    {
        try
        {
            using var sub = key.OpenSubKey(path);
            return sub?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }
}
