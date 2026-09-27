using System.Text;
using JoePro.Documents.Yaml;

namespace JoePro.Documents.Reports;

public enum ReportKind { Report, Label }

public enum BandKind { Title, PageHeader, ColumnHeader, GroupHeader, DetailHeader, Detail, DetailFooter, GroupFooter, ColumnFooter, PageFooter, Summary }

public enum CalcType { None, Count, Sum, Average, Lowest, Highest, StdDev, Variance }

public enum ResetScope { Report, Page, Column, Group }

public enum FloatMode { Top, Float, Bottom }

public enum TextAlign { Left, Center, Right }

public enum PenStyle { Solid, Dot, Dash, DashDot, DashDotDot, None }

public enum PictureScale { Clip, Scale, Stretch }

public enum PictureSource { File, Field, Expression }

public enum ColumnOrder { Down, Across }

/// <summary>
/// A report (.jpreport) or label (.jplabel) layout: page setup, data environment, variables, groups and bands of
/// positioned objects. Measurements are in inches. Stored as a canonical YAML subset (see <see cref="ReportSerializer"/>).
/// </summary>
public sealed class ReportDocument
{
    public ReportKind Kind { get; set; }
    public string? Description { get; set; }

    // Page setup (width and height are the paper in portrait orientation).
    public string Paper { get; set; } = "Letter";
    public double PageWidth { get; set; } = 8.5;
    public double PageHeight { get; set; } = 11;
    public bool Landscape { get; set; }
    public double LeftMargin { get; set; } = 0.5;
    public double RightMargin { get; set; } = 0.5;
    public double TopMargin { get; set; } = 0.5;
    public double BottomMargin { get; set; } = 0.5;
    public int Columns { get; set; } = 1;
    /// <summary>Column width; 0 divides the printable width evenly.</summary>
    public double ColumnWidth { get; set; }
    public double ColumnSpacing { get; set; }
    public ColumnOrder ColumnOrder { get; set; } = ColumnOrder.Down;

    public string FontName { get; set; } = "Arial";
    public double FontSize { get; set; } = 10;

    public bool TitleOnNewPage { get; set; }
    public bool SummaryOnNewPage { get; set; }
    /// <summary>Print the page header and footer on a summary that starts its own page.</summary>
    public bool SummaryWithPageHeader { get; set; } = true;
    public bool PrivateDataSession { get; set; }
    /// <summary>The data environment as DEFINE CLASS … AS DataEnvironment code (cursors, relations, event code), or null.</summary>
    public string? DataEnvironment { get; set; }

    public List<ReportVariable> Variables { get; } = new();
    /// <summary>Data groups, outermost first.</summary>
    public List<ReportGroup> Groups { get; } = new();
    public List<ReportBand> Bands { get; } = new();

    /// <summary>Printable width of the page in the current orientation.</summary>
    public double PaperWidth => Landscape ? PageHeight : PageWidth;
    public double PaperHeight => Landscape ? PageWidth : PageHeight;
    public double PrintableWidth => PaperWidth - LeftMargin - RightMargin;
    public double EffectiveColumnWidth => ColumnWidth > 0 ? ColumnWidth : Math.Max(0.1, (PrintableWidth - ColumnSpacing * (Columns - 1)) / Math.Max(1, Columns));

    public ReportBand? Band(BandKind kind, int index = 0) =>
        Bands.FirstOrDefault(b => b.Kind == kind && (index == 0 || b.Index == index));

    /// <summary>Bands in the order they appear in the designer (title first, summary last).</summary>
    public void SortBands()
    {
        int Order(ReportBand b) => b.Kind switch
        {
            BandKind.Title => 0, BandKind.PageHeader => 1, BandKind.ColumnHeader => 2,
            BandKind.GroupHeader => 100 + b.Index,
            BandKind.DetailHeader => 1000 + b.Index * 3, BandKind.Detail => 1001 + b.Index * 3, BandKind.DetailFooter => 1002 + b.Index * 3,
            BandKind.GroupFooter => 10000 - b.Index,
            BandKind.ColumnFooter => 20000, BandKind.PageFooter => 20001, _ => 20002,
        };
        var sorted = Bands.OrderBy(Order).ToList();
        Bands.Clear();
        Bands.AddRange(sorted);
    }

    /// <summary>A new report with a page header, one detail band and a page footer (CREATE REPORT).</summary>
    public static ReportDocument NewReport()
    {
        var r = new ReportDocument();
        r.Bands.Add(new ReportBand { Kind = BandKind.PageHeader, Height = 0.5 });
        r.Bands.Add(new ReportBand { Kind = BandKind.Detail, Index = 1, Height = 0.25 });
        r.Bands.Add(new ReportBand { Kind = BandKind.PageFooter, Height = 0.5 });
        return r;
    }

    public static ReportDocument Load(string path) =>
        ReportSerializer.Parse(File.ReadAllText(path), path.EndsWith(".jplabel", StringComparison.OrdinalIgnoreCase) ? ReportKind.Label : ReportKind.Report);

    public void Save(string path) => File.WriteAllText(path, ReportSerializer.Write(this), new UTF8Encoding(false));
}

public sealed class ReportVariable
{
    public string Name { get; set; } = "";
    /// <summary>The value to store (an expression).</summary>
    public string Value { get; set; } = "";
    public string? Initial { get; set; }
    public CalcType Calculate { get; set; }
    public ResetScope Reset { get; set; }
    /// <summary>For <see cref="ResetScope.Group"/>: the 1-based group number.</summary>
    public int ResetGroup { get; set; }
    /// <summary>Release the variable when the report ends.</summary>
    public bool Release { get; set; } = true;
}

