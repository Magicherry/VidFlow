using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VidFlow.Localization;
using VidFlow.Models;

namespace VidFlow.Services;

/// <summary>One timed line of a subtitle file.</summary>
public sealed record SubtitleCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>A subtitle file yt-dlp saved: its language code and where it is.</summary>
public sealed record SavedSubtitle(string Language, string Path);

/// <summary>
/// Subtitles-only downloads: the formats on offer, what yt-dlp is asked for, and turning a subtitle file into plain
/// text or Markdown (which no tool does: yt-dlp and ffmpeg only convert between timed formats).
/// </summary>
public static partial class SubtitleFiles
{
    /// <summary>Timed formats ffmpeg converts to, then text-only ones VidFlow writes itself.</summary>
    public static readonly string[] Formats = ["srt", "vtt", "ass", "lrc", "txt", "md"];

    public const string DefaultFormat = "srt";

    /// <summary>Prefix of a <see cref="DownloadRequest.SubtitleLanguage"/> that names an auto-generated track.</summary>
    public const string AutoPrefix = "auto:";

    /// <summary>YouTube's auto-generated track in the video's own language is "xx-orig"; the other automatic ones are
    /// machine translations of it.</summary>
    public const string AutoOriginal = AutoPrefix + ".*-orig";

    public static bool IsText(string format) => format is "txt" or "md";

    public static string Normalize(string? format) => Formats.Contains(format) ? format! : DefaultFormat;

    public static string DisplayName(string format) => format switch
    {
        "vtt" => "WebVTT",
        "txt" => L.Get("SubFormat_Txt"),
        "md" => L.Get("SubFormat_Md"),
        _ => format.ToUpperInvariant(),
    };

    /// <summary>Short name for labels: "SRT", "TXT", "Markdown".</summary>
    public static string ShortName(string format) => format switch
    {
        "vtt" => "WebVTT",
        "md" => "Markdown",
        _ => format.ToUpperInvariant(),
    };

    /// <summary>yt-dlp's --sub-langs value, and whether automatic captions are wanted. No choice means the languages
    /// in Settings, in the uploader's own subtitles only: automatic ones there would be machine translations.</summary>
    public static (string Languages, bool Automatic) Languages(string choice, AppSettings s)
    {
        if (choice.StartsWith(AutoPrefix, StringComparison.Ordinal)) return (choice[AutoPrefix.Length..], true);
        if (choice.Length > 0) return (choice, false);
        return (string.IsNullOrWhiteSpace(s.SubtitleLanguages) ? SubtitleLanguageSet.Fallback : s.SubtitleLanguages.Trim(), false);
    }

    /// <summary>Printed once yt-dlp is done: the title, link and the subtitles it saved (with their paths).</summary>
    public const string ResultTemplate = "VFSUB|%(.{title,webpage_url,requested_subtitles})j";

