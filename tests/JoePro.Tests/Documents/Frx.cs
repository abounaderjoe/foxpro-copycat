using JoePro.Core;
using JoePro.Legacy.Formats;

namespace JoePro.Tests.Documents;

/// <summary>Builds small VFP report files (.frx/.frt) for tests, with the columns the converter reads.</summary>
public sealed class Frx
{
    private static readonly FieldDef[] Fields =
    [
        new("PLATFORM", 'C', 8), new("UNIQUEID", 'C', 10), new("OBJTYPE", 'N', 2), new("OBJCODE", 'N', 3), new("NAME", 'M'), new("EXPR", 'M'),
        new("VPOS", 'N', 9, 3), new("HPOS", 'N', 9, 3), new("HEIGHT", 'N', 9, 3), new("WIDTH", 'N', 9, 3), new("PICTURE", 'M'),
        new("COMMENT", 'M'), new("TAG", 'M'), new("TAG2", 'M'), new("PENRED", 'N', 5), new("PENGREEN", 'N', 5), new("PENBLUE", 'N', 5),
        new("FILLRED", 'N', 5), new("FILLGREEN", 'N', 5), new("FILLBLUE", 'N', 5), new("PENSIZE", 'N', 5), new("PENPAT", 'N', 5), new("FILLPAT", 'N', 5),
        new("FONTFACE", 'M'), new("FONTSTYLE", 'N', 3), new("FONTSIZE", 'N', 3), new("MODE", 'N', 3), new("FLOAT", 'L'), new("STRETCH", 'L'),
        new("TOP", 'L'), new("BOTTOM", 'L'), new("NOREPEAT", 'L'), new("PAGEBREAK", 'L'), new("COLBREAK", 'L'), new("RESETPAGE", 'L'),
        new("GENERAL", 'N', 3), new("SWAPHEADER", 'L'), new("EJECTBEFOR", 'L'), new("EJECTAFTER", 'L'), new("OFFSET", 'N', 3),
        new("TOPMARGIN", 'N', 3), new("BOTMARGIN", 'N', 3), new("TOTALTYPE", 'N', 2), new("RESETTOTAL", 'N', 2), new("SUPEXPR", 'M'),
    ];

    private readonly List<Value[]> _rows = new();
    /// <summary>Top of the next band (1/10000 in), counting the separator bars as FRX does.</summary>
    private double _bandTop;
    private readonly List<(int Code, double Top, double Height)> _bands = new();

    public Frx(int columns = 1, double columnWidth = 80000, string paper = "1", string orientation = "0", string font = "Arial", int fontSize = 10)
    {
        Row(1, 53, ("EXPR", $"ORIENTATION={orientation}\nPAPERSIZE={paper}\nCOLOR=2\n"), ("VPOS", columns), ("WIDTH", columnWidth), ("FONTFACE", font), ("FONTSIZE", fontSize),
            ("HPOS", columns > 1 ? 2500 : 0));
    }

    public Frx Row(int type, int code, params (string Col, object Value)[] cols)
    {
        var values = Fields.Select(f => f.Type switch { 'M' or 'C' => Value.String(""), 'L' => Value.False, _ => Value.Number(0) }).ToArray();
        values[0] = Value.String("WINDOWS");
        void Set(string col, object v)
        {
            var i = Array.FindIndex(Fields, f => f.Name == col);
            values[i] = v switch
            {
                string s => Value.String(s.Replace("\r\n", "\n").Replace("\n", "\r\n")),
                bool b => Value.Logical(b),
                int n => Value.Number(n),
                double d => Value.Number(d, 3),
                _ => throw new ArgumentException(col),
            };
        }
        Set("OBJTYPE", type);
        Set("OBJCODE", code);
        foreach (var p in new[] { "PENRED", "PENGREEN", "PENBLUE", "FILLRED", "FILLGREEN", "FILLBLUE" }) Set(p, -1);
        foreach (var (c, v) in cols) Set(c, v);
        _rows.Add(values);
        return this;
    }

    /// <summary>Adds a band; objects are then placed relative to the most recent band of that code.</summary>
    public Frx Band(int code, double inches)
    {
        var h = inches * 10000;
        Row(9, code, ("HEIGHT", h));
        _bands.Add((code, _bandTop, h));
        _bandTop += h + 2083.333;
        return this;
    }

    private double Y(int bandCode, double inches) => _bands.Last(b => b.Code == bandCode).Top + inches * 10000;

    public Frx Label(int band, string text, double top, double left, double width = 1, double height = 0.2, int style = 0, params (string, object)[] extra) =>
        Row(5, 0, [("EXPR", "\"" + text + "\""), ("VPOS", Y(band, top)), ("HPOS", left * 10000), ("WIDTH", width * 10000), ("HEIGHT", height * 10000),
            ("FONTSTYLE", style), ("MODE", 1), .. extra]);

    public Frx Field(int band, string expr, double top, double left, double width = 1, double height = 0.2, params (string, object)[] extra) =>
        Row(8, 0, [("EXPR", expr), ("VPOS", Y(band, top)), ("HPOS", left * 10000), ("WIDTH", width * 10000), ("HEIGHT", height * 10000), ("MODE", 1), .. extra]);

    public Frx Line(int band, double top, double left, double width, double height = 0) =>
        Row(6, 0, ("VPOS", Y(band, top)), ("HPOS", left * 10000), ("WIDTH", width * 10000), ("HEIGHT", height * 10000), ("PENSIZE", 1), ("PENPAT", 8));

    public Frx Box(int band, double top, double left, double width, double height, int curvature = 0) =>
        Row(7, 4, ("VPOS", Y(band, top)), ("HPOS", left * 10000), ("WIDTH", width * 10000), ("HEIGHT", height * 10000), ("PENSIZE", 1), ("PENPAT", 8), ("OFFSET", curvature));

    public Frx Group(string expr, bool newPage = false, bool reprint = false) =>
        Row(10, 0, ("EXPR", expr), ("PAGEBREAK", newPage), ("SWAPHEADER", reprint));

    public Frx Variable(string name, string value, int totalType, int reset, string initial = "0") =>
        Row(18, 0, ("NAME", name), ("EXPR", value), ("TAG", initial), ("TOTALTYPE", totalType), ("RESETTOTAL", reset));

    public Frx DataEnvironment(string properties, string methods = "") => Row(25, 0, ("NAME", "dataenvironment"), ("EXPR", properties), ("TAG", methods));

    public Frx Cursor(string properties) => Row(26, 0, ("NAME", "cursor"), ("EXPR", properties));

    public void Save(string path) => DbfWriter.Write(path, Fields, _rows.Select(r => (false, r)));
}