public sealed class ReportGroup
{
    public string Expression { get; set; } = "";
    public bool NewPage { get; set; }
    public bool NewColumn { get; set; }
    public bool ResetPageNumber { get; set; }
    public bool ReprintHeader { get; set; }
    /// <summary>Start the group on a new page when less than this much space (inches) is left; 0 = never.</summary>
    public double MinSpace { get; set; }
}

public sealed class ReportBand
{
    public BandKind Kind { get; set; }
    /// <summary>Group number for group bands, detail number for detail bands (1-based); 0 otherwise.</summary>
    public int Index { get; set; }
    public double Height { get; set; }
    /// <summary>The band keeps its height even when objects stretch.</summary>
    public bool ConstantHeight { get; set; }
    public string? OnEntry { get; set; }
    public string? OnExit { get; set; }
    /// <summary>Detail bands: the child alias whose records drive this band (VFP 9 multiple detail bands).</summary>
    public string? TargetAlias { get; set; }
    public List<ReportObject> Objects { get; } = new();

    public string DisplayName => Kind switch
    {
        BandKind.GroupHeader or BandKind.GroupFooter => $"{Kind} {Index}",
        BandKind.Detail or BandKind.DetailHeader or BandKind.DetailFooter => Index > 1 ? $"{Kind} {Index}" : Kind.ToString(),
        _ => Kind.ToString(),
    };
}

public abstract class ReportObject
{
    public string? Name { get; set; }
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public FloatMode Float { get; set; }
    /// <summary>Fields: grow downward to show all the text. Lines and shapes: stretch with the band.</summary>
    public bool Stretch { get; set; }
    public string? PrintWhen { get; set; }
    public bool PrintRepeated { get; set; } = true;
    public bool RemoveLineIfBlank { get; set; }
    public string? Comment { get; set; }

    public abstract ReportObject Clone();
    protected T CopyBase<T>(T o) where T : ReportObject
    {
        o.Name = Name; o.Left = Left; o.Top = Top; o.Width = Width; o.Height = Height; o.Float = Float; o.Stretch = Stretch;
        o.PrintWhen = PrintWhen; o.PrintRepeated = PrintRepeated; o.RemoveLineIfBlank = RemoveLineIfBlank; o.Comment = Comment;
        return o;
    }
}

/// <summary>Font and colors of text objects. Null values use the report's defaults (colors: black text, transparent background).</summary>
public abstract class ReportTextObject : ReportObject
{
    public string? FontName { get; set; }
    public double? FontSize { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikeout { get; set; }
    /// <summary>Colors as VFP RGB numbers (red + green*256 + blue*65536).</summary>
    public int? ForeColor { get; set; }
    public int? BackColor { get; set; }
    public TextAlign Align { get; set; }

    protected T CopyText<T>(T o) where T : ReportTextObject
    {
        CopyBase(o);
        o.FontName = FontName; o.FontSize = FontSize; o.Bold = Bold; o.Italic = Italic; o.Underline = Underline; o.Strikeout = Strikeout;
        o.ForeColor = ForeColor; o.BackColor = BackColor; o.Align = Align;
        return o;
    }
}

public sealed class ReportLabel : ReportTextObject
{
    public string Text { get; set; } = "";
    public override ReportObject Clone() => CopyText(new ReportLabel { Text = Text });
}

public sealed class ReportField : ReportTextObject
{
    public string Expression { get; set; } = "";
    /// <summary>Format/InputMask as in TRANSFORM (for example "@Z 999,999.99").</summary>
    public string? Format { get; set; }
    public CalcType Calculate { get; set; }
    public ResetScope Reset { get; set; }
    public int ResetGroup { get; set; }
    public override ReportObject Clone() => CopyText(new ReportField { Expression = Expression, Format = Format, Calculate = Calculate, Reset = Reset, ResetGroup = ResetGroup });
}

public sealed class ReportLine : ReportObject
{
    public bool Vertical { get; set; }
    public double PenWidth { get; set; } = 1;
    public PenStyle PenStyle { get; set; }
    public int? Color { get; set; }
    public override ReportObject Clone() => CopyBase(new ReportLine { Vertical = Vertical, PenWidth = PenWidth, PenStyle = PenStyle, Color = Color });
}

public sealed class ReportShape : ReportObject
{
    /// <summary>0 = rectangle, 99 = ellipse.</summary>
    public int Curvature { get; set; }
    public double PenWidth { get; set; } = 1;
    public PenStyle PenStyle { get; set; }
    public int? LineColor { get; set; }
    public int? FillColor { get; set; }
    public override ReportObject Clone() => CopyBase(new ReportShape { Curvature = Curvature, PenWidth = PenWidth, PenStyle = PenStyle, LineColor = LineColor, FillColor = FillColor });
}

public sealed class ReportPicture : ReportObject
{
    public PictureSource Source { get; set; }
    /// <summary>A file name, a General field, or an expression returning a file name.</summary>
    public string Value { get; set; } = "";
    public PictureScale Scale { get; set; } = PictureScale.Scale;
    public override ReportObject Clone() => CopyBase(new ReportPicture { Source = Source, Value = Value, Scale = Scale });
}
