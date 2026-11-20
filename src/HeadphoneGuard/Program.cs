using System.IO;
using System.Text;
using HeadphoneGuard.App;
using HeadphoneGuard.Audio;
using HeadphoneGuard.Bluetooth;
using HeadphoneGuard.Core;

namespace HeadphoneGuard;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase) || a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
            AttachParentConsole();

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // Консоли нет (запуск из трея) — не страшно.
        }

        if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
        {
            PrintHelp();
            return 0;
        }

        if (args.Any(a => a.Equals("--sessions", StringComparison.OrdinalIgnoreCase)))
            return RunSessionsProbe();

        if (args.Any(a => a.Equals("--hid", StringComparison.OrdinalIgnoreCase)))
            return RunHidProbe(args);

        if (args.Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase) || a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
            return RunDiagnose(args).GetAwaiter().GetResult();

        var settings = Settings.Load();
        Diagnostics.Verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));

        using var engine = new GuardEngine(settings);
        engine.Start();

        var tray = new TrayApp(settings, engine);
        tray.Run();
        return 0;
    }

    private static void AttachParentConsole()
    {
        try
        {
            if (!Console.IsOutputRedirected)
                return;

            if (!NativeMethods.AttachConsole(NativeMethods.AttachParentProcess))
                return;

            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
        }
        catch
        {
            // Остаёмся без консоли — не критично.
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("HeadphoneGuard — тихий режим для разряжающихся наушников");
        Console.WriteLine();
        Console.WriteLine("  HeadphoneGuard              запуск в фоне (иконка в трее)");
        Console.WriteLine("  HeadphoneGuard --diagnose   показать заряды, выходы и источники звука");
        Console.WriteLine("  HeadphoneGuard --verbose    подробный лог");
    }

    private static int RunHidProbe(string[] args)
    {
        AttachParentConsole();

        Console.WriteLine("=== HID-коллекции в системе ===");
        var collections = Hid.HidInspector.Enumerate();
        foreach (var c in collections)
        {
            Console.WriteLine($"  VID_{c.VendorId:X4}:PID_{c.ProductId:X4}  usage {c.UsagePage:X4}:{c.Usage:X4}  " +
                              $"feature={c.FeatureReportLength} in={c.InputReportLength} out={c.OutputReportLength}  {c.Description}");
            Console.WriteLine($"      {c.Path}");
        }

        Console.WriteLine();
        Console.WriteLine("=== Попытки опроса заряда (vendor-протоколы) ===");
        foreach (var c in collections.Where(c => c.UsagePage >= 0xFF00))
        {
            foreach (var strategy in Hid.MchoseBatteryProtocol.Strategies)
            {
                byte[]? reply = strategy.Transport == Hid.MchoseBatteryProtocol.Transport.Output
                    ? Hid.HidInspector.Exchange(c.Path, strategy.Payload)
                    : Hid.HidInspector.Query(c.Path, strategy.Payload, 64, strategy.ReportId);

                if (reply is null)
                    continue;

                int? battery = Hid.MchoseBatteryProtocol.ParseBattery(reply);
                Console.WriteLine($"  {c.Description} [{c.UsagePage:X4}:{c.Usage:X4}] {strategy.Name}: " +
                                  string.Join(' ', reply.Take(10).Select(b => b.ToString("X2"))) +
                                  (battery is { } b ? $"  -> заряд {b}%" : string.Empty));
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Подробный обмен с вендорными коллекциями ===");
        foreach (var c in collections.Where(c => c.VendorId == 0x291D))
        {
            Console.WriteLine($"  {c.Description} [{c.UsagePage:X4}:{c.Usage:X4}] in={c.InputReportLength} out={c.OutputReportLength} feat={c.FeatureReportLength}");
            Console.Write(Hid.HidInspector.DebugExchange(c.Path, new byte[] { 0x55, 0x65, 0x01, 0x00 }));
        }

        Console.WriteLine();
        Console.WriteLine("=== Узлы дерева PnP (совпадения по VID/PID) ===");
        var named = Bluetooth.DeviceTree.EnumerateNamedNodes();
        Console.WriteLine($"  всего узлов с именем: {named.Count}");
        foreach (var node in named.Where(n => n.InstanceId.Contains("291D", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  {node.FriendlyName}  <-  {node.InstanceId}");

        return 0;
    }

    private static int RunLiveHidFrames(string[] args)
    {
        AttachParentConsole();
        var collections = Hid.HidInspector.Enumerate();

        Console.WriteLine("=== Живые кадры от HID-коллекций ===");
        var listeners = new List<Hid.HidListener>();
        foreach (var c in collections.Where(c => c.UsagePage >= 0xFF00))
        {
            var listener = new Hid.HidListener(c.Path, Math.Max(c.InputReportLength, 8));
            listener.FrameReceived += frame =>
                Console.WriteLine($"  [{c.UsagePage:X4}:{c.Usage:X4}] {string.Join(' ', frame.Take(16).Select(b => b.ToString("X2")))}");
            listener.Start();
            listeners.Add(listener);
        }

        Thread.Sleep(10_000);

        return 0;
    }

    private static int RunSessionsProbe()
    {
        AttachParentConsole();

        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).ToList();

        var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        Console.WriteLine($"По умолчанию: {defaultEndpoint.FriendlyName}");

        var managers = devices.Select(d => (d.FriendlyName, d.AudioSessionManager)).ToList();

        for (int tick = 0; tick < 20; tick++)
        {
            if (tick == 5)
            {
                Console.WriteLine(">>> проигрываю системный звук");
                System.Media.SystemSounds.Exclamation.Play();
            }

            foreach (var (name, manager) in managers)
            {
                manager.RefreshSessions();
                var sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    string state;
                    double peak = 0;
                    try
                    {
                        state = session.State.ToString();
                        peak = session.AudioMeterInformation.MasterPeakValue;
                    }
                    catch (Exception ex)
                    {
                        state = "err:" + ex.GetType().Name;
                    }

                    if (tick == 5 || state.Contains("Active") || peak > 0.001)
                        Console.WriteLine($"  [{tick:00}] {name} :: {session.DisplayName} state={state} peak={peak:F4} pid={session.GetProcessID}");
                }
            }

            Thread.Sleep(250);
        }

        return 0;
    }

    private static async Task<int> RunDiagnose(string[] args)
    {
        Diagnostics.Verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));

        int seconds = ReadIntOption(args, "--seconds", 2);
        bool beep = args.Any(a => a.Equals("--beep", StringComparison.OrdinalIgnoreCase));

        if (beep)
        {
            try
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Не удалось проиграть тестовый звук: " + ex.Message);
            }
        }

        Console.WriteLine("=== Bluetooth-устройства с зарядом (PnP-дерево) ===");
        var nodes = DeviceTree.ReadBluetoothBatteryNodes();
        if (nodes.Count == 0)
            Console.WriteLine("  (не найдено узлов с DEVPKEY_Bluetooth_BatteryLevel)");
        foreach (var node in nodes)
        {
            Console.WriteLine($"  {node.FriendlyName} [{node.Address}] — {node.BatteryPercent}% " +
                              $"(подключено: {(node.Connected?.ToString() ?? "?" )})");
        }

        Console.WriteLine();
        Console.WriteLine("=== Заряд беспроводных устройств (универсальный источник) ===");
        var batteryService = new Core.BatteryService();
        batteryService.Start();
        await Task.Delay(8000);
        foreach (var reading in await batteryService.PollAsync())
            Console.WriteLine($"  {reading.Name} — {reading.Percent}% (источник: {reading.Source})");
        batteryService.Dispose();

        Console.WriteLine();
        Console.WriteLine("=== Аудио-выходы (render) ===");
        var catalog = new AudioCatalog();
        var endpoints = catalog.ListActiveRenderEndpoints();
        foreach (var endpoint in endpoints)
            Console.WriteLine($"  {endpoint.FriendlyName}\n      id: {endpoint.Id}");

        Console.WriteLine();
        Console.WriteLine("=== Живые аудиосессии (кто сейчас играет) ===");
        using var tracker = new SoundSourceTracker();
        tracker.Start();
        Thread.Sleep(seconds * 1000);

        foreach (var manager in new[] { tracker })
        {
            var events = tracker.RecentEvents;
            if (events.Count == 0)
                Console.WriteLine("  (тишина)");
            foreach (var evt in events)
                Console.WriteLine($"  {evt.Timestamp:HH:mm:ss} {evt.ProcessName} ({evt.SessionName}) — {evt.Duration.TotalMilliseconds:F0} мс");
        }

        Console.WriteLine();
        Console.WriteLine("=== Журнал ===");
        foreach (var line in Diagnostics.Snapshot())
            Console.WriteLine("  " + line);

        return 0;
    }

    private static int ReadIntOption(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out int value))
                return value;
        }

        return fallback;
    }
}
