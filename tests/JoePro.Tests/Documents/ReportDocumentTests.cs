using JoePro.Documents;
using JoePro.Documents.Reports;
using JoePro.Documents.Yaml;

namespace JoePro.Tests.Documents;

public class YamlTests
{
    [Fact]
    public void Writes_and_reads_nested_maps_sequences_and_scalars()
    {
        var root = new YamlMap()
            .Set("name", "Customers")
            .Set("count", 3)
            .Set("ratio", 0.125)
            .Set("enabled", true)
            .Set("expr", "IIF(x > 1, \"a: b\", 'c')")
            .Set("looksLikeBool", "yes")
            .Set("number", "12")
            .Set("code", "SELECT 1\n  indented\nlast\n");
        var seq = new YamlSeq();
        seq.Add(new YamlMap().Set("a", 1).Set("b", "two"));
        seq.Add(new YamlMap().Set("a", 3).SetNode("inner", new YamlMap().Set("c", "x y")));
        root.SetNode("items", seq);
        var text = YamlText.Write(root, "test v1");
        Assert.Equal("""
            # test v1
            name: Customers
            count: 3
            ratio: 0.125
            enabled: true
            expr: "IIF(x > 1, \"a: b\", 'c')"
            looksLikeBool: "yes"
            number: "12"
            code: |
              SELECT 1
                indented
              last
            items:
              - a: 1
                b: two
              - a: 3
                inner:
                  c: x y

            """.Replace("\r\n", "\n"), text);
        var back = YamlText.Parse(text);
        Assert.Equal("Customers", back.Str("name"));
        Assert.Equal(3, back.Int("count", 0));
        Assert.True(back.Bool("enabled"));
        Assert.Equal("IIF(x > 1, \"a: b\", 'c')", back.Str("expr"));
        Assert.Equal("yes", back.Str("looksLikeBool"));
        Assert.Equal("SELECT 1\n  indented\nlast\n", back.Str("code"));
        var items = back.Seq("items").Cast<YamlMap>().ToList();
        Assert.Equal("two", items[0].Str("b"));
        Assert.Equal("x y", items[1].Map("inner")!.Str("c"));
    }

    [Fact]
    public void Reads_comments_single_quotes_blocks_without_final_newline_and_same_indent_sequences()
    {
        var m = YamlText.Parse("""
            # comment
            a: 'it''s'   # trailing comment
            b: |-
              one
              two
            list:
            - x
            - "y"
            empty: []
            """);
        Assert.Equal("it's", m.Str("a"));
        Assert.Equal("one\ntwo", m.Str("b"));
        Assert.Equal(["x", "y"], m.Seq("list").Cast<YamlScalar>().Select(s => s.Value));
        Assert.Empty(m.Seq("empty"));
    }

    [Theory]
    [InlineData("a: [1, 2]", "flow collections")]
    [InlineData("a: &x 1", "anchors")]
    [InlineData("a: 1\n  b: 2", "indentation")]
    [InlineData("a: 1\na: 2", "duplicate key")]
    [InlineData("a: \"open", "unterminated")]
    public void Rejects_what_the_subset_does_not_support(string text, string message)
    {
        var ex = Assert.Throws<FormatException>(() => YamlText.Parse(text));
        Assert.Contains(message, ex.Message);
    }
}

public class ReportDocumentTests
{
    private static ReportDocument Sample()
    {
        var r = ReportDocument.NewReport();
        r.Description = "Orders by customer";
        r.Groups.Add(new ReportGroup { Expression = "orders.custid", ReprintHeader = true });
        r.Variables.Add(new ReportVariable { Name = "nTotal", Value = "orders.amount", Initial = "0", Calculate = CalcType.Sum, Reset = ResetScope.Group, ResetGroup = 1 });
        r.Bands.Add(new ReportBand { Kind = BandKind.GroupHeader, Index = 1, Height = 0.3 });
        r.Bands.Add(new ReportBand { Kind = BandKind.GroupFooter, Index = 1, Height = 0.3 });
        r.SortBands();
        r.Band(BandKind.PageHeader)!.Objects.Add(new ReportLabel { Text = "Orders", Left = 0.1, Top = 0.1, Width = 2, Height = 0.3, Bold = true, FontSize = 14, Align = TextAlign.Center });
        r.Band(BandKind.Detail)!.Objects.Add(new ReportField { Expression = "orders.amount", Format = "999,999.99", Left = 5, Top = 0, Width = 1.2, Height = 0.2, Align = TextAlign.Right });
        r.Band(BandKind.GroupFooter)!.Objects.Add(new ReportField { Expression = "orders.amount", Calculate = CalcType.Sum, Reset = ResetScope.Group, ResetGroup = 1, Left = 5, Width = 1.2, Height = 0.2, ForeColor = 255 });
        r.Band(BandKind.GroupFooter)!.Objects.Add(new ReportLine { Left = 5, Width = 1.2, PenWidth = 2 });
        r.Band(BandKind.PageFooter)!.Objects.Add(new ReportShape { Curvature = 20, Width = 7.5, Height = 0.4, FillColor = 0xEEEEEE });
        r.Band(BandKind.PageFooter)!.Objects.Add(new ReportPicture { Value = "logo.png", Left = 6, Width = 1, Height = 0.4 });
        return r;
    }

