namespace VidFlow.Services;

/// <summary>How cards split into rows.</summary>
public static class CardLayout
{
    /// <summary>The cards in each row, for up to <paramref name="columns"/> in a row: as few rows as that allows, as even
    /// as can be, longer rows first (7 in 3 columns: 3, 2, 2). Every row is meant to fill the width, so none looks unfinished.</summary>
    public static int[] RowSizes(int count, int columns)
    {
        if (count <= 0) return [];
        var rows = (count + Math.Max(columns, 1) - 1) / Math.Max(columns, 1);
        return Enumerable.Range(0, rows).Select(r => count / rows + (r < count % rows ? 1 : 0)).ToArray();
    }
}
