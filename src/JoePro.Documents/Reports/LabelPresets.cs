namespace JoePro.Documents.Reports;

/// <summary>A label sheet layout (CREATE LABEL): paper, label size, columns and gaps, in inches.</summary>
public sealed record LabelPreset(string Name, string Paper, double PageWidth, double PageHeight, int Columns, double LabelWidth, double LabelHeight,
    double LeftMargin, double TopMargin, double HorizontalGap, double VerticalGap);

public static class LabelPresets
{
    private const double Mm = 1 / 25.4;

    public static readonly IReadOnlyList<LabelPreset> All =
    [
        new("Avery 5160 / 8160 — address, 1 x 2 5/8 in, 3 across", "Letter", 8.5, 11, 3, 2.625, 1, 0.1875, 0.5, 0.125, 0),
        new("Avery 5161 / 8161 — address, 1 x 4 in, 2 across", "Letter", 8.5, 11, 2, 4, 1, 0.15625, 0.5, 0.1875, 0),
        new("Avery 5162 / 8162 — address, 1 1/3 x 4 in, 2 across", "Letter", 8.5, 11, 2, 4, 1.3333, 0.15625, 0.8333, 0.1875, 0),
        new("Avery 5163 / 8163 — shipping, 2 x 4 in, 2 across", "Letter", 8.5, 11, 2, 4, 2, 0.15625, 0.5, 0.1875, 0),
        new("Avery 5164 / 8164 — shipping, 3 1/3 x 4 in, 2 across", "Letter", 8.5, 11, 2, 4, 3.3333, 0.15625, 0.5, 0.1875, 0),
        new("Avery 5167 — return address, 1/2 x 1 3/4 in, 4 across", "Letter", 8.5, 11, 4, 1.75, 0.5, 0.28125, 0.5, 0.3125, 0),
        new("Avery L7160 — 63.5 x 38.1 mm, 3 across (A4)", "A4", 210 * Mm, 297 * Mm, 3, 63.5 * Mm, 38.1 * Mm, 7.2 * Mm, 15.1 * Mm, 2.5 * Mm, 0),
        new("Avery L7163 — 99.1 x 38.1 mm, 2 across (A4)", "A4", 210 * Mm, 297 * Mm, 2, 99.1 * Mm, 38.1 * Mm, 4.65 * Mm, 15.1 * Mm, 2.5 * Mm, 0),
        new("Avery L7651 — 38.1 x 21.2 mm, 5 across (A4)", "A4", 210 * Mm, 297 * Mm, 5, 38.1 * Mm, 21.2 * Mm, 4.75 * Mm, 10.7 * Mm, 2.5 * Mm, 0),
        new("Continuous — 3 1/2 x 15/16 in, 1 across", "Custom", 4, 11, 1, 3.5, 0.9375, 0.25, 0, 0, 0.0625),
    ];

    /// <summary>A new label layout for a sheet: one detail band per label, printed across.</summary>
    public static ReportDocument Create(LabelPreset p)
    {
        var doc = new ReportDocument
        {
            Kind = ReportKind.Label, Paper = p.Paper, PageWidth = Math.Round(p.PageWidth, 4), PageHeight = Math.Round(p.PageHeight, 4),
            Columns = p.Columns, ColumnWidth = Math.Round(p.LabelWidth, 4), ColumnSpacing = Math.Round(p.HorizontalGap, 4), ColumnOrder = ColumnOrder.Across,
            LeftMargin = Math.Round(p.LeftMargin, 4), TopMargin = Math.Round(p.TopMargin, 4), BottomMargin = 0, RightMargin = 0,
            Description = p.Name,
        };
        doc.RightMargin = Math.Round(Math.Max(0, doc.PageWidth - doc.LeftMargin - p.Columns * p.LabelWidth - (p.Columns - 1) * p.HorizontalGap), 4);
        doc.Bands.Add(new ReportBand { Kind = BandKind.Detail, Index = 1, Height = Math.Round(p.LabelHeight + p.VerticalGap, 4) });
        return doc;
    }
}