    [Fact]
    public void Round_trips_canonically_and_writes_only_non_defaults()
    {
        var text = ReportSerializer.Write(Sample());
        Assert.StartsWith("# Joe Pro report v1\n", text);
        Assert.DoesNotContain("page:", text); // default page setup
        Assert.Contains("  - band: groupHeader\n    index: 1\n", text);
        Assert.Contains("      - field: orders.amount\n", text);
        Assert.Contains("        calculate: sum\n        reset: group 1\n", text);
        Assert.Contains("        foreColor: 255,0,0\n", text);
        Assert.Contains("      - shape: rounded\n", text);
        var back = ReportSerializer.Parse(text);
        Assert.Equal(text, ReportSerializer.Write(back));
        Assert.Equal(["PageHeader", "GroupHeader", "Detail", "GroupFooter", "PageFooter"], back.Bands.Select(b => b.Kind.ToString()));
        var total = (ReportField)back.Band(BandKind.GroupFooter)!.Objects[0];
        Assert.Equal((CalcType.Sum, ResetScope.Group, 1), (total.Calculate, total.Reset, total.ResetGroup));
    }

    [Fact]
    public void Page_setup_and_labels()
    {
        var label = new ReportDocument { Kind = ReportKind.Label, Columns = 3, ColumnWidth = 2.625, ColumnSpacing = 0.125, ColumnOrder = ColumnOrder.Across, Landscape = true, Paper = "A4", PageWidth = 8.2677, PageHeight = 11.6929 };
        label.Bands.Add(new ReportBand { Kind = BandKind.Detail, Index = 1, Height = 1 });
        var text = ReportSerializer.Write(label);
        Assert.StartsWith("# Joe Pro label v1\n", text);
        Assert.Contains("  orientation: landscape\n", text);
        Assert.DoesNotContain("order:", text); // across is the default for labels
        var back = ReportSerializer.Parse(text, ReportKind.Label);
        Assert.Equal((3, 2.625, true), (back.Columns, back.ColumnWidth, back.Landscape));
        Assert.Equal(11.6929, back.PaperWidth);
    }

    [Theory]
    [InlineData("bands:\n  - band: middle\n", "not a valid band")]
    [InlineData("colour: red\n", "unknown key 'colour'")]
    [InlineData("bands:\n  - band: detail\n    objects:\n      - label: x\n        foreColor: red\n", "red,green,blue")]
    [InlineData("bands:\n  - band: detail\n    objects:\n      - circle: x\n", "must start with")]
    public void Reports_errors_with_line_numbers(string text, string message)
    {
        var ex = Assert.Throws<FormatException>(() => ReportSerializer.Parse(text));
        Assert.Contains(message, ex.Message);
        Assert.StartsWith("Line ", ex.Message);
    }
}

