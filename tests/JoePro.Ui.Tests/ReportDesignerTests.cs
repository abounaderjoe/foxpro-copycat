using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Documents.Reports;
using JoePro.Ide;
using JoePro.Reports;

namespace JoePro.Ui.Tests;

public class ReportDesignerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-report-designer-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public ReportDesignerTests()
    {
        Directory.CreateDirectory(_dir);
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        _session.Execute("SET TALK OFF");
        _session.Execute("""
            CREATE TABLE customer (name C(20), city C(15), balance N(10,2))
            INSERT INTO customer VALUES ("Acme", "Boston", 1200)
            INSERT INTO customer VALUES ("Globex", "Chicago", 300.5)
            GO TOP
            """);
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    private ReportDesigner Active() => Assert.IsType<ReportDesignerTab>(_window.Documents.SelectedItem).Designer;

    [AvaloniaFact]
    public void Create_report_opens_the_designer_with_default_bands()
    {
        _session.Execute("CREATE REPORT sales");
        var d = Active();
        Assert.Equal(Path.Combine(_dir, "sales.jpreport"), d.FilePath);
        Assert.Equal(["PageHeader", "Detail", "PageFooter"], d.Session.Document.Bands.Select(b => b.Kind.ToString()));
        Assert.Contains("customer.name", d.AvailableFields());
    }

    [AvaloniaFact]
    public void Toolbox_fields_move_resize_band_height_properties_and_undo()
    {
        _session.Execute("CREATE REPORT sales");
        var d = Active();
        d.LivePreview = false;
        var detailTop = d.BandTopPx(1);
        d.ArmedTool = "Label";
        d.PointerDown(new Point(20, 10));
        d.PointerUp(new Point(20, 10));
        var label = d.Selection.Single();
        Assert.Equal(0, label.Band); // page header
        Assert.Equal(0.1875, d.Session.Get(label)!.Left); // snapped to 1/16 in
        d.SetProperty("Text", "Customer");
        d.SetProperty("Bold", "true");
        d.SetProperty("ForeColor", "0,0,255");
        var l = (ReportLabel)d.Session.Get(label)!;
        Assert.Equal(("Customer", true, 0xFF0000), (l.Text, l.Bold, l.ForeColor!.Value));

        var field = d.AddField("customer.name", band: 1);
        Assert.Equal("customer.name", ((ReportField)d.Session.Get(field)!).Expression);
        Assert.Contains(d.Session.Document.Band(BandKind.PageHeader)!.Objects.OfType<ReportLabel>(), x => x.Text == "Name");

        // Drag the field 1 inch to the right.
        var r = d.RectOf(d.Selection[0]);
        d.PointerDown(new Point(r.X + 5, r.Y + 5));
        d.PointerMove(new Point(r.X + 5 + 96, r.Y + 5));
        d.PointerUp(new Point(r.X + 5 + 96, r.Y + 5));
        Assert.Equal(1, d.Session.Get(d.Selection[0])!.Left, 3);

        // Drag the detail band's bar down a quarter inch.
        var barY = d.BandTopPx(1) + d.Session.Document.Bands[1].Height * ReportDesigner.Dpi + 5;
        d.PointerDown(new Point(50, barY));
        d.PointerMove(new Point(50, barY + 24));
        d.PointerUp(new Point(50, barY + 24));
        Assert.Equal(0.5, d.Session.Document.Bands[1].Height, 3);

        d.Undo();
        Assert.Equal(0.25, d.Session.Document.Bands[1].Height, 3);
        d.Select([]);
        d.SetProperty("Height", "0.4"); // the band last clicked
        Assert.Equal(0.4, d.Session.Document.Bands[1].Height, 3);
    }

    [AvaloniaFact]
    public void Live_preview_save_and_run_in_the_preview_tab()
    {
        _session.Execute("CREATE REPORT sales");
        var d = Active();
        d.QuickReport("customer");
        d.RefreshPreview();
        Assert.Null(d.PreviewError);
        var page = Assert.Single(d.PreviewReport!.Pages);
        Assert.Contains(page.Items.OfType<TextItem>(), t => t.Text == "Globex");
        d.Save();
        Assert.StartsWith("# Joe Pro report v1", File.ReadAllText(Path.Combine(_dir, "sales.jpreport")));
        _window.RunActive();
        Dispatcher.UIThread.RunJobs();
        var preview = Assert.IsType<ReportPreviewTab>(_window.Documents.SelectedItem).Preview;
        Assert.Equal(1, preview.Report.Pages.Count);
        Assert.True(preview.FindNext("Chicago"));
        Assert.Single(preview.Matches);
        Assert.False(preview.FindNext("Nowhere"));
    }

    [AvaloniaFact]
    public void Groups_variables_and_title_summary_through_the_session()
    {
        _session.Execute("CREATE REPORT grouped");
        var d = Active();
        d.LivePreview = false;
        d.Session.AddGroup("customer.city");
        d.Session.SetTitleSummary(true, true);
        var summary = d.Session.Document.Bands.FindIndex(b => b.Kind == BandKind.Summary);
        d.Session.AddObject(summary, new ReportField { Expression = "customer.balance", Calculate = CalcType.Sum, Width = 1, Height = 0.2, Format = "99,999.99" });
        d.Session.AddObject(1 + d.Session.Document.Bands.FindIndex(b => b.Kind == BandKind.PageHeader), new ReportField { Expression = "customer.city", Width = 1, Height = 0.2 });
        d.RefreshPreview();
        Assert.Null(d.PreviewError);
        Assert.True(d.PreviewReport!.Pages[0].Items.OfType<TextItem>().Any(t => t.Text.Trim() == "1,500.50"), string.Join("|", d.PreviewReport!.Pages[0].Items.OfType<TextItem>().Select(t => t.Text)));
    }

    [AvaloniaFact]
    public void Legacy_frx_opens_converted_and_new_labels_use_presets()
    {
        var frx = Path.Combine(_dir, "old.frx");
        new JoePro.Tests.Documents.Frx().Band(1, 0.3).Band(4, 0.2).Label(1, "Old report", 0, 0, 2).Field(4, "customer.name", 0, 0, 2).Save(frx);
        _window.OpenAny(frx);
        var d = Active();
        Assert.Equal(Path.Combine(_dir, "old.jpreport"), d.FilePath);
        Assert.Contains(d.Session.Document.Band(BandKind.PageHeader)!.Objects.OfType<ReportLabel>(), l => l.Text == "Old report");

        var label = LabelPresets.Create(LabelPresets.All[0]);
        Assert.Equal((3, 2.625, ColumnOrder.Across), (label.Columns, label.ColumnWidth, label.ColumnOrder));
        Assert.Equal(1, label.Band(BandKind.Detail)!.Height);
    }
}
