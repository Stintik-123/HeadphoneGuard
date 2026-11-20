using System.Diagnostics;
using System.IO;

namespace HeadphoneGuard;

public static class Diagnostics
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();
    private static string? _logPath;

    public static bool Verbose { get; set; }

    /// <summary>Путь файла журнала (по умолчанию — в профиле пользователя).</summary>
    public static string LogPath
    {
        get => _logPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HeadphoneGuard",
            "headphoneguard.log");
        set => _logPath = value;
    }

    public static void Log(string message)
    {
        lock (Gate)
        {
            string line = $"{DateTime.Now:HH:mm:ss} {message}";
            Lines.Add(line);
            if (Lines.Count > 500)
                Lines.RemoveRange(0, 100);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Журнал не критичен.
            }
        }

        System.Diagnostics.Debug.WriteLine(message);
    }

    public static void Dev(string message)
    {
        if (Verbose)
            Log($"[dbg] {message}");
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
            return Lines.ToArray();
    }
}
