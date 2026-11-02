using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VidFlow.Services;
using Windows.Foundation;

namespace VidFlow.Controls;

/// <summary>
/// Lays cards out in rows of up to as many as fit (and <see cref="MaxColumns"/>), each row filling the width, split as
/// evenly as possible (see <see cref="CardLayout"/>). Cards in a row share the height of the tallest, so wrapped text
/// never gets cut off.
/// </summary>
public sealed partial class CardGridPanel : Panel
{
    public double MinColumnWidth { get; set; } = 200;
    public double ColumnSpacing { get; set; } = 8;
    public double RowSpacing { get; set; } = 8;
    public int MaxColumns { get; set; } = 3;

    int[] Rows(double width)
    {
        var columns = (int)Math.Floor((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing));
        return CardLayout.RowSizes(Children.Count, Math.Clamp(columns, 1, MaxColumns));
    }

    double CardWidth(double width, int inRow) => Math.Max(0, (width - (inRow - 1) * ColumnSpacing) / inRow);

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * MaxColumns : available.Width;
        double height = 0;
        var first = 0;
        foreach (var inRow in Rows(width))
        {
            double rowHeight = 0;
            foreach (var card in Children.Skip(first).Take(inRow))
            {
                card.Measure(new Size(CardWidth(width, inRow), double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, card.DesiredSize.Height);
            }
            height += rowHeight + (first > 0 ? RowSpacing : 0);
            first += inRow;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        var first = 0;
        foreach (var inRow in Rows(final.Width))
        {
            var cards = Children.Skip(first).Take(inRow).ToList();
            var cardWidth = CardWidth(final.Width, inRow);
            var rowHeight = cards.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < cards.Count; i++)
                cards[i].Arrange(new Rect(i * (cardWidth + ColumnSpacing), y, cardWidth, rowHeight));
            y += rowHeight + RowSpacing;
            first += inRow;
        }
        return final;
    }
}
