using JoePro.Documents.Reports;
using JoePro.Reports;
using JoePro.Tests.Documents;

namespace JoePro.Tests.Runtime;

/// <summary>REPORT FORM / LABEL FORM: layout, calculations, groups, pages and output formats.</summary>
public class ReportTests : RuntimeHarness
{
    private ReportEngine Engine => (ReportEngine)Rt.Reports!;
    private RenderedReport Last => Engine.LastReport!;

    private static IEnumerable<string> Texts(ReportPage page) => page.Items.OfType<TextItem>().OrderBy(t => Math.Round(t.Y)).ThenBy(t => t.X).Select(t => t.Text);

    private const string Orders = """
        CREATE TABLE orders (id I, cust C(10), amount N(10,2), note M)
        INSERT INTO orders VALUES (1, "Acme", 100, "")
        INSERT INTO orders VALUES (2, "Acme", 250.5, "")
        INSERT INTO orders VALUES (3, "Globex", 75, "")
        INSERT INTO orders VALUES (4, "Initech", 20, "")
        INSERT INTO orders VALUES (5, "Initech", 30, "")
        INDEX ON cust TAG cust
        GO TOP
        """;

    private static ReportField F(string expr, double left, double width = 1, TextAlign align = TextAlign.Left) =>
        new() { Expression = expr, Left = left, Width = width, Height = 0.2, Align = align };

    private string SaveGroupedReport()
    {
        var r = ReportDocument.NewReport();
        r.Band(BandKind.PageHeader)!.Objects.Add(new ReportLabel { Text = "Orders", Left = 0, Width = 2, Height = 0.2, Bold = true });
        r.Groups.Add(new ReportGroup { Expression = "orders.cust" });
        r.Bands.Add(new ReportBand { Kind = BandKind.GroupHeader, Index = 1, Height = 0.25, Objects = { F("orders.cust", 0, 2) } });
        r.Bands.Add(new ReportBand
        {
            Kind = BandKind.GroupFooter, Index = 1, Height = 0.25,
            Objects = { new ReportLabel { Text = "Subtotal", Left = 2, Width = 1, Height = 0.2 }, new ReportField { Expression = "orders.amount", Calculate = CalcType.Sum, Reset = ResetScope.Group, ResetGroup = 1, Left = 4, Width = 1.2, Height = 0.2, Format = "99,999.99", Align = TextAlign.Right } },
        });
        r.Bands.Add(new ReportBand
        {
            Kind = BandKind.Summary, Height = 0.3,
            Objects = { new ReportLabel { Text = "Total", Left = 2, Width = 1, Height = 0.2 }, new ReportField { Expression = "orders.amount", Calculate = CalcType.Sum, Left = 4, Width = 1.2, Height = 0.2, Format = "99,999.99", Align = TextAlign.Right },
                        new ReportField { Expression = "orders.id", Calculate = CalcType.Count, Left = 5.5, Width = 0.5, Height = 0.2 } },
        });
        r.SortBands();
        r.Band(BandKind.Detail)!.Objects.Add(F("orders.id", 0.5, 0.5));
        r.Band(BandKind.Detail)!.Objects.Add(new ReportField { Expression = "orders.amount", Left = 4, Width = 1.2, Height = 0.2, Format = "99,999.99", Align = TextAlign.Right });
        r.Band(BandKind.PageFooter)!.Objects.Add(new ReportField { Expression = "\"Page \" + TRANSFORM(_PAGENO) + \" of \" + TRANSFORM(_PAGETOTAL)", Left = 3, Width = 2, Height = 0.2 });
        var path = Path.Combine(Dir, "orders.jpreport");
        r.Save(path);
        return path;
    }

    [Fact]
    public void Grouped_report_with_subtotals_total_count_and_page_x_of_y()
    {
        SaveGroupedReport();
        var o = Run(Orders + "\nREPORT FORM orders");
        var page = Assert.Single(Last.Pages);
        Assert.Equal(["Orders", "Acme", "1", "100.00", "2", "250.50", "Subtotal", "350.50", "Globex", "3", "75.00", "Subtotal", "75.00",
            "Initech", "4", "20.00", "5", "30.00", "Subtotal", "50.00", "Total", "475.50", "5", "Page 1 of 1"], Texts(page));
        Assert.Contains("Subtotal", o); // printed as text on the screen
        Assert.Contains("475.50", o);
        Assert.True(Rt.Evaluate("EOF()").AsBool); // like LIST, the report ends at the end of the file
    }

