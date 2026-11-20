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

        if (args.Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase) || a.Equals("--diag", StringComparison.OrdinalIgnoreCase)))
            return RunDiagnose(args);

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

    private static int RunDiagnose(string[] args)
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