public class LegacyReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-frx-" + Guid.NewGuid().ToString("N"));
    public LegacyReportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Converts_bands_objects_groups_variables_and_the_data_environment()
    {
        var path = Path.Combine(_dir, "orders.frx");
        new Frx(font: "Courier New", fontSize: 10)
            .Band(0, 0.5).Band(1, 0.4).Band(3, 0.3).Band(4, 0.2).Band(5, 0.3).Band(7, 0.3).Band(8, 0.4)
            .Label(0, "Order list", 0.1, 0.2, 2, 0.3, style: 1, ("FONTFACE", "Arial"), ("FONTSIZE", 16))
            .Label(1, "Amount", 0.1, 5, 1)
            .Field(3, "customer.name", 0.05, 0.1, 3, 0.2, ("FONTFACE", "Courier New"), ("FONTSIZE", 10), ("FONTSTYLE", 1))
            .Field(4, "orders.amount", 0, 5, 1.2, 0.2, ("PICTURE", "\"999,999.99\""), ("OFFSET", 1), ("STRETCH", true), ("SUPEXPR", "orders.amount > 0"))
            .Field(5, "orders.amount", 0.05, 5, 1.2, 0.2, ("TOTALTYPE", 2), ("RESETTOTAL", 4), ("PENRED", 255), ("PENGREEN", 0), ("PENBLUE", 0))
            .Line(5, 0.01, 5, 1.2)
            .Box(8, 0.05, 0.1, 7, 0.3, 10)
            .Field(7, "_PAGENO", 0, 7, 0.5)
            .Group("orders.custid", newPage: true, reprint: true)
            .Variable("nCount", "1", 1, 1)
            .DataEnvironment("Top = 10\nLeft = 10\nName = \"Dataenvironment\"", "PROCEDURE BeforeOpenTables\nSET DELETED ON\nENDPROC")
            .Cursor("Alias = \"orders\"\nCursorSource = orders.dbf\nOrder = \"custid\"\nName = \"Cursor1\"")
            .Save(path);

        var c = LegacyReportConverter.Convert(path);
        var r = c.Document;
        Assert.Equal("Courier New", r.FontName);
        Assert.Equal(["Title", "PageHeader", "GroupHeader", "Detail", "GroupFooter", "PageFooter", "Summary"], r.Bands.Select(b => b.Kind.ToString()));
        var title = (ReportLabel)r.Band(BandKind.Title)!.Objects.Single();
        Assert.Equal(("Order list", "Arial", 16.0, true), (title.Text, title.FontName, title.FontSize!.Value, title.Bold));
        Assert.Equal((0.1, 0.2), (title.Top, title.Left));
        var detail = (ReportField)r.Band(BandKind.Detail)!.Objects.Single();
        Assert.Equal(("999,999.99", TextAlign.Right, true, "orders.amount > 0"), (detail.Format, detail.Align, detail.Stretch, detail.PrintWhen));
        Assert.Equal(0, detail.Top);
        var total = (ReportField)r.Band(BandKind.GroupFooter)!.Objects.OfType<ReportField>().Single();
        Assert.Equal((CalcType.Sum, ResetScope.Group, 1, 255), (total.Calculate, total.Reset, total.ResetGroup, total.ForeColor!.Value));
        Assert.Single(r.Band(BandKind.GroupFooter)!.Objects.OfType<ReportLine>());
        Assert.Equal(10, ((ReportShape)r.Band(BandKind.Summary)!.Objects.Single()).Curvature);
        Assert.Equal("_PAGENO", ((ReportField)r.Band(BandKind.PageFooter)!.Objects.Single()).Expression);
        var group = r.Groups.Single();
        Assert.Equal(("orders.custid", true, true), (group.Expression, group.NewPage, group.ReprintHeader));
        Assert.Equal(("nCount", CalcType.Count), (r.Variables[0].Name, r.Variables[0].Calculate));
        Assert.Contains("ADD OBJECT Cursor1 AS Cursor", r.DataEnvironment);
        Assert.Contains("CursorSource = \"orders.jpt\"", r.DataEnvironment);
        Assert.Contains("PROCEDURE BeforeOpenTables", r.DataEnvironment);

        var text = ReportSerializer.Write(r);
        Assert.Equal(text, ReportSerializer.Write(ReportSerializer.Parse(text)));
    }

    [Fact]
    public void Paper_orientation_columns_and_labels()
    {
        var path = Path.Combine(_dir, "addr.lbx");
        new Frx(columns: 3, columnWidth: 26250, paper: "9", orientation: "1").Band(4, 1).Field(4, "customer.name", 0.1, 0.1, 2.5).Save(path);
        var r = LegacyReportConverter.Convert(path).Document;
        Assert.Equal(ReportKind.Label, r.Kind);
        Assert.Equal(("A4", true, 3, 2.625, 0.25), (r.Paper, r.Landscape, r.Columns, r.ColumnWidth, r.ColumnSpacing));
        Assert.StartsWith("# Joe Pro label v1", ReportSerializer.Write(r));
    }
}
