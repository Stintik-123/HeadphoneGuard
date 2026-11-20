using System.Text;

namespace HeadphoneGuard.Audio;

/// <summary>
/// Связывает Bluetooth-устройство с его аудио-выходом в Windows.
/// Windows называет выход наушников по-разному («WH-1000XM4 (Stereo)»,
/// «Наушники (Hands-Free)»), поэтому сравнение идёт по нормализованным именам,
/// а ручную привязку всегда можно задать в настройках.
/// </summary>
public static class EndpointMatcher
{
    private static readonly string[] NoiseTokens =
    {
        "stereo", "hands", "free", "handsfree", "avrcp", "a2dp", "hfp", "headset",
        "headphones", "headphone", "earphones", "earphone", "earbuds", "output",
        "device", "audio", "sound", "usb", "wireless", "bluetooth", "bt",
        "microsoft", "windows", "render",
    };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            if (char.IsLetterOrDigit(c))
                sb.Append(c);

        string compact = sb.ToString();

        foreach (var token in NoiseTokens)
            compact = compact.Replace(token, string.Empty);

        return compact;
    }

    /// <summary>
    /// Оценка совпадения: 0 — совпадения нет, 100 — точное.
    /// </summary>
    public static int Score(string deviceName, string endpointName)
    {
        string a = Normalize(deviceName);
        string b = Normalize(endpointName);

        if (a.Length == 0 || b.Length == 0)
            return 0;

        if (a == b)
            return 100;

        if (b.Contains(a) || a.Contains(b))
            return 80;

        // Общий «значимый» кусок имени: модельный код (T20, WM-D2X4, RONIN...).
        var tokensA = SignificantTokens(deviceName);
        var tokensB = SignificantTokens(endpointName);

        foreach (string ta in tokensA)
        {
            if (ta.Length < 3)
                continue;
            foreach (string tb in tokensB)
                if (ta == tb)
                    return 60;
        }

        return 0;
    }

    private static IEnumerable<string> SignificantTokens(string name)
    {
        return name
            .Split(new[] { ' ', '(', ')', '-', '_', '[', ']', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 3 && !NoiseTokens.Contains(t.ToLowerInvariant()));
    }

    /// <summary>Подбирает выход для устройства; ручная привязка важнее автоматической.</summary>
    public static AudioEndpointInfo? Match(
        IReadOnlyList<AudioEndpointInfo> endpoints,
        string deviceName,
        IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(deviceName, out string? forcedId))
        {
            var forced = endpoints.FirstOrDefault(e => e.Id == forcedId);
            if (forced is not null)
                return forced;
        }

        AudioEndpointInfo? best = null;
        int bestScore = 0;

        foreach (var endpoint in endpoints)
        {
            int score = Score(deviceName, endpoint.FriendlyName);
            if (score > bestScore)
            {
                bestScore = score;
                best = endpoint;
            }
        }

        return bestScore >= 60 ? best : null;
    }
}
