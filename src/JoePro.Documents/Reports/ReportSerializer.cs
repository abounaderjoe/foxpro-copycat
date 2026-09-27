using System.Globalization;
using JoePro.Documents.Yaml;

namespace JoePro.Documents.Reports;

/// <summary>
/// Reads and writes .jpreport/.jplabel documents as canonical YAML: fixed key order, only values that differ from
/// the defaults, one value per line, measurements in inches rounded to 1/10000.
/// </summary>
public static class ReportSerializer
{
    public const string ReportHeader = "Joe Pro report v1";
    public const string LabelHeader = "Joe Pro label v1";

    private static string Word<T>(T value) where T : Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    private static T ParseWord<T>(YamlMap map, string key, T fallback) where T : struct, Enum
    {
        var s = map.Str(key);
        if (s == null) return fallback;
        return Enum.TryParse<T>(s, ignoreCase: true, out var v) && !int.TryParse(s, out _)
            ? v
            : throw new FormatException($"Line {map[key]!.Line}: '{s}' is not a valid {key} (use {string.Join(", ", Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}).");
    }

    private static string Color(int c) => $"{c & 0xFF},{(c >> 8) & 0xFF},{(c >> 16) & 0xFF}";

    private static int? ParseColor(YamlMap map, string key)
    {
        var s = map.Str(key);
        if (s == null) return null;
        var parts = s.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts.All(p => int.TryParse(p, out var n) && n is >= 0 and <= 255))
            return int.Parse(parts[0]) + int.Parse(parts[1]) * 256 + int.Parse(parts[2]) * 65536;
        throw new FormatException($"Line {map[key]!.Line}: '{key}' should be red,green,blue (for example 255,0,0).");
    }

    private static string Reset(ResetScope scope, int group) => scope == ResetScope.Group ? $"group {group}" : Word(scope);

    private static (ResetScope, int) ParseReset(YamlMap map, string key)
    {
        var s = map.Str(key);
        if (s == null) return (ResetScope.Report, 0);
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[0].Equals("group", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], out var g) && g > 0) return (ResetScope.Group, g);
        if (parts.Length == 1 && Enum.TryParse<ResetScope>(parts[0], true, out var r) && r != ResetScope.Group) return (r, 0);
        throw new FormatException($"Line {map[key]!.Line}: '{key}' should be report, page, column or group N.");
    }

    // ================================================================================
    // Writing
    // ================================================================================

    public static string Write(ReportDocument r)
    {
        var root = new YamlMap();
        root.Set("description", r.Description);
        var page = new YamlMap()
            .SetWord("paper", r.Paper, "Letter")
            .Set("width", r.PageWidth, 8.5)
            .Set("height", r.PageHeight, 11)
            .SetWord("orientation", r.Landscape ? "landscape" : "portrait", "portrait");
        var margins = new YamlMap().Set("left", r.LeftMargin, 0.5).Set("right", r.RightMargin, 0.5).Set("top", r.TopMargin, 0.5).Set("bottom", r.BottomMargin, 0.5);
        page.SetNode("margins", margins);
        var columns = new YamlMap().Set("count", r.Columns, 1).Set("width", r.ColumnWidth, 0).Set("spacing", r.ColumnSpacing, 0)
            .SetWord("order", Word(r.ColumnOrder), r.Kind == ReportKind.Label ? "across" : "down");
        page.SetNode("columns", columns);
        root.SetNode("page", page);
        root.SetNode("font", new YamlMap().Set("name", r.FontName, "Arial").Set("size", r.FontSize, 10));
        root.Set("titleOnNewPage", r.TitleOnNewPage).Set("summaryOnNewPage", r.SummaryOnNewPage).Set("summaryWithPageHeader", r.SummaryWithPageHeader, true)
            .Set("privateDataSession", r.PrivateDataSession);
        root.Set("dataEnvironment", r.DataEnvironment == null ? null : r.DataEnvironment.TrimEnd('\n') + "\n");

        var vars = new YamlSeq();
        foreach (var v in r.Variables)
            vars.Add(new YamlMap().Set("name", v.Name).Set("value", v.Value, "").Set("initial", v.Initial)
                .SetWord("calculate", Word(v.Calculate), "none").SetWord("reset", Reset(v.Reset, v.ResetGroup), "report").Set("release", v.Release, true));
        root.SetNode("variables", vars);

        var groups = new YamlSeq();
        foreach (var g in r.Groups)
            groups.Add(new YamlMap().Set("expression", g.Expression).Set("newPage", g.NewPage).Set("newColumn", g.NewColumn)
                .Set("resetPageNumber", g.ResetPageNumber).Set("reprintHeader", g.ReprintHeader).Set("minSpace", g.MinSpace, 0));
        root.SetNode("groups", groups);

        var bands = new YamlSeq();
        foreach (var b in r.Bands)
        {
            var m = new YamlMap().SetWord("band", Word(b.Kind));
            if (b.Index > 0 && b.Kind is BandKind.GroupHeader or BandKind.GroupFooter or BandKind.Detail or BandKind.DetailHeader or BandKind.DetailFooter)
                m.Set("index", b.Index, b.Kind is BandKind.Detail or BandKind.DetailHeader or BandKind.DetailFooter ? 1 : null);
            m.Set("height", b.Height).Set("constantHeight", b.ConstantHeight).Set("onEntry", b.OnEntry).Set("onExit", b.OnExit).Set("targetAlias", b.TargetAlias);
            var objs = new YamlSeq();
            foreach (var o in b.Objects) objs.Add(WriteObject(o));
            m.SetNode("objects", objs);
            bands.Add(m);
        }
        root.SetNode("bands", bands);
        return YamlText.Write(root, r.Kind == ReportKind.Label ? LabelHeader : ReportHeader);
    }

    private static YamlMap WriteObject(ReportObject o)
    {
        var m = new YamlMap();
        switch (o)
        {
            case ReportLabel l: m.Add("label", new YamlScalar(l.Text, quoted: true)); break;
            case ReportField f: m.Add("field", new YamlScalar(f.Expression, quoted: true)); break;
            case ReportLine line: m.SetWord("line", line.Vertical ? "vertical" : "horizontal"); break;
            case ReportShape s: m.SetWord("shape", s.Curvature >= 99 ? "ellipse" : s.Curvature > 0 ? "rounded" : "rectangle"); break;
            case ReportPicture p: m.Add("picture", new YamlScalar(p.Value, quoted: true)); break;
        }
        m.Set("name", o.Name).Set("left", o.Left).Set("top", o.Top).Set("width", o.Width).Set("height", o.Height);
        switch (o)
        {
            case ReportField f:
                m.Set("format", f.Format).SetWord("calculate", Word(f.Calculate), "none").SetWord("reset", Reset(f.Reset, f.ResetGroup), "report");
                break;
            case ReportLine line:
                m.Set("penWidth", line.PenWidth, 1).SetWord("penStyle", Word(line.PenStyle), "solid");
                if (line.Color is { } lc) m.SetWord("color", Color(lc));
                break;
            case ReportShape s:
                if (s.Curvature is > 0 and < 99) m.Set("curvature", s.Curvature);
                m.Set("penWidth", s.PenWidth, 1).SetWord("penStyle", Word(s.PenStyle), "solid");
                if (s.LineColor is { } slc) m.SetWord("lineColor", Color(slc));
                if (s.FillColor is { } sfc) m.SetWord("fillColor", Color(sfc));
                break;
            case ReportPicture p:
                m.SetWord("source", Word(p.Source), "file").SetWord("scale", Word(p.Scale), "scale");
                break;
        }
        if (o is ReportTextObject t)
        {
            var font = new YamlMap().Set("name", t.FontName);
            if (t.FontSize is { } size) font.Set("size", size);
            font.Set("bold", t.Bold).Set("italic", t.Italic).Set("underline", t.Underline).Set("strikeout", t.Strikeout);
            m.SetNode("font", font);
            if (t.ForeColor is { } fc) m.SetWord("foreColor", Color(fc));
            if (t.BackColor is { } bc) m.SetWord("backColor", Color(bc));
            m.SetWord("align", Word(t.Align), "left");
        }
        m.SetWord("float", Word(o.Float), "top").Set("stretch", o.Stretch).Set("printWhen", o.PrintWhen).Set("printRepeated", o.PrintRepeated, true)
            .Set("removeLineIfBlank", o.RemoveLineIfBlank).Set("comment", o.Comment);
        return m;
    }

    // ================================================================================
    // Reading
    // ================================================================================

    public static ReportDocument Parse(string text, ReportKind? kind = null)
    {
        var firstLine = text.Split('\n', 2)[0];
        var r = new ReportDocument { Kind = kind ?? (firstLine.Contains("label", StringComparison.OrdinalIgnoreCase) ? ReportKind.Label : ReportKind.Report) };
        if (r.Kind == ReportKind.Label) r.ColumnOrder = ColumnOrder.Across;
        var root = YamlText.Parse(text);
        root.CheckKeys("description", "page", "font", "titleOnNewPage", "summaryOnNewPage", "summaryWithPageHeader", "privateDataSession", "dataEnvironment", "variables", "groups", "bands");
        r.Description = root.Str("description");
        if (root.Map("page") is { } page)
        {
            page.CheckKeys("paper", "width", "height", "orientation", "margins", "columns");
            r.Paper = page.Str("paper", "Letter");
            r.PageWidth = page.Num("width", 8.5);
            r.PageHeight = page.Num("height", 11);
            r.Landscape = page.Str("orientation", "portrait") switch
            {
                "portrait" => false,
                "landscape" => true,
                var o => throw new FormatException($"Line {page["orientation"]!.Line}: orientation should be portrait or landscape, not '{o}'."),
            };
            if (page.Map("margins") is { } mg)
            {
                mg.CheckKeys("left", "right", "top", "bottom");
                r.LeftMargin = mg.Num("left", 0.5); r.RightMargin = mg.Num("right", 0.5); r.TopMargin = mg.Num("top", 0.5); r.BottomMargin = mg.Num("bottom", 0.5);
            }
            if (page.Map("columns") is { } cols)
            {
                cols.CheckKeys("count", "width", "spacing", "order");
                r.Columns = Math.Max(1, cols.Int("count", 1));
                r.ColumnWidth = cols.Num("width", 0);
                r.ColumnSpacing = cols.Num("spacing", 0);
                r.ColumnOrder = ParseWord(cols, "order", r.ColumnOrder);
            }
        }
        if (root.Map("font") is { } font)
        {
            font.CheckKeys("name", "size");
            r.FontName = font.Str("name", "Arial");
            r.FontSize = font.Num("size", 10);
        }
        r.TitleOnNewPage = root.Bool("titleOnNewPage");
        r.SummaryOnNewPage = root.Bool("summaryOnNewPage");
        r.SummaryWithPageHeader = root.Bool("summaryWithPageHeader", true);
        r.PrivateDataSession = root.Bool("privateDataSession");
        r.DataEnvironment = root.Str("dataEnvironment") is { Length: > 0 } de ? de : null;

        foreach (var node in root.Seq("variables"))
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a variable should be a mapping.");
            m.CheckKeys("name", "value", "initial", "calculate", "reset", "release");
            var (reset, group) = ParseReset(m, "reset");
            r.Variables.Add(new ReportVariable
            {
                Name = m.Str("name") ?? throw new FormatException($"Line {m.Line}: a variable needs a name."),
                Value = m.Str("value", ""), Initial = m.Str("initial"), Calculate = ParseWord(m, "calculate", CalcType.None),
                Reset = reset, ResetGroup = group, Release = m.Bool("release", true),
            });
        }
        foreach (var node in root.Seq("groups"))
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a group should be a mapping.");
            m.CheckKeys("expression", "newPage", "newColumn", "resetPageNumber", "reprintHeader", "minSpace");
            r.Groups.Add(new ReportGroup
            {
                Expression = m.Str("expression") ?? throw new FormatException($"Line {m.Line}: a group needs an expression."),
                NewPage = m.Bool("newPage"), NewColumn = m.Bool("newColumn"), ResetPageNumber = m.Bool("resetPageNumber"),
                ReprintHeader = m.Bool("reprintHeader"), MinSpace = m.Num("minSpace", 0),
            });
        }
        foreach (var node in root.Seq("bands"))
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a band should be a mapping.");
            m.CheckKeys("band", "index", "height", "constantHeight", "onEntry", "onExit", "targetAlias", "objects");
            var kindOfBand = ParseWord(m, "band", BandKind.Detail);
            var band = new ReportBand
            {
                Kind = kindOfBand,
                Index = m.Int("index", kindOfBand is BandKind.Detail or BandKind.DetailHeader or BandKind.DetailFooter ? 1 : 0),
                Height = m.Num("height", 0), ConstantHeight = m.Bool("constantHeight"),
                OnEntry = m.Str("onEntry"), OnExit = m.Str("onExit"), TargetAlias = m.Str("targetAlias"),
            };
            foreach (var on in m.Seq("objects")) band.Objects.Add(ReadObject(on as YamlMap ?? throw new FormatException($"Line {on.Line}: an object should be a mapping.")));
            r.Bands.Add(band);
        }
        return r;
    }

    private static ReportObject ReadObject(YamlMap m)
    {
        var common = new[] { "name", "left", "top", "width", "height", "float", "stretch", "printWhen", "printRepeated", "removeLineIfBlank", "comment" };
        var text = new[] { "font", "foreColor", "backColor", "align" };
        ReportObject o;
        if (m["label"] != null)
        {
            m.CheckKeys([.. common, .. text, "label"]);
            o = new ReportLabel { Text = m.Str("label", "") };
        }
        else if (m["field"] != null)
        {
            m.CheckKeys([.. common, .. text, "field", "format", "calculate", "reset"]);
            var (reset, group) = ParseReset(m, "reset");
            o = new ReportField { Expression = m.Str("field", ""), Format = m.Str("format"), Calculate = ParseWord(m, "calculate", CalcType.None), Reset = reset, ResetGroup = group };
        }
        else if (m["line"] != null)
        {
            m.CheckKeys([.. common, "line", "penWidth", "penStyle", "color"]);
            o = new ReportLine
            {
                Vertical = m.Str("line") switch { "vertical" => true, "horizontal" => false, var v => throw new FormatException($"Line {m.Line}: line should be horizontal or vertical, not '{v}'.") },
                PenWidth = m.Num("penWidth", 1), PenStyle = ParseWord(m, "penStyle", PenStyle.Solid), Color = ParseColor(m, "color"),
            };
        }
        else if (m["shape"] != null)
        {
            m.CheckKeys([.. common, "shape", "curvature", "penWidth", "penStyle", "lineColor", "fillColor"]);
            var shape = m.Str("shape");
            o = new ReportShape
            {
                Curvature = shape switch
                {
                    "rectangle" => 0, "ellipse" => 99, "rounded" => m.Int("curvature", 12),
                    _ => throw new FormatException($"Line {m.Line}: shape should be rectangle, rounded or ellipse, not '{shape}'."),
                },
                PenWidth = m.Num("penWidth", 1), PenStyle = ParseWord(m, "penStyle", PenStyle.Solid), LineColor = ParseColor(m, "lineColor"), FillColor = ParseColor(m, "fillColor"),
            };
        }
        else if (m["picture"] != null)
        {
            m.CheckKeys([.. common, "picture", "source", "scale"]);
            o = new ReportPicture { Value = m.Str("picture", ""), Source = ParseWord(m, "source", PictureSource.File), Scale = ParseWord(m, "scale", PictureScale.Scale) };
        }
        else throw new FormatException($"Line {m.Line}: an object must start with label, field, line, shape or picture.");

        o.Name = m.Str("name");
        o.Left = m.Num("left", 0); o.Top = m.Num("top", 0); o.Width = m.Num("width", 0); o.Height = m.Num("height", 0);
        o.Float = ParseWord(m, "float", FloatMode.Top);
        o.Stretch = m.Bool("stretch");
        o.PrintWhen = m.Str("printWhen");
        o.PrintRepeated = m.Bool("printRepeated", true);
        o.RemoveLineIfBlank = m.Bool("removeLineIfBlank");
        o.Comment = m.Str("comment");
        if (o is ReportTextObject t)
        {
            if (m.Map("font") is { } f)
            {
                f.CheckKeys("name", "size", "bold", "italic", "underline", "strikeout");
                t.FontName = f.Str("name");
                t.FontSize = f["size"] != null ? f.Num("size", 10) : null;
                t.Bold = f.Bool("bold"); t.Italic = f.Bool("italic"); t.Underline = f.Bool("underline"); t.Strikeout = f.Bool("strikeout");
            }
            t.ForeColor = ParseColor(m, "foreColor");
            t.BackColor = ParseColor(m, "backColor");
            t.Align = ParseWord(m, "align", TextAlign.Left);
        }
        return o;
    }

    public static string FormatInches(double d) => YamlText.Number(d);
    internal static CultureInfo Invariant => CultureInfo.InvariantCulture;
}