    [Fact]
    public void Scope_for_clause_summary_and_output_files()
    {
        SaveGroupedReport();
        Run(Orders + "\nREPORT FORM orders FOR amount > 50 TO FILE out.txt ASCII NOCONSOLE");
        var text = File.ReadAllText(Path.Combine(Dir, "out.txt"));
        Assert.Contains("350.50", text);
        Assert.DoesNotContain("Initech", text);
        Run("REPORT FORM orders SUMMARY TO FILE sum.txt ASCII");
        Assert.DoesNotContain("250.50", File.ReadAllText(Path.Combine(Dir, "sum.txt")));
        Assert.Contains("475.50", File.ReadAllText(Path.Combine(Dir, "sum.txt")));

        Run("REPORT FORM orders TO FILE out.pdf");
        var pdf = File.ReadAllBytes(Path.Combine(Dir, "out.pdf"));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Contains("FontFile", System.Text.Encoding.Latin1.GetString(pdf)); // fonts are embedded

        Run("REPORT FORM orders TO FILE out.html");
        Assert.Contains("Subtotal</div>", File.ReadAllText(Path.Combine(Dir, "out.html")));
        Run("REPORT FORM orders TO FILE out.xml");
        Assert.Contains("<text x=", File.ReadAllText(Path.Combine(Dir, "out.xml")));
        Run("REPORT FORM orders TO FILE page.png");
        var png = File.ReadAllBytes(Path.Combine(Dir, "page.png"));
        Assert.Equal(0x89, png[0]);
    }

    [Fact]
    public void Page_breaks_reprint_headers_new_page_groups_and_range()
    {
        var r = ReportDocument.NewReport();
        r.PageHeight = 3; // a short page: 2 in of printable height
        r.TopMargin = r.BottomMargin = 0.5;
        r.Band(BandKind.PageHeader)!.Height = 0.25;
        r.Band(BandKind.PageHeader)!.Objects.Add(new ReportLabel { Text = "Header", Width = 1, Height = 0.2 });
        r.Band(BandKind.PageFooter)!.Height = 0.25;
        r.Band(BandKind.PageFooter)!.Objects.Add(F("_PAGENO", 0, 0.5));
        r.Band(BandKind.Detail)!.Objects.Add(F("orders.id", 0, 0.5));
        r.Groups.Add(new ReportGroup { Expression = "orders.cust", NewPage = true });
        r.Bands.Add(new ReportBand { Kind = BandKind.GroupHeader, Index = 1, Height = 0.25, Objects = { F("orders.cust", 0, 2) } });
        r.SortBands();
        r.Save(Path.Combine(Dir, "paged.jpreport"));
        Run(Orders + "\nREPORT FORM paged NOCONSOLE");
        Assert.Equal(3, Last.Pages.Count); // one page per customer
        Assert.Equal(["Header", "Globex", "3", "2"], Texts(Last.Pages[1]));

        // Many rows: they flow over pages, each with its header and page number.
        Run("CREATE TABLE nums (n I)\nFOR i = 1 TO 30\nINSERT INTO nums VALUES (i)\nENDFOR\nGO TOP");
        r.Groups.Clear();
        r.Bands.RemoveAll(b => b.Kind == BandKind.GroupHeader);
        r.Band(BandKind.Detail)!.Objects[0] = F("nums.n", 0, 0.5);
        r.Save(Path.Combine(Dir, "paged.jpreport"));
        Run("REPORT FORM paged NOCONSOLE");
        var pages = Last.Pages;
        Assert.True(pages.Count >= 5);
        Assert.All(pages, p => Assert.Equal("Header", Texts(p).First()));
        Assert.Equal(pages.Count.ToString(), Texts(pages[^1]).Last());
        Assert.Equal(30, pages.Sum(p => Texts(p).Count() - 2));
        Run("REPORT FORM paged RANGE 2, 3 NOCONSOLE");
        Assert.Equal([2, 3], Last.Pages.Select(p => p.Number));
    }

