using JoePro.Core;
using JoePro.Legacy.Formats;

namespace JoePro.Documents.Reports;

public sealed class ReportConversion
{
    public required ReportDocument Document { get; init; }
    public List<ConversionFinding> Findings { get; } = new();
    public string SourcePath { get; init; } = "";
}

/// <summary>
/// Converts VFP report and label definitions (.FRX/.FRT, .LBX/.LBT) to .jpreport/.jplabel documents. FRX positions
/// are in 1/10000 inch and measured from the top of the whole layout, band separators included; they become
/// inches relative to each band.
/// </summary>
public static class LegacyReportConverter
{
    private const double Fru = 10000.0;
    /// <summary>The height of a band's separator bar in the VFP designer, which FRX vertical positions include.</summary>
    private const double BandBar = 2083.333;

    private sealed class Rec
    {
        public Dictionary<string, Value> Cols { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string S(string name) => Cols.TryGetValue(name, out var v) && v.Kind == ValueKind.Character ? v.AsString : "";
        public string T(string name) => S(name).Trim();
        public double N(string name) => Cols.TryGetValue(name, out var v) && v.Kind is ValueKind.Number or ValueKind.Currency ? v.AsNumber : 0;
        public bool L(string name) => Cols.TryGetValue(name, out var v) && v.Kind == ValueKind.Logical && v.AsBool;
        public int ObjType => (int)N("OBJTYPE");
        public int ObjCode => (int)N("OBJCODE");
    }

    private static List<Rec> ReadRecords(string path)
    {
        using var table = DbfTable.Open(path);
        var names = table.Fields.Select(f => f.Name).ToList();
        var list = new List<Rec>();
        foreach (var r in table.Records())
        {
            if (r.Deleted) continue;
            var rec = new Rec();
            for (int i = 0; i < names.Count; i++) rec.Cols[names[i]] = r.Values[i];
            list.Add(rec);
        }
        return list;
    }

