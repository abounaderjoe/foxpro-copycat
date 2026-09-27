using JoePro.Documents.Reports;

namespace JoePro.Reports;

/// <summary>A font as the layout measured it (family as requested; the renderer resolves substitutes the same way).</summary>
public sealed record FontSpec(string Family, double Size, bool Bold = false, bool Italic = false, bool Underline = false, bool Strikeout = false);

/// <summary>Something placed on a page. Coordinates are in points (1/72 inch) from the top left of the paper.</summary>
public abstract class PageItem
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    /// <summary>The report object this item came from (its position in the document), or -1.</summary>
    public int ObjectId { get; set; } = -1;
}

public sealed class TextItem : PageItem
{
    public required List<string> Lines { get; init; }
    public required FontSpec Font { get; init; }
    public double LineHeight { get; init; }
    public double Ascent { get; init; }
    public int Color { get; init; }
    public int? Back { get; init; }
    public TextAlign Align { get; init; }
    public string Text => string.Join("\n", Lines);
}

public sealed class LineItem : PageItem
{
    public bool Vertical { get; init; }
    public double PenWidth { get; init; } = 1;
    public PenStyle Style { get; init; }
    public int Color { get; init; }
}

public sealed class RectItem : PageItem
{
    public int Curvature { get; init; }
    public double PenWidth { get; init; } = 1;
    public PenStyle Style { get; init; }
    public int? LineColor { get; init; }
    public int? FillColor { get; init; }
}

public sealed class ImageItem : PageItem
{
    public required byte[] Data { get; init; }
    public PictureScale Scale { get; init; }
}

public sealed class ReportPage
{
    public int Number { get; set; }
    public double Width { get; init; }
    public double Height { get; init; }
    public List<PageItem> Items { get; } = new();
}

/// <summary>The laid-out report: pages ready for any renderer.</summary>
public sealed class RenderedReport
{
    public required ReportDocument Document { get; init; }
    public string Title { get; init; } = "";
    public List<ReportPage> Pages { get; } = new();
    /// <summary>Fonts that were not installed and what was used instead.</summary>
    public SortedDictionary<string, string> FontSubstitutions { get; } = new(StringComparer.OrdinalIgnoreCase);
}
