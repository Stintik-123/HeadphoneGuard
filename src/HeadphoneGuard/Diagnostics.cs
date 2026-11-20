using System.Diagnostics;

namespace HeadphoneGuard;

public static class Diagnostics
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();

    public static bool Verbose { get; set; }

    public static void Log(string message)
    {
        lock (Gate)
        {
            Lines.Add($"{DateTime.Now:HH:mm:ss} {message}");
            if (Lines.Count > 500)
                Lines.RemoveRange(0, 100);
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