    /// <summary>The subtitles listed in a <see cref="ResultTemplate"/> line that are really on disk. A converted file
    /// is looked for under the new extension too, in case the path printed is the one before converting.</summary>
    public static (List<SavedSubtitle> Files, string? Title, string? Url) Saved(string? json, string extension, string? folder = null)
    {
        var files = new List<SavedSubtitle>();
        if (string.IsNullOrWhiteSpace(json)) return (files, null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var url = root.TryGetProperty("webpage_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (root.TryGetProperty("requested_subtitles", out var subs) && subs.ValueKind == JsonValueKind.Object)
                foreach (var sub in subs.EnumerateObject())
                {
                    if (!sub.Value.TryGetProperty("filepath", out var fp) || fp.GetString() is not { Length: > 0 } path) continue;
                    // Absolute when yt-dlp is given -P, as VidFlow does; relative to the folder otherwise.
                    if (folder is not null) path = System.IO.Path.GetFullPath(path, folder);
                    if (!File.Exists(path) && System.IO.Path.ChangeExtension(path, extension) is var converted && File.Exists(converted)) path = converted;
                    if (File.Exists(path)) files.Add(new(sub.Name, path));
                }
            return (files, title, url);
        }
        catch (JsonException ex)
        {
            Log.Info($"Subtitle list unreadable: {ex.Message}");
            return (files, null, null);
        }
    }

    /// <summary>Writes the text version next to <paramref name="source"/> and removes the timed file it came from.</summary>
    public static string WriteText(SavedSubtitle source, string format, string? title, string? url)
    {
        var cues = Parse(File.ReadAllText(source.Path));
        var path = System.IO.Path.ChangeExtension(source.Path, format);
        File.WriteAllText(path, format == "md"
            ? ToMarkdown(cues, title ?? System.IO.Path.GetFileNameWithoutExtension(source.Path), url, source.Language)
            : ToText(cues), new UTF8Encoding(false));
        if (!string.Equals(path, source.Path, StringComparison.OrdinalIgnoreCase)) File.Delete(source.Path);
        return path;
    }

    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})")]
    private static partial Regex Time();

    [GeneratedRegex(@"<[^>]*>|\{\\[^}]*\}")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\n[ \t]*\n")]
    private static partial Regex BlankLine();

    /// <summary>The cues of an SRT or WebVTT file, markup removed. Lines repeated from the cue before are dropped:
    /// YouTube's automatic captions roll, each cue repeating the last line of the one before.</summary>
    public static List<SubtitleCue> Parse(string content)
    {
        var cues = new List<SubtitleCue>();
        var recent = new Queue<string>();
        foreach (var block in BlankLine().Split(content.Replace("\r\n", "\n").Replace('\r', '\n')))
        {
            var lines = block.Split('\n');
            var at = Array.FindIndex(lines, l => l.Contains("-->"));
            if (at < 0) continue; // header, numbers, NOTE / STYLE blocks
            var times = Time().Matches(lines[at]);
            if (times.Count < 2) continue;
            var text = new List<string>();
            foreach (var raw in lines.Skip(at + 1))
            {
                var line = WebUtility.HtmlDecode(Markup().Replace(raw, "")).Replace(' ', ' ').Trim();
                if (line.Length == 0 || recent.Contains(line)) continue;
                text.Add(line);
                recent.Enqueue(line);
                if (recent.Count > 3) recent.Dequeue();
            }
            if (text.Count > 0) cues.Add(new(ToTime(times[0]), ToTime(times[1]), string.Join("\n", text)));
        }
        return cues;
    }

    static TimeSpan ToTime(Match m) => new TimeSpan(0,
        m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
        int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
        int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
        int.Parse(m.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture));

    /// <summary>The words alone, in paragraphs.</summary>
    public static string ToText(IReadOnlyList<SubtitleCue> cues) =>
        string.Join("\n\n", Paragraphs(cues).Select(p => p.Text)) + "\n";

    /// <summary>A heading, where it came from, then paragraphs each starting with its time.</summary>
    public static string ToMarkdown(IReadOnlyList<SubtitleCue> cues, string title, string? url, string language)
    {
        var md = new StringBuilder();
        md.Append("# ").Append(title.Trim()).Append("\n\n");
        if (url is { Length: > 0 }) md.Append("- ").Append(L.Get("Subtitles_MdSource")).Append(": <").Append(url).Append(">\n");
        md.Append("- ").Append(L.Get("Subtitles_MdLanguage")).Append(": ").Append(LanguageName(language)).Append("\n\n");
        foreach (var (start, text) in Paragraphs(cues))
            md.Append("**").Append(Stamp(start)).Append("** ").Append(EscapeStart(text)).Append("\n\n");
        return md.ToString().TrimEnd() + "\n";
    }

    /// <summary>"01:02:03", or "02:03" under an hour.</summary>
    public static string Stamp(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}"
        : $"{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>A line starting with "#", "-" or "1." would turn into a heading or a list.</summary>
    static string EscapeStart(string text) =>
        text.Length > 0 && (text[0] is '#' or '-' or '+' or '>' || char.IsDigit(text[0]) && text.TakeWhile(char.IsDigit).Count() is var n && n < text.Length && text[n] == '.')
            ? "\\" + text : text;

    /// <summary>"English", "简体中文 (auto)": the code without yt-dlp's "-orig".</summary>
    public static string LanguageName(string code)
    {
        var bare = code.EndsWith("-orig", StringComparison.Ordinal) ? code[..^5] : code;
        try
        {
            return CultureInfo.GetCultureInfo(bare).NativeName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    const double ParagraphGap = 2;   // seconds of silence that end a paragraph
    const double SoftLength = 30;    // after this long, the next sentence end does
    const double HardLength = 60;    // and after this long, any line break

    /// <summary>Cues joined into paragraphs, broken at pauses and sentence ends so each covers half a minute or so.</summary>
    static List<(TimeSpan Start, string Text)> Paragraphs(IReadOnlyList<SubtitleCue> cues)
    {
        var result = new List<(TimeSpan, string)>();
        var text = new StringBuilder();
        TimeSpan start = default, lastEnd = default;
        foreach (var cue in cues)
        {
            if (text.Length > 0)
            {
                var span = (cue.Start - start).TotalSeconds;
                var sentenceEnd = text[^1] is '.' or '!' or '?' or '。' or '！' or '？' or '…';
                if ((cue.Start - lastEnd).TotalSeconds >= ParagraphGap || span >= HardLength || span >= SoftLength && sentenceEnd)
                {
                    result.Add((start, text.ToString()));
                    text.Clear();
                }
            }
            if (text.Length == 0) start = cue.Start;
            foreach (var line in cue.Text.Split('\n'))
            {
                if (text.Length > 0 && !(IsCjk(text[^1]) && IsCjk(line[0]))) text.Append(' ');
                text.Append(line);
            }
            lastEnd = cue.End;
        }
        if (text.Length > 0) result.Add((start, text.ToString()));
        return result;
    }

    /// <summary>Chinese and Japanese run on without spaces between lines.</summary>
    static bool IsCjk(char c) => c is >= '　' and <= '鿿' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';
}
