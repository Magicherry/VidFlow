using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using VidFlow.Services;

namespace VidFlow.Controls;

/// <summary>Shows Markdown (see <see cref="MarkdownLite"/>) in a RichTextBlock.</summary>
public static class MarkdownRenderer
{
    const double NestingIndent = 18;
    /// <summary>How wide each marker and the space after it are at the body font size.</summary>
    const double BulletWidth = 13, NumberWidth = 16;

    public static void Render(RichTextBlock target, string? markdown)
    {
        target.Blocks.Clear();
        var blocks = MarkdownLite.Parse(markdown);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var paragraph = new Paragraph();
            switch (block.Kind)
            {
                case MdBlockKind.Heading:
                    paragraph.FontSize = block.Level <= 2 ? 16 : 14;
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Margin = new Thickness(0, i == 0 ? 0 : 16, 0, 6);
                    break;
                case MdBlockKind.Bullet:
                case MdBlockKind.Numbered:
                    // A hanging indent: wrapped lines start under the text, not under the marker.
                    var hang = block.Kind == MdBlockKind.Bullet ? BulletWidth : NumberWidth;
                    paragraph.Margin = new Thickness(NestingIndent * block.Level + hang, 0, 0, 4);
                    paragraph.TextIndent = -hang;
                    paragraph.Inlines.Add(new Run { Text = (block.Kind == MdBlockKind.Bullet ? "•" : $"{block.Number}.") + " " });
                    break;
                default:
                    paragraph.Margin = new Thickness(0, 0, 0, 8);
                    break;
            }
            foreach (var inline in block.Inlines) paragraph.Inlines.Add(Build(inline));
            target.Blocks.Add(paragraph);
        }
    }

    static Inline Build(MdInline i)
    {
        var run = new Run { Text = i.Text };
        if (i.Code) run.FontFamily = new FontFamily("Consolas");
        if (i.Url is not null)
        {
            var link = new Hyperlink { NavigateUri = new Uri(i.Url) };
            link.Inlines.Add(run);
            if (i.Bold) link.FontWeight = FontWeights.SemiBold;
            return link;
        }
        if (i.Bold) run.FontWeight = FontWeights.SemiBold;
        return run;
    }
}