    [Fact]
    public void Variables_print_when_repeated_values_stretch_and_float()
    {
        var r = ReportDocument.NewReport();
        r.Variables.Add(new ReportVariable { Name = "nRunning", Value = "orders.amount", Calculate = CalcType.Sum });
        r.Variables.Add(new ReportVariable { Name = "cLabel", Value = "UPPER(orders.cust)" });
        var detail = r.Band(BandKind.Detail)!;
        detail.Objects.Add(new ReportField { Expression = "orders.cust", Width = 1, Height = 0.2, PrintRepeated = false });
        detail.Objects.Add(F("nRunning", 1.5, 1));
        detail.Objects.Add(new ReportField { Expression = "\"big\"", Left = 3, Width = 1, Height = 0.2, PrintWhen = "orders.amount > 200" });
        detail.Objects.Add(new ReportField { Expression = "orders.note", Left = 4.5, Width = 1, Height = 0.2, Stretch = true });
        detail.Objects.Add(new ReportField { Expression = "cLabel", Top = 0.25, Width = 1, Height = 0.2, Float = FloatMode.Float });
        detail.Height = 0.5;
        r.Save(Path.Combine(Dir, "vars.jpreport"));
        Run(Orders + "\nREPLACE note WITH \"one two three four five six seven eight nine ten eleven twelve\" FOR id = 2\nGO TOP\nREPORT FORM vars NOCONSOLE");
        var texts = Texts(Last.Pages[0]).ToList();
        Assert.Equal(1, texts.Count(t => t == "Acme")); // repeated value suppressed
        Assert.Contains("350.50", texts);                // running sum after the second record
        Assert.Equal(1, texts.Count(t => t == "big"));   // print when
        var note = Last.Pages[0].Items.OfType<TextItem>().Single(t => t.Text.StartsWith("one"));
        Assert.True(note.Lines.Count > 1);               // stretched
        var floated = Last.Pages[0].Items.OfType<TextItem>().Where(t => t.Text == "ACME").ToList();
        Assert.Equal(2, floated.Count);
        var detailTop2 = floated[1].Y - 0.25 * 72;
        Assert.True(floated[1].Y >= note.Y + note.H - 0.5); // floated below the stretched note
        Assert.Equal(Orders.Length > 0, Rt.Session.Current.InUse);
        Assert.Throws<JoePro.Core.VfpException>(() => Rt.Evaluate("nRunning")); // released after the report
    }

    [Fact]
    public void Labels_print_across_columns()
    {
        var label = new ReportDocument { Kind = ReportKind.Label, Columns = 3, ColumnWidth = 2.5, ColumnSpacing = 0.125, ColumnOrder = ColumnOrder.Across, TopMargin = 0.5, LeftMargin = 0.25 };
        label.Bands.Add(new ReportBand { Kind = BandKind.Detail, Index = 1, Height = 1, Objects = { F("orders.cust", 0.1, 2), F("TRANSFORM(orders.id)", 0.1, 2) } });
        label.Bands[0].Objects[1].Top = 0.25;
        label.Save(Path.Combine(Dir, "addr.jplabel"));
        Run(Orders + "\nLABEL FORM addr NOCONSOLE");
        var items = Last.Pages[0].Items.OfType<TextItem>().Where(t => t.Font.Size > 0 && t.Text.Length > 1 || char.IsDigit(t.Text[0])).ToList();
        var ids = items.Where(t => t.Text.Length == 1).OrderBy(t => t.Y).ThenBy(t => t.X).ToList();
        Assert.Equal(5, ids.Count);
        Assert.Equal(ids[0].Y, ids[2].Y, 3);     // first row: labels 1-3
        Assert.True(ids[3].Y > ids[0].Y + 70);  // second row one inch lower
        Assert.Equal((2.5 + 0.125) * 72, ids[1].X - ids[0].X, 3);
    }

