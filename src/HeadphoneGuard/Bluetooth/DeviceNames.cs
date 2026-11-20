using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Devices.Enumeration;

namespace HeadphoneGuard.Bluetooth;

/// <summary>
/// Имена устройств через WinRT: сопоставляет VID/PID с человеческим названием
/// («MCHOSE V9 PRO» вместо «291D:385D»).
/// </summary>
public static class DeviceNames
{
    private static readonly Regex VidPid = new("VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Кэш «VID_xxxx&amp;PID_yyyy -&gt; имя устройства».</summary>
    public static IReadOnlyDictionary<string, string> Map
    {
        get
        {
            if (Cache.Count == 0)
                Refresh();
            return Cache;
        }
    }

    public static void Refresh()
    {
        try
        {
            var devices = DeviceInformation
                .FindAllAsync(null, null, DeviceInformationKind.Device)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            lock (Cache)
            {
                Cache.Clear();
                var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var device in devices)
                {
                    if (string.IsNullOrWhiteSpace(device.Name))
                        continue;

                    var match = VidPid.Match(device.Id);
                    if (!match.Success)
                        continue;

                    string key = $"{match.Groups[1].Value.ToUpperInvariant()}:{match.Groups[2].Value.ToUpperInvariant()}";
                    int score = Score(device.Name);

                    // Из одного VID/PID бывает несколько узлов (коллекции HID, звук, USB).
                    // Нам нужна самая «человеческая» подпись.
                    if (!scores.TryGetValue(key, out int best) || score > best)
                    {
                        scores[key] = score;
                        Cache[key] = device.Name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Dev($"Не удалось получить имена устройств: {ex.Message}");
        }
    }

    /// <summary>Человеческое имя устройства по VID/PID либо техническое «VID:PID».</summary>
    public static string ByVidPid(ushort vendorId, ushort productId)
    {
        string key = $"{vendorId:X4}:{productId:X4}";
        return Map.TryGetValue(key, out var name) ? name : $"USB {key}";
    }

    /// <summary>Оценка «человечности» названия: системные имена получают 0.</summary>
    private static int Score(string name)
    {
        string lower = name.ToLowerInvariant();

        string[] generic =
        {
            "hid-", "usb-устройство ввода", "составное usb", "устройство интерфейса",
            "unknown", "generic", "vendor-defined", "определенное поставщиком",
            "устройство управления", "совместимый телефон",
        };

        foreach (string token in generic)
            if (lower.Contains(token))
                return 0;

        return 1 + Math.Min(name.Length, 40);
    }
}
