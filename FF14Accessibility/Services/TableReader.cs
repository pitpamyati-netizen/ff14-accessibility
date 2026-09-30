namespace FF14Accessibility.Services;

/// <summary>A read-only cursor over visible text. Identity is separate from text,
/// so changing a quantity does not move the reader to a different row.</summary>
internal sealed class TableReader
{
    internal sealed record Cell(string Key, string Text, float X, float Y, float Height);
    internal sealed record Row(string Section, IReadOnlyList<Cell> Cells);
    internal IReadOnlyList<Row> Rows { get; private set; } = [];
    internal int RowIndex { get; private set; }
    internal int ColumnIndex { get; private set; }

    internal static IReadOnlyList<Row> Arrange(string section, IEnumerable<Cell> cells)
    {
        var result = new List<Row>();
        var unique = cells.Where(c => !string.IsNullOrWhiteSpace(c.Text)
                && float.IsFinite(c.X) && float.IsFinite(c.Y))
            .DistinctBy(c => c.Key).OrderBy(c => c.Y).ThenBy(c => c.X);
        var line = new List<Cell>();
        float top = 0, tolerance = 0;
        foreach (var cell in unique)
        {
            // Compare against the first cell, not the previous one: chained
            // offsets must never merge successive rows. Tolerance scales with text.
            if (line.Count == 0 || Math.Abs(cell.Y - top) <= Math.Min(tolerance, Math.Clamp(cell.Height * .35f, 2, 10)))
            {
                if (line.Count == 0) { top = cell.Y; tolerance = Math.Clamp(cell.Height * .35f, 2, 10); }
                line.Add(cell);
            }
            else
            {
                result.Add(new Row(section, line.OrderBy(c => c.X).ToArray()));
                line = [cell]; top = cell.Y; tolerance = Math.Clamp(cell.Height * .35f, 2, 10);
            }
        }
        if (line.Count > 0) result.Add(new Row(section, line.OrderBy(c => c.X).ToArray()));
        return result;
    }

    internal bool Refresh(IReadOnlyList<Row> rows)
    {
        var key = Rows.Count == 0 ? null : Rows[RowIndex].Cells[ColumnIndex].Key;
        Rows = rows.Where(r => r.Cells.Count > 0).ToArray();
        RowIndex = Math.Clamp(RowIndex, 0, Math.Max(0, Rows.Count - 1));
        ColumnIndex = 0;
        for (var i = 0; key != null && i < Rows.Count; i++)
            for (var j = 0; j < Rows[i].Cells.Count; j++)
                if (Rows[i].Cells[j].Key == key) { RowIndex = i; ColumnIndex = j; return true; }
        return Rows.Count > 0;
    }

    internal void MoveRow(int delta)
    {
        RowIndex = (int)Math.Clamp((long)RowIndex + delta, 0, Math.Max(0, Rows.Count - 1));
        ColumnIndex = 0;
    }
    internal void MoveColumn(int delta)
    {
        if (Rows.Count > 0) ColumnIndex = Math.Clamp(ColumnIndex + delta, 0, Rows[RowIndex].Cells.Count - 1);
    }
    internal void MoveSection(int delta)
    {
        if (Rows.Count == 0 || delta == 0) return;
        var section = Rows[RowIndex].Section;
        for (var i = RowIndex + delta; i >= 0 && i < Rows.Count; i += delta)
            if (Rows[i].Section != section)
            {
                while (i > 0 && Rows[i - 1].Section == Rows[i].Section) i--;
                RowIndex = i; ColumnIndex = 0; return;
            }
    }
    internal string SpeakRow() => Rows.Count == 0 ? AccessibilityStrings.TableEmpty
        : AccessibilityStrings.TableRow(Rows[RowIndex].Section, RowIndex + 1, Rows.Count,
            string.Join("; ", Rows[RowIndex].Cells.Select(c => c.Text)));
    internal string SpeakCell() => Rows.Count == 0 ? AccessibilityStrings.TableEmpty
        : AccessibilityStrings.TableCell(ColumnIndex + 1, Rows[RowIndex].Cells.Count,
            Rows[RowIndex].Cells.Take(ColumnIndex).LastOrDefault(c => !IsNumber(c.Text))?.Text ?? "",
            Rows[RowIndex].Cells[ColumnIndex].Text);
    private static bool IsNumber(string text) => text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c) || ",.%/+-".Contains(c));
}
