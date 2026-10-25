using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace VidFlow.Controls;

/// <summary>
/// Lays cards out in equal columns, as many as fit (up to <see cref="MaxColumns"/>), using only column counts that
/// fill every row (3 cards: one row of three, or three rows, never 2 + 1). Cards in a row share the height of the
/// tallest, so wrapped text never gets cut off.
/// </summary>
public sealed partial class CardGridPanel : Panel
{
    public double MinColumnWidth { get; set; } = 200;
    public double ColumnSpacing { get; set; } = 8;
    public double RowSpacing { get; set; } = 8;
    public int MaxColumns { get; set; } = 3;

    int Columns(double width)
    {
        var n = Children.Count;
        for (var c = Math.Min(MaxColumns, Math.Max(n, 1)); c > 1; c--)
            if (n % c == 0 && c * MinColumnWidth + (c - 1) * ColumnSpacing <= width) return c;
        return 1;
    }

    double ColumnWidth(double width, int columns) => Math.Max(0, (width - (columns - 1) * ColumnSpacing) / columns);

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinColumnWidth * MaxColumns : available.Width;
        var columns = Columns(width);
        var columnWidth = ColumnWidth(width, columns);
        double height = 0, rowHeight = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == Children.Count - 1)
            {
                height += rowHeight + (height > 0 ? RowSpacing : 0);
                rowHeight = 0;
            }
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width);
        var columnWidth = ColumnWidth(final.Width, columns);
        double y = 0;
        for (var row = 0; row * columns < Children.Count; row++)
        {
            var cells = Children.Skip(row * columns).Take(columns).ToList();
            var rowHeight = cells.Max(c => c.DesiredSize.Height);
            for (var col = 0; col < cells.Count; col++)
                cells[col].Arrange(new Rect(col * (columnWidth + ColumnSpacing), y, columnWidth, rowHeight));
            y += rowHeight + RowSpacing;
        }
        return final;
    }
}