    public static ReportConversion Convert(string path)
    {
        var isLabel = Path.GetExtension(path).Equals(".lbx", StringComparison.OrdinalIgnoreCase);
        var doc = new ReportDocument { Kind = isLabel ? ReportKind.Label : ReportKind.Report };
        if (isLabel) doc.ColumnOrder = ColumnOrder.Across;
        var result = new ReportConversion { Document = doc, SourcePath = path };
        var recs = ReadRecords(path).Where(r => !r.T("PLATFORM").Equals("DOS", StringComparison.OrdinalIgnoreCase)
                                             && !r.T("PLATFORM").Equals("UNIX", StringComparison.OrdinalIgnoreCase)
                                             && !r.T("PLATFORM").Equals("MAC", StringComparison.OrdinalIgnoreCase)).ToList();
        var name = Path.GetFileName(path);
        if (recs.Count == 0 || recs.All(r => r.ObjType != 1)) throw new FormatException($"{name} has no report record; is it a report file?");

        ApplyReportRecord(doc, recs.First(r => r.ObjType == 1), result);

        // Bands in file order; group headers count up, group footers count down.
        var bandRecs = recs.Where(r => r.ObjType == 9).ToList();
        var groupCount = bandRecs.Count(b => b.ObjCode == 3);
        int gh = 0, gf = groupCount + 1, detail = 0;
        var tops = new List<(ReportBand Band, double Top)>();
        double top = 0;
        foreach (var b in bandRecs)
        {
            var kind = b.ObjCode switch
            {
                0 => BandKind.Title, 1 => BandKind.PageHeader, 2 => BandKind.ColumnHeader, 3 => BandKind.GroupHeader, 4 => BandKind.Detail,
                5 => BandKind.GroupFooter, 6 => BandKind.ColumnFooter, 7 => BandKind.PageFooter, 8 => BandKind.Summary,
                9 => BandKind.DetailHeader, 10 => BandKind.DetailFooter, _ => (BandKind?)null,
            };
            if (kind == null)
            {
                result.Findings.Add(new(FindingStatus.Unsupported, name, $"Unknown band type {b.ObjCode} was skipped."));
                top += b.N("HEIGHT") + BandBar;
                continue;
            }
            var band = new ReportBand { Kind = kind.Value, Height = Inches(b.N("HEIGHT")) };
            switch (kind)
            {
                case BandKind.GroupHeader: band.Index = ++gh; break;
                case BandKind.GroupFooter: band.Index = --gf; break;
                case BandKind.DetailHeader: band.Index = detail + 1; break;
                case BandKind.Detail: band.Index = ++detail; break;
                case BandKind.DetailFooter: band.Index = Math.Max(1, detail); break;
            }
            if (kind == BandKind.Title && (b.L("PAGEBREAK") || b.L("EJECTAFTER"))) doc.TitleOnNewPage = true;
            if (kind == BandKind.Summary && (b.L("PAGEBREAK") || b.L("EJECTBEFOR"))) doc.SummaryOnNewPage = true;
            if (kind == BandKind.Detail && b.T("EXPR").Length > 0) band.TargetAlias = Unquote(b.T("EXPR"));
            if (IsText(b.T("TAG"))) band.OnEntry = NullIfEmpty(b.T("TAG"));
            if (IsText(b.T("TAG2"))) band.OnExit = NullIfEmpty(b.T("TAG2"));
            doc.Bands.Add(band);
            tops.Add((band, top));
            top += b.N("HEIGHT") + BandBar;
        }
        foreach (var g in recs.Where(r => r.ObjType == 10))
            doc.Groups.Add(new ReportGroup
            {
                Expression = g.T("EXPR"), NewPage = g.L("PAGEBREAK"), NewColumn = g.L("COLBREAK"), ResetPageNumber = g.L("RESETPAGE"),
                ReprintHeader = g.L("SWAPHEADER"), MinSpace = Inches(g.N("WIDTH")),
            });
        foreach (var v in recs.Where(r => r.ObjType == 18))
        {
            var (reset, group) = Reset(v);
            doc.Variables.Add(new ReportVariable
            {
                Name = v.T("NAME"), Value = v.T("EXPR"), Initial = NullIfEmpty(v.T("TAG")),
                Calculate = Calc(v), Reset = reset, ResetGroup = group,
            });
        }

        // Layout objects go to the band their vertical position falls in.
        foreach (var o in recs.Where(r => r.ObjType is 5 or 6 or 7 or 8 or 17))
        {
            var vpos = o.N("VPOS");
            var owner = tops.LastOrDefault(t => t.Top <= vpos + 210); // small tolerance: rounding in old files
            if (owner.Band == null)
            {
                result.Findings.Add(new(FindingStatus.NeedsReview, name, $"An object at {Inches(vpos)} in is above the first band and was skipped."));
                continue;
            }
            var obj = ConvertObject(o, doc, result, name);
            if (obj == null) continue;
            obj.Top = Math.Max(0, Inches(vpos - owner.Top));
            obj.Left = Inches(o.N("HPOS"));
            obj.Width = Inches(o.N("WIDTH"));
            obj.Height = Inches(o.N("HEIGHT"));
            if (obj.Top + obj.Height > owner.Band.Height + 0.001 && obj is not ReportLine)
                result.Findings.Add(new(FindingStatus.Info, name, $"{Describe(obj)} extends below its band ({owner.Band.DisplayName})."));
            owner.Band.Objects.Add(obj);
        }

        var de = recs.FirstOrDefault(r => r.ObjType == 25);
        if (de != null) doc.DataEnvironment = ConvertDataEnvironment(de, recs.Where(r => r.ObjType == 26).ToList(), result, name);
        if (recs.Any(r => r.ObjType is 2 or 3 or 4))
            result.Findings.Add(new(FindingStatus.NeedsReview, name, "The report uses a FoxPro 2.x style environment (work areas/indexes/relations); open the tables before running it."));
        if (recs.Any(r => r.ObjType == 17 && r.T("NAME").Length > 0 && r.N("OFFSET") == 2))
            result.Findings.Add(new(FindingStatus.Info, name, "Pictures from expressions are evaluated when the report runs (an object reference must return a file name or picture)."));
        doc.SortBands();
        return result;
    }

