using VidFlow.Localization;

namespace VidFlow.Services;

/// <summary>Turns raw yt-dlp stderr into a short, actionable message.</summary>
public static class ErrorMapper
{
    /// <summary>The last "ERROR:" line, or the last line if there is none.</summary>
    public static string LastErrorLine(string raw)
    {
        var lines = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? "";
    }

    public static string Friendly(string raw)
    {
        var line = LastErrorLine(raw);
        if (line.Length == 0) return L.Get("Error_UnexpectedExit");

        var hint = line switch
        {
            _ when Has(line, "Sign in to confirm") => L.Get("Error_BotCheck"),
            _ when Has(line, "Unsupported URL") => L.Get("Error_Unsupported"),
            _ when Has(line, "HTTP Error 429") || Has(line, "Too Many Requests") => L.Get("Error_RateLimited"),
            _ when IsCookieReadError(line) => L.Get("Error_Cookies"),
            _ when Has(line, "Private video") || Has(line, "members-only") || Has(line, "Join this channel") => L.Get("Error_Private"),
            _ when IsNoFormats(line) => L.Get("Error_NoFormats"),
            // Only the real "video is gone" messages; "Requested format is not available" is handled above.
            _ when Has(line, "Video unavailable") || Has(line, "video is unavailable") || Has(line, "This video is not available") || Has(line, "啥都木有")
                   || Has(line, "has been removed") => L.Get("Error_Unavailable"),
            _ when Has(line, "ffmpeg") && (Has(line, "not found") || Has(line, "not installed")) => L.Get("Error_NoFfmpeg"),
            _ when Has(line, "No space left") || Has(line, "Errno 28") => L.Get("Error_DiskFull"),
            _ when Has(line, "timed out") || Has(line, "Unable to download") || Has(line, "getaddrinfo")
                   || Has(line, "Connection reset") || Has(line, "SSL") => L.Get("Error_Network"),
            _ => null,
        };
        return hint is null ? line : $"{hint}\n{line}";
    }

    /// <summary>yt-dlp could not read the configured browser's cookie store (locked / encrypted / missing).</summary>
    public static bool IsCookieReadError(string error) =>
        Has(error, "cookie database")
        || Has(error, "Failed to decrypt")
        || Has(error, "cookies from browser")
        || (Has(error, "could not find") && Has(error, "cookies"));

    /// <summary>The site answered but offered nothing downloadable (on YouTube typically a sign-in / cookie problem).</summary>
    public static bool IsNoFormats(string error) =>
        Has(error, "Requested format is not available") || Has(error, "No video formats found");

    /// <summary>YouTube refuses without a signed-in account (age-restricted, members-only, private, bot check).</summary>
    public static bool NeedsSignIn(string error) =>
        Has(error, "Sign in to confirm")
        || Has(error, "age-restricted") || Has(error, "inappropriate for some users")
        || Has(error, "members-only") || Has(error, "Join this channel") || Has(error, "available to this channel's members")
        || Has(error, "Private video")
        || Has(error, "use --cookies");

    static bool Has(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);
}
