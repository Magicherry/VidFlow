namespace VidFlow.Services;

/// <summary>
/// Maps the yt-dlp <c>--sub-langs</c> setting to a short list of common languages the user can tick, plus any other
/// codes kept as typed. A language counts as chosen when any of its codes is present.
/// </summary>
public static class SubtitleLanguageSet
{
    /// <summary>Key, name in its own language, and the codes written for it (sites use different ones).</summary>
    public sealed record Language(string Key, string NativeName, string[] Codes);

    public static readonly IReadOnlyList<Language> Common =
    [
        // Bilibili's AI-generated subtitles are "ai-zh".
        new("zh-Hans", "简体中文", ["zh-Hans", "zh-CN", "zh", "ai-zh"]),
        new("zh-Hant", "繁體中文", ["zh-Hant", "zh-TW", "zh-HK"]),
        new("en", "English", ["en.*"]),
        new("ja", "日本語", ["ja.*"]),
        new("ko", "한국어", ["ko.*"]),
    ];

    /// <summary>Used by yt-dlp when nothing is chosen.</summary>
    public const string Fallback = "zh.*,en.*";

    /// <summary>Splits a setting into the ticked common languages and the remaining codes.</summary>
    public static (HashSet<string> Chosen, string Other) Split(string setting)
    {
        var chosen = new HashSet<string>();
        var other = new List<string>();
        foreach (var code in Codes(setting))
        {
            if (Common.FirstOrDefault(l => Matches(l, code)) is { } lang) chosen.Add(lang.Key);
            else if (!other.Contains(code, StringComparer.OrdinalIgnoreCase)) other.Add(code);
        }
        return (chosen, string.Join(", ", other));
    }

    /// <summary>The setting for the ticked languages plus the other codes (comma or space separated).</summary>
    public static string Join(IEnumerable<string> chosen, string other)
    {
        var keys = chosen.ToHashSet();
        var codes = Common.Where(l => keys.Contains(l.Key)).SelectMany(l => l.Codes).ToList();
        foreach (var code in Codes(other))
            if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase)) codes.Add(code);
        return string.Join(',', codes);
    }

    /// <summary>"简体中文, English" for a summary line; empty when nothing is chosen.</summary>
    public static string Describe(string setting)
    {
        var (chosen, other) = Split(setting);
        var names = Common.Where(l => chosen.Contains(l.Key)).Select(l => l.NativeName).ToList();
        if (other.Length > 0) names.Add(other);
        return string.Join(", ", names);
    }

    static IEnumerable<string> Codes(string text) =>
        text.Split([',', ' ', '，', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>"en", "en-US" and "en.*" all belong to English; "zh-CN" belongs to Simplified Chinese.</summary>
    static bool Matches(Language lang, string code)
    {
        foreach (var c in lang.Codes)
        {
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return true;
            if (c.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = c[..^2];
                if (string.Equals(code, prefix, StringComparison.OrdinalIgnoreCase)
                    || code.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}