    private static double Inches(double fru) => Math.Round(fru / Fru, 4);
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    private static bool IsText(string s) => s.All(c => c >= ' ' || c is '\r' or '\n' or '\t');

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'' || s[0] == '[' && s[^1] == ']') ? s[1..^1] : s;
    }

    private static CalcType Calc(Rec r) => (int)r.N("TOTALTYPE") is var t and >= 0 and <= 7 ? (CalcType)t : CalcType.None;

    private static (ResetScope, int) Reset(Rec r) => (int)r.N("RESETTOTAL") switch
    {
        2 => (ResetScope.Page, 0),
        3 => (ResetScope.Column, 0),
        var n when n >= 4 => (ResetScope.Group, n - 3),
        _ => (ResetScope.Report, 0),
    };

    private static void ApplyReportRecord(ReportDocument doc, Rec r, ReportConversion result)
    {
        var settings = r.S("EXPR").Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToUpperInvariant(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
        (doc.Paper, doc.PageWidth, doc.PageHeight) = settings.GetValueOrDefault("PAPERSIZE") switch
        {
            "5" => ("Legal", 8.5, 14.0),
            "8" => ("A3", 11.6929, 16.5354),
            "9" => ("A4", 8.2677, 11.6929),
            "11" => ("A5", 5.8268, 8.2677),
            "7" => ("Executive", 7.25, 10.5),
            _ => ("Letter", 8.5, 11.0),
        };
        if (settings.TryGetValue("PAPERLENGTH", out var pl) && settings.TryGetValue("PAPERWIDTH", out var pw)
            && double.TryParse(pl, out var len) && double.TryParse(pw, out var wid) && len > 0 && wid > 0)
        {
            // Tenths of a millimeter.
            doc.Paper = "Custom";
            doc.PageWidth = Math.Round(wid / 254.0, 4);
            doc.PageHeight = Math.Round(len / 254.0, 4);
        }
        doc.Landscape = settings.GetValueOrDefault("ORIENTATION") == "1";
        doc.Columns = Math.Max(1, (int)r.N("VPOS"));
        var columnWidth = Inches(r.N("WIDTH"));
        if (doc.Columns > 1)
        {
            doc.ColumnWidth = columnWidth;
            doc.ColumnSpacing = Inches(r.N("HPOS"));
        }
        // FRX layouts start at the printer's printable area; use a typical 0.25 in hardware margin.
        doc.LeftMargin = 0.25;
        doc.TopMargin = 0.25 + Inches(r.N("TOPMARGIN"));
        doc.BottomMargin = 0.25 + Inches(r.N("BOTMARGIN"));
        var used = doc.Columns * columnWidth + (doc.Columns - 1) * doc.ColumnSpacing;
        if (doc.Columns == 1) used = columnWidth;
        doc.RightMargin = Math.Round(Math.Max(0, doc.PaperWidth - doc.LeftMargin - used), 4);
        if (r.T("FONTFACE").Length > 0) doc.FontName = r.T("FONTFACE");
        if (r.N("FONTSIZE") > 0) doc.FontSize = r.N("FONTSIZE");
        if (r.T("TAG2").Length > 0 || r.T("TAG").Length > 0)
            result.Findings.Add(new(FindingStatus.Info, Path.GetFileName(result.SourcePath), "Printer-specific settings (DEVMODE) were not kept; paper size and orientation were."));
    }

    private static ReportObject? ConvertObject(Rec o, ReportDocument doc, ReportConversion result, string file)
    {
        ReportObject obj;
        switch (o.ObjType)
        {
            case 5:
            {
                var text = Unquote(o.T("EXPR"));
                obj = new ReportLabel { Text = text };
                break;
            }
            case 8:
            {
                var (reset, group) = Reset(o);
                obj = new ReportField
                {
                    Expression = o.T("EXPR"),
                    Format = NullIfEmpty(Unquote(o.T("PICTURE"))),
                    Calculate = Calc(o), Reset = reset, ResetGroup = group,
                };
                break;
            }
            case 6:
                obj = new ReportLine { Vertical = o.N("HEIGHT") > o.N("WIDTH"), PenWidth = Math.Max(0.5, o.N("PENSIZE")), PenStyle = Pen(o), Color = Rgb(o, "PEN") };
                break;
            case 7:
                obj = new ReportShape
                {
                    Curvature = Math.Clamp((int)o.N("OFFSET"), 0, 99), PenWidth = Math.Max(0.5, o.N("PENSIZE")), PenStyle = Pen(o),
                    LineColor = Rgb(o, "PEN"), FillColor = o.N("FILLPAT") != 0 && o.N("MODE") == 0 ? Rgb(o, "FILL") ?? 0xFFFFFF : null,
                };
                break;
            case 17:
            {
                var source = (int)o.N("OFFSET") switch { 1 => PictureSource.Field, 2 => PictureSource.Expression, _ => PictureSource.File };
                var value = source == PictureSource.File ? Unquote(o.T("PICTURE")).Replace('\\', '/') : o.T("NAME");
                obj = new ReportPicture
                {
                    Source = source, Value = value,
                    Scale = (int)o.N("GENERAL") switch { 0 => PictureScale.Clip, 2 => PictureScale.Stretch, _ => PictureScale.Scale },
                };
                if (source == PictureSource.Field) result.Findings.Add(new(FindingStatus.NeedsReview, file, $"Picture from General field {value}: General (OLE) fields show only the images they contain."));
                break;
            }
            default:
                return null;
        }
        if (obj is ReportTextObject t)
        {
            var face = o.T("FONTFACE");
            if (face.Length > 0 && !face.Equals(doc.FontName, StringComparison.OrdinalIgnoreCase)) t.FontName = face;
            if (o.N("FONTSIZE") > 0 && o.N("FONTSIZE") != doc.FontSize) t.FontSize = o.N("FONTSIZE");
            var style = (int)o.N("FONTSTYLE");
            t.Bold = (style & 1) != 0; t.Italic = (style & 2) != 0; t.Underline = (style & 4) != 0; t.Strikeout = (style & 128) != 0;
            t.ForeColor = Rgb(o, "PEN") is { } fc && fc != 0 ? fc : null;
            t.BackColor = o.N("MODE") == 0 ? Rgb(o, "FILL") ?? 0xFFFFFF : null;
            t.Align = (int)o.N("OFFSET") switch { 1 => TextAlign.Right, 2 => TextAlign.Center, _ => TextAlign.Left };
            if (t is ReportLabel && t.BackColor == 0xFFFFFF) t.BackColor = null; // white on white paper: same as transparent
        }
        obj.Float = o.L("FLOAT") ? FloatMode.Float : o.L("BOTTOM") ? FloatMode.Bottom : FloatMode.Top;
        obj.Stretch = o.L("STRETCH");
        obj.PrintWhen = NullIfEmpty(o.T("SUPEXPR"));
        obj.PrintRepeated = !o.L("NOREPEAT");
        obj.Comment = NullIfEmpty(o.T("COMMENT"));
        if (o.T("NAME").Length > 0 && o.ObjType != 17 && o.ObjType != 18) obj.Name = o.T("NAME");
        return obj;
    }

    private static PenStyle Pen(Rec o) => (int)o.N("PENPAT") switch
    {
        0 => PenStyle.None, 1 => PenStyle.Dot, 2 => PenStyle.Dash, 3 => PenStyle.DashDot, 4 => PenStyle.DashDotDot, _ => PenStyle.Solid,
    };

    private static int? Rgb(Rec o, string prefix)
    {
        var (r, g, b) = (o.N(prefix + "RED"), o.N(prefix + "GREEN"), o.N(prefix + "BLUE"));
        if (r < 0 || g < 0 || b < 0) return null;
        return (int)r + (int)g * 256 + (int)b * 65536;
    }

    private static string Describe(ReportObject o) => o switch
    {
        ReportField f => $"Field {f.Expression}",
        ReportLabel l => $"Label \"{l.Text}\"",
        _ => o.GetType().Name.Replace("Report", ""),
    };

    /// <summary>The report's data environment as DEFINE CLASS code, converted like a form's data environment.</summary>
    private static string ConvertDataEnvironment(Rec de, List<Rec> members, ReportConversion result, string file)
    {
        var scratch = new ConversionResult { File = new ClassFile(), SourcePath = result.SourcePath };
        var cls = new ClassDocument { Name = "ReportDataEnvironment", ParentClass = "DataEnvironment" };
        foreach (var (n, v) in LegacyFormConverter.ParseProperties(de.S("EXPR"), "Dataenvironment", scratch))
            if (!n.Equals("Name", StringComparison.OrdinalIgnoreCase)) cls.Properties[n] = LegacyFormConverter.ConvertValue(n, v, "Dataenvironment", scratch);
        foreach (var m in LegacyFormConverter.ParseMethods(de.S("TAG"))) cls.Methods.Add(m);
        var index = 0;
        foreach (var rec in members)
        {
            var props = LegacyFormConverter.ParseProperties(rec.S("EXPR"), "cursor", scratch);
            var objName = props.FirstOrDefault(p => p.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)).Value is { } nm ? Unquote(nm) : $"Cursor{++index}";
            var isRelation = props.Any(p => p.Name.Equals("ParentAlias", StringComparison.OrdinalIgnoreCase));
            var member = new MemberDocument { Path = objName, Class = isRelation ? "Relation" : "Cursor" };
            foreach (var (n, v) in props)
                if (!n.Equals("Name", StringComparison.OrdinalIgnoreCase)) member.Properties[n] = LegacyFormConverter.ConvertValue(n, v, objName, scratch);
            cls.Members.Add(member);
            foreach (var m in LegacyFormConverter.ParseMethods(rec.S("TAG"))) { m.Name = objName + "." + m.Name; cls.Methods.Add(m); }
        }
        foreach (var f in scratch.Findings) result.Findings.Add(f with { Object = file + ": " + f.Object });
        return ClassLibrary.ClassCode(cls);
    }
}
