using System.Text;
using System.Text.RegularExpressions;

namespace VidFlow.Services;

/// <summary>
/// File naming and folder classification rules made of variables such as {title} and {date}, translated into a
/// yt-dlp output template. The values are filled in by yt-dlp (and sanitized by it); literal text is cleaned here.
/// </summary>
public static partial class NamingTemplate
{
    public const string DefaultFileName = "{title} [{id}]";

    /// <summary>File name presets (shown in the template editor); anything else is a custom pattern.</summary>
    public static readonly string[] FilePresets = [DefaultFileName, "{title}", "{uploader} - {title}", "{date} {title}", "{title} ({resolution})"];

    /// <summary>Variables used in the pattern that VidFlow doesn't know (they would be kept as literal text).</summary>
    public static IReadOnlyList<string> UnknownVariables(string template) =>
        VariableRegex().Matches(template).Select(m => m.Groups[1].Value).Where(v => !Variables.Contains(v)).Distinct().ToList();

    /// <summary>Folder classification presets (shown in the template editor).</summary>
    public static readonly string[] FolderPresets = ["", "{platform}", "{uploader}", "{year}-{month}", "{platform}/{uploader}"];

    /// <summary>Variable → yt-dlp field. {platform} is substituted before (it is known from the URL).</summary>
    static readonly Dictionary<string, string> Fields = new()
    {
        ["title"] = "%(title).150B",
        ["uploader"] = "%(uploader,channel,creator|Unknown)s",
        ["date"] = "%(upload_date>%Y-%m-%d|unknown)s",
        ["year"] = "%(upload_date>%Y|unknown)s",
        ["month"] = "%(upload_date>%m|00)s",
        ["resolution"] = "%(height&{}p|audio)s",
        ["id"] = "%(id)s",
    };

    public static IReadOnlyList<string> Variables => ["title", "uploader", "date", "year", "month", "resolution", "id", "platform"];

    [GeneratedRegex(@"\{([a-z]+)\}")]
    private static partial Regex VariableRegex();

    /// <summary>The yt-dlp "-o" value: optional folders, then the file name, then the extension.
    /// <paramref name="known"/> supplies values VidFlow already has (when yt-dlp is only given a bare media
    /// address and wouldn't know the title or uploader); the rest are left to yt-dlp.</summary>
    public static string ToOutputTemplate(string fileTemplate, string folderTemplate, string platformName,
        IReadOnlyDictionary<string, string?>? known = null)
    {
        var file = Translate(string.IsNullOrWhiteSpace(fileTemplate) ? DefaultFileName : fileTemplate, platformName, false, known);
        if (file.Trim().Length == 0) file = Translate(DefaultFileName, platformName, false, known);
        var folder = string.IsNullOrWhiteSpace(folderTemplate) ? "" : Translate(folderTemplate, platformName, true, known).Trim('/');
        return (folder.Length > 0 ? folder + "/" : "") + file + ".%(ext)s";
    }

    static string Translate(string template, string platformName, bool allowFolders, IReadOnlyDictionary<string, string?>? known)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in VariableRegex().Matches(template))
        {
            sb.Append(Literal(template[last..m.Index], allowFolders));
            var name = m.Groups[1].Value;
            sb.Append(name == "platform" ? Literal(platformName, false)
                : known is not null && known.TryGetValue(name, out var value) ? Literal(Clip(value ?? "", 150), false)
                : Fields.TryGetValue(name, out var field) ? field
                : Literal(m.Value, allowFolders)); // unknown variable: keep as text
            last = m.Index + m.Length;
        }
        sb.Append(Literal(template[last..], allowFolders));
        return sb.ToString();
    }

    /// <summary>At most <paramref name="max"/> characters (Douyin captions can be long), on one line.</summary>
    static string Clip(string s, int max)
    {
        s = string.Join(' ', s.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }

    /// <summary>Literal text: no characters Windows forbids in file names, and "%" escaped for yt-dlp.</summary>
    static string Literal(string text, bool allowFolders)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '/' or '\\') sb.Append(allowFolders ? '/' : '_');
            else if (c is '<' or '>' or ':' or '"' or '|' or '?' or '*' || char.IsControl(c)) sb.Append('_');
            else if (c == '%') sb.Append("%%");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Example result for the editor, e.g. "YouTube\Blender\Big Buck Bunny [aqz-KE-bpKQ].mp4".</summary>
    public static string Preview(string fileTemplate, string folderTemplate, string ext = "mp4")
    {
        var sample = new Dictionary<string, string>
        {
            ["title"] = "Big Buck Bunny",
            ["uploader"] = "Blender",
            ["date"] = "2014-11-10",
            ["year"] = "2014",
            ["month"] = "11",
            ["resolution"] = "1080p",
            ["id"] = "aqz-KE-bpKQ",
            ["platform"] = "YouTube",
        };
        string Fill(string t, bool folders) => VariableRegex().Replace(t, m =>
            sample.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value).Replace('/', folders ? '\\' : '_');
        var file = Fill(string.IsNullOrWhiteSpace(fileTemplate) ? DefaultFileName : fileTemplate, false);
        var folder = string.IsNullOrWhiteSpace(folderTemplate) ? "" : Fill(folderTemplate, true).Trim('\\') + "\\";
        return $"{folder}{file}.{ext}";
    }
}
