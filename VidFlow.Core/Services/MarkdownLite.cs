using System.Text;
using System.Text.RegularExpressions;

namespace VidFlow.Services;

/// <summary>A run of text in a line. <paramref name="Url"/> makes it a link (http and https only).</summary>
public sealed record MdInline(string Text, bool Bold = false, bool Code = false, string? Url = null);

public enum MdBlockKind { Heading, Paragraph, Bullet, Numbered }

/// <summary>One line of a document. <paramref name="Level"/> is the heading level, or how deep a list item is nested
/// (0 at the top). <paramref name="Number"/> is a numbered item's own number.</summary>
public sealed record MdBlock(MdBlockKind Kind, int Level, IReadOnlyList<MdInline> Inlines, int Number = 0);

/// <summary>
/// The Markdown release notes are written in: headings, bullet and numbered lists (nested), paragraphs, bold, inline
/// code and links. Anything else stays as plain text rather than going missing.
/// </summary>
public static partial class MarkdownLite
{
    public static List<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        var paragraph = new StringBuilder();

        void EndParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new(MdBlockKind.Paragraph, 0, Inlines(paragraph.ToString())));
            paragraph.Clear();
        }

        foreach (var raw in (markdown ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                EndParagraph();
            }
            else if (HeadingLine().Match(line) is { Success: true } heading)
            {
                EndParagraph();
                blocks.Add(new(MdBlockKind.Heading, heading.Groups[1].Length, Inlines(heading.Groups[2].Value)));
            }
            else if (BulletLine().Match(line) is { Success: true } bullet)
            {
                EndParagraph();
                blocks.Add(new(MdBlockKind.Bullet, Depth(bullet.Groups[1].Value), Inlines(bullet.Groups[2].Value)));
            }
            else if (NumberedLine().Match(line) is { Success: true } numbered)
            {
                EndParagraph();
                blocks.Add(new(MdBlockKind.Numbered, Depth(numbered.Groups[1].Value), Inlines(numbered.Groups[3].Value),
                    int.Parse(numbered.Groups[2].Value)));
            }
            else
            {
                // Lines of one paragraph run together, as Markdown has it.
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line.Trim());
            }
        }
        EndParagraph();
        return blocks;
    }

    /// <summary>Two spaces (or a tab) to a level of nesting.</summary>
    static int Depth(string indent) => indent.Replace("\t", "  ").Length / 2;

    public static List<MdInline> Inlines(string text)
    {
        var inlines = new List<MdInline>();
        var at = 0;
        foreach (Match m in InlineMark().Matches(text))
        {
            if (m.Index > at) inlines.Add(new(text[at..m.Index]));
            if (m.Groups["bold"].Success)
                // A link or code inside bold keeps being one.
                inlines.AddRange(Inlines(m.Groups["bold"].Value).Select(i => i with { Bold = true }));
            else if (m.Groups["code"].Success)
                inlines.Add(new(m.Groups["code"].Value, Code: true));
            else
                inlines.Add(new(m.Groups["label"].Value, Url: m.Groups["url"].Value));
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(new(text[at..]));
        return inlines;
    }

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.+)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^(\s*)(\d{1,9})[.)]\s+(.+)$")]
    private static partial Regex NumberedLine();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<url>https?://[^\s)]+)\)")]
    private static partial Regex InlineMark();
}