    [Fact]
    public void Legacy_frx_runs_directly_with_its_data_environment()
    {
        Run("""
            CREATE TABLE items (name C(10), qty N(5))
            INSERT INTO items VALUES ("Bolt", 5)
            INSERT INTO items VALUES ("Nut", 7)
            USE
            """);
        new Frx().Band(1, 0.3).Band(4, 0.2).Band(8, 0.3)
            .Label(1, "Items", 0, 0, 1)
            .Field(4, "items.name", 0, 0, 1).Field(4, "items.qty", 0, 2, 0.5)
            .Field(8, "items.qty", 0.05, 2, 0.5, 0.2, ("TOTALTYPE", 2), ("RESETTOTAL", 1))
            .DataEnvironment("Top = 0\nName = \"Dataenvironment\"")
            .Cursor("Alias = \"items\"\nCursorSource = items.dbf\nName = \"Cursor1\"")
            .Save(Path.Combine(Dir, "items.frx"));
        Run("REPORT FORM items.frx NOCONSOLE");
        Assert.Equal(["Items", "Bolt", "5", "Nut", "7", "12"], Texts(Last.Pages[0]));
        Assert.Contains(StatusMessages, m => m.Contains("items.frx was converted in memory"));
        Assert.False(Rt.Evaluate("USED('items')").AsBool); // the data environment closed its table
    }

    [Fact]
    public void Report_listener_can_change_field_text_and_suppress_items()
    {
        SaveGroupedReport();
        var o = Run(Orders + """

            oListener = CREATEOBJECT("MyListener")
            REPORT FORM orders OBJECT oListener NOCONSOLE
            ? oListener.cLog
            DEFINE CLASS MyListener AS ReportListener
               cLog = ""
               PROCEDURE BeforeReport
                  This.cLog = This.cLog + "B"
               ENDPROC
               PROCEDURE AfterReport
                  This.cLog = This.cLog + "A" + TRANSFORM(This.PageTotal)
               ENDPROC
               PROCEDURE EvaluateContents(nRec, oProps)
                  IF oProps.Text == "Globex"
                     oProps.Text = "GLOBEX CORP"
                     oProps.Reload = .T.
                  ENDIF
               ENDPROC
               PROCEDURE Render(nRec, nLeft, nTop, nWidth, nHeight, nCont, cText, oImage)
                  IF cText == "Subtotal"
                     NODEFAULT
                  ENDIF
               ENDPROC
            ENDDEFINE
            """);
        var texts = Texts(Last.Pages[0]).ToList();
        Assert.Contains("GLOBEX CORP", texts);
        Assert.DoesNotContain("Subtotal", texts);
        Assert.Contains("A1", o.Trim());
    }

    [Fact]
    public void Create_report_from_a_table_writes_a_quick_report()
    {
        Run(Orders + "\nUSE\nCREATE REPORT quick FROM orders FIELDS id, amount");
        var doc = ReportDocument.Load(Path.Combine(Dir, "quick.jpreport"));
        Assert.Equal(["orders.id", "orders.amount"], doc.Band(BandKind.Detail)!.Objects.OfType<ReportField>().Select(f => f.Expression));
        Assert.False(Rt.Evaluate("USED('orders')").AsBool);
        Run("USE orders\nREPORT FORM quick NOCONSOLE");
        Assert.Contains("250.50", Texts(Last.Pages[0]));
    }

    [Fact]
    public void Missing_report_and_bad_expressions_raise_errors()
    {
        var o = Run("""
            TRY
               REPORT FORM nosuch
            CATCH TO e
               ? e.ErrorNo
            ENDTRY
            """);
        Assert.Equal("1", o.Trim());
        var r = ReportDocument.NewReport();
        r.Band(BandKind.Detail)!.Objects.Add(F("undefinedvar + 1", 0));
        r.Save(Path.Combine(Dir, "bad.jpreport"));
        var ex = Assert.Throws<JoePro.Core.VfpException>(() => Rt.ExecuteCommand("REPORT FORM bad NOCONSOLE"));
        Assert.Contains("UNDEFINEDVAR", ex.Message.ToUpperInvariant());
    }
}
