using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeadphoneGuard.Core;

public sealed class Settings
{
    /// <summary>Порог «скоро разрядится», в процентах.</summary>
    public int BatteryThreshold { get; set; } = 10;

    /// <summary>Как часто опрашивать заряд, секунд.</summary>
    public int BatteryPollSeconds { get; set; } = 30;

    /// <summary>Максимальная длительность «короткого» звука, мс. Всё длиннее — считается игрой/музыкой.</summary>
    public int ShortSoundMaxMs { get; set; } = 4000;

    /// <summary>Процессы, которые никогда не заглушать (например, облачный клиент игры).</summary>
    public List<string> ProtectedProcesses { get; set; } = new();

    /// <summary>Процессы, которые гасим всегда (запомненные «нарушители»).</summary>
    public List<string> MutedProcesses { get; set; } = new();

    /// <summary>Ручная привязка: имя BT-устройства -&gt; ID аудио-выхода.</summary>
    public Dictionary<string, string> EndpointOverrides { get; set; } = new();

    /// <summary>Показывать уведомление Windows при срабатывании.</summary>
    public bool ShowNotification { get; set; } = true;

    /// <summary>Запускать вместе с Windows.</summary>
    public bool StartWithWindows { get; set; }

    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HeadphoneGuard",
        "settings.json");

    public static Settings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Settings>(json, Options);
                if (loaded is not null)
                {
                    loaded.FilePath = path;
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Не удалось прочитать настройки: {ex.Message}");
        }

        return new Settings { FilePath = path };
    }

    public void Save()
    {
        try
        {
            var path = string.IsNullOrEmpty(FilePath) ? DefaultPath : FilePath;
            FilePath = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Не удалось сохранить настройки: {ex.Message}");
        }
    }
}
