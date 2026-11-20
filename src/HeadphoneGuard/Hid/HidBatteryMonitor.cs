using System.Collections.Concurrent;
using System.Globalization;

namespace HeadphoneGuard.Hid;

/// <summary>
/// Универсальный читатель заряда беспроводных устройств через vendor-HID.
///
/// Многие гарнитуры и мыши (MCHOSE, Logitech, Razer, HyperX, Corsair и другие)
/// сообщают заряд в собственном HID-протоколе. Чаще всего кадр выглядит так:
/// <c>55 65 &lt;значение&gt; 02 ...</c>, где поле 0x02 — процент заряда.
/// Такие устройства Windows не показывает в «Параметрах», поэтому читать их
/// приходится напрямую — тем же путём, что и open-source проекты
/// Wireless-Device-Battery-Monitor и HeadsetControl.
/// </summary>
public sealed class HidBatteryMonitor : IDisposable
{
    private readonly ConcurrentDictionary<string, int> _batteries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HidListener> _listeners = new();
    private readonly List<(string Path, int OutputLength)> _channels = new();
    private readonly object _gate = new();
    private Timer? _requester;

    /// <summary>Устройство -> процент заряда.</summary>
    public IReadOnlyDictionary<string, int> Batteries => _batteries;

    public event Action<string, int>? BatteryUpdated;

    public void Start()
    {
        lock (_gate)
        {
            foreach (var collection in HidInspector.Enumerate())
            {
                if (collection.UsagePage < 0xFF00 || collection.InputReportLength == 0)
                    continue;

                string key = DeviceKey(collection.VendorId, collection.ProductId);
                string name = ResolveName(collection.VendorId, collection.ProductId);
                _names[key] = name;

                var listener = new HidListener(collection.Path, collection.InputReportLength);
                listener.FrameReceived += frame => OnFrame(name, name, frame);
                listener.Start();
                _listeners.Add(listener);

                if (collection.OutputReportLength > 0)
                    _channels.Add((collection.Path, collection.OutputReportLength));

                Diagnostics.Dev($"Слушаю HID {name} [{collection.UsagePage:X4}:{collection.Usage:X4}]");
            }

            // Донглы с вендорным протоколом отвечают на запрос: подталкиваем их кадром с зарядом.
            RequestBattery();
            _requester = new Timer(_ => RequestBattery(), null, 20_000, 20_000);

            Diagnostics.Log($"Слушаю { _listeners.Count } вендорных HID-каналов.");
        }
    }

    /// <summary>Просит устройство прислать кадр с зарядом.</summary>
    private void RequestBattery()
    {
        foreach (var (path, outputLength) in _channels)
        {
            try
            {
                int length = Math.Max(8, outputLength);
                HidInspector.Send(path, MchoseBatteryProtocol.RequestFrame(length), length);
            }
            catch (Exception ex)
            {
                Diagnostics.Dev($"Не удалось запросить заряд HID: {ex.Message}");
            }
        }
    }

    private void OnFrame(string key, string name, byte[] frame)
    {
        int? battery = ParseBattery(frame);
        if (battery is not { } percent)
            return;

        if (_batteries.TryGetValue(key, out int previous) && previous == percent)
            return;

        _batteries[key] = percent;
        Diagnostics.Log($"HID-заряд: {name} — {percent}%");
        BatteryUpdated?.Invoke(name, percent);
    }

    /// <summary>
    /// Разбор вендорного кадра заряда. Поддерживаются известные форматы:
    /// 55 65 &lt;значение&gt; 02 (MCHOSE X9/V9 и совместимые), A1 &lt;значение&gt; (MCHOSE A5).
    /// </summary>
    public static int? ParseBattery(byte[] frame)
    {
        if (frame is null || frame.Length < 3)
            return null;

        if (frame[0] == 0x55 && frame[1] == 0x65 && frame.Length >= 4)
        {
            if (frame[3] == 0x02)
                return Valid(frame[2]);
            if (frame[2] == 0x02)
                return Valid(frame[3]);
            return null;
        }

        if (frame[0] == 0xA1)
            return Valid(frame[1]);

        return null;
    }

    private static int? Valid(int value) => value is >= 0 and <= 100 ? value : null;

    private static string DeviceKey(ushort vendorId, ushort productId) =>
        string.Create(CultureInfo.InvariantCulture, $"{vendorId:X4}:{productId:X4}");

    /// <summary>Ищет человеческое имя устройства по VID/PID через WinRT.</summary>
    private static string ResolveName(ushort vendorId, ushort productId)
    {
        return Bluetooth.DeviceNames.ByVidPid(vendorId, productId);
    }

    public void Dispose()
    {
        foreach (var listener in _listeners)
            listener.Dispose();
        _listeners.Clear();
    }
}
