using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using JoePro.Ide;

// Renders screenshots of the IDE and a sample form without a display (headless Avalonia + Skia).
// Usage: dotnet run --project tools/JoePro.Screenshots -- <output folder>
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/images");
Directory.CreateDirectory(output);
AppBuilder.Configure<ShotApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var work = Path.Combine(Path.GetTempPath(), "joepro-shots-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
File.WriteAllText(Path.Combine(work, "customer.jpform"), """
    *-- Joe Pro form v1
    DEFINE CLASS frmCustomer AS Form
       Caption = "Customers"
       Width = 460
       Height = 330
       ADD OBJECT lblName AS Label WITH Caption = "Name", Left = 16, Top = 18, Width = 60
       ADD OBJECT txtName AS TextBox WITH ControlSource = "customer.name", Left = 90, Top = 14, Width = 220
       ADD OBJECT lblCity AS Label WITH Caption = "City", Left = 16, Top = 52, Width = 60
       ADD OBJECT txtCity AS TextBox WITH ControlSource = "customer.city", Left = 90, Top = 48, Width = 160
       ADD OBJECT chkActive AS CheckBox WITH Caption = "Active customer", ControlSource = "customer.active", Left = 90, Top = 84, Width = 160
       ADD OBJECT pgf AS PageFrame WITH Left = 16, Top = 118, Width = 428, Height = 150
       ADD OBJECT pgf.Page1.grdOrders AS Grid WITH RecordSource = "orders", Left = 4, Top = 4, Width = 414, Height = 108
       ADD OBJECT cmdPrev AS CommandButton WITH Caption = "\<Previous", Left = 250, Top = 284, Width = 90
       ADD OBJECT cmdNext AS CommandButton WITH Caption = "\<Next", Left = 354, Top = 284, Width = 90
       PROCEDURE Init
          This.pgf.Page1.Caption = "Orders"
          This.pgf.Page2.Caption = "Notes"
       ENDPROC
       PROCEDURE cmdNext.Click
          SKIP
          IF EOF()
             GO BOTTOM
          ENDIF
          ThisForm.Refresh()
       ENDPROC
       PROCEDURE cmdPrev.Click
          SKIP -1
          ThisForm.Refresh()
       ENDPROC
    ENDDEFINE
    """);
File.WriteAllText(Path.Combine(work, "report.prg"), """
    * Top customers by order total
    LOCAL lnTotal
    SELECT c.name, SUM(o.amount) AS total ;
       FROM customer c JOIN orders o ON o.custid = c.id ;
       GROUP BY c.name ORDER BY total DESC INTO CURSOR top
    SCAN
       ? PADR(name, 20), total
    ENDSCAN
    """);

// JOEPRO_SHOTS_THEME=dark renders the designer screenshots in the dark theme (to check it; docs use light).
var designerVariant = Environment.GetEnvironmentVariable("JOEPRO_SHOTS_THEME") == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
foreach (var (variant, file) in new[] { (ThemeVariant.Light, "ide-light.png"), (ThemeVariant.Dark, "ide-dark.png") })
{
    Application.Current!.RequestedThemeVariant = variant;
    var dir = Path.Combine(work, variant.ToString()!);
    Directory.CreateDirectory(dir);
    foreach (var f in Directory.GetFiles(work)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
    using var session = new IdeSession(dir);
    var window = new MainWindow(session) { Width = 1280, Height = 800 };
    window.Show();
    window.Run("SET TALK OFF");
    window.Run("CREATE TABLE customer (id I, name C(20), city C(15), active L)");
    window.Run("INSERT INTO customer VALUES (1, 'Acme Corp', 'Boston', .T.)");
    window.Run("INSERT INTO customer VALUES (2, 'Globex', 'Chicago', .T.)");
    window.Run("INSERT INTO customer VALUES (3, 'Initech', 'Austin', .F.)");
    window.Run("CREATE TABLE orders (id I, custid I, amount N(10,2))");
    window.Run("INSERT INTO orders VALUES (1, 1, 1250.00)");
    window.Run("INSERT INTO orders VALUES (2, 2, 980.50)");
    window.Run("INSERT INTO orders VALUES (3, 1, 310.25)");
    window.Run("SELECT customer");
    window.Run("LIST name, city");
    window.Run("DO report");
    window.Run("MODIFY COMMAND report");
    window.Run("SELECT customer");
    window.Run("BROWSE");
    window.Documents.SelectedIndex = 0;
    Pump();
    Save(window, Path.Combine(output, file));

    if (variant == designerVariant)
    {
        window.Documents.SelectedIndex = 1;
        Pump();
        Save(window, Path.Combine(output, "ide-editor.png"));
        window.Run("GO TOP\nDO FORM customer NAME oCust");
        var form = session.Host.OpenForms.First();
        var fw = session.Host.WindowOf(form)!;
        Pump();
        Save(fw, Path.Combine(output, "form-customer.png"));
        session.Runtime.Release(form);

        window.Run("MODIFY FORM customer");
        var designer = ((FormDesignerTab)window.Documents.SelectedItem!).Designer;
        designer.OpenMethod("cmdNext", "Click");
        designer.Select(["txtName"]);
        Pump();
        Save(window, Path.Combine(output, "form-designer.png"));

        window.Run("""
            CREATE CLASS txtBase OF controls AS TextBox
            CREATE CLASS txtDate OF controls AS txtBase
            CREATE CLASS cntAddress OF controls AS Container
            CREATE CLASS cmdOk OF controls AS CommandButton
            """);
        var lib = JoePro.Documents.ClassLibrary.Load(Path.Combine(dir, "controls.jpclass"));
        var txtBase = lib.Find("txtBase")!;
        txtBase.Properties["nMaxLength"] = "40";
        txtBase.MemberDescriptions["nMaxLength"] = "Longest text allowed";
        txtBase.Methods.Add(new JoePro.Documents.MethodDocument { Name = "Validate", Code = "RETURN LEN(ALLTRIM(This.Value)) <= This.nMaxLength" });
        txtBase.Description = "Base text box for the application";
        lib.Find("txtDate")!.Methods.Add(new JoePro.Documents.MethodDocument { Name = "Valid", Code = "RETURN This.Validate() AND !EMPTY(CTOD(This.Value))" });
        JoePro.Documents.ClassFileWriter.Save(lib, Path.Combine(dir, "controls.jpclass"));
        var browser = window.OpenClassBrowser(Path.Combine(dir, "controls.jpclass")).Browser;
        browser.SelectClass("txtDate");
        Pump();
        Save(window, Path.Combine(output, "class-browser.png"));

        File.Copy(Path.Combine(AppContext.BaseDirectory, "orders.jpreport"), Path.Combine(dir, "orders.jpreport"), overwrite: true);
        window.Run("SELECT c.name, c.city, o.id, o.amount FROM customer c JOIN orders o ON o.custid = c.id ORDER BY c.name, o.id INTO CURSOR rpt");
        window.Width = 1600;
        window.Height = 1150;
        window.Run("MODIFY REPORT orders");
        var reportDesigner = ((ReportDesignerTab)window.Documents.SelectedItem!).Designer;
        reportDesigner.RefreshPreview();
        reportDesigner.Select([new JoePro.Documents.Reports.ObjectRef(reportDesigner.Session.Document.Bands.FindIndex(b => b.Kind == JoePro.Documents.Reports.BandKind.Detail), 2)]);
        Pump();
        Save(window, Path.Combine(output, "report-designer.png"));
        window.Run("REPORT FORM orders PREVIEW");
        Pump();
        Save(window, Path.Combine(output, "report-preview.png"));

        // Phase 5: database, table, query and menu designers, the Project Manager and the migration report.
        window.Width = 1440;
        window.Height = 900;
        window.Run("""
            CLOSE TABLES ALL
            CREATE DATABASE sales
            CREATE TABLE client (id I AUTOINC, name C(30) NOT NULL, city C(20), since D)
            ALTER TABLE client ADD PRIMARY KEY id TAG id
            INDEX ON UPPER(name) TAG name
            CREATE TABLE invoice (id I AUTOINC, clientid I, issued D, total Y)
            ALTER TABLE invoice ADD PRIMARY KEY id TAG id
            ALTER TABLE invoice ADD FOREIGN KEY clientid TAG clientid REFERENCES client ON DELETE CASCADE ON INSERT RESTRICT
            CREATE TABLE line (id I AUTOINC, invoiceid I, item C(20), qty N(6), price Y)
            ALTER TABLE line ADD FOREIGN KEY invoiceid TAG invoiceid REFERENCES invoice ON DELETE CASCADE
            INSERT INTO client (name, city, since) VALUES ('Acme Corp', 'Boston', DATE())
            INSERT INTO client (name, city, since) VALUES ('Globex', 'Chicago', DATE())
            INSERT INTO invoice (clientid, issued, total) VALUES (1, DATE(), 1250)
            INSERT INTO invoice (clientid, issued, total) VALUES (1, DATE(), 310.25)
            INSERT INTO invoice (clientid, issued, total) VALUES (2, DATE(), 980.5)
            CREATE SQL VIEW bigclients AS SELECT client.name, SUM(invoice.total) AS total FROM client INNER JOIN invoice ON invoice.clientid = client.id GROUP BY client.name
            CLOSE TABLES ALL
            """);
        var dbDesigner = window.OpenDatabaseDesigner(Path.Combine(dir, "sales.jpdb"));
        dbDesigner.Select(null, dbDesigner.Relations.First(r => r.ChildTable == "INVOICE"));
        Pump();
        Save(window, Path.Combine(output, "database-designer.png"));
        var tableDesigner = window.OpenTableDesigner(Path.Combine(dir, "sales.jpdb"), "client");
        tableDesigner.SelectField("name");
        tableDesigner.UpdateField(f => f with { Caption = "Client name", RuleExpr = "!EMPTY(name)", RuleText = "A client needs a name" });
        Pump();
        Save(window, Path.Combine(output, "table-designer.png"));
        var query = window.OpenQuery(null);
        query.AddTable("client");
        query.AddTable("invoice");
        query.Join("client", "id", "invoice", "clientid");
        query.AddField("client.name");
        query.AddField("COUNT(*)", "invoices");
        query.AddField("SUM(invoice.total)", "total");
        query.Document.GroupBy.Add("client.name");
        query.AddOrder("3", descending: true);
        query.RunPreview();
        Pump();
        Save(window, Path.Combine(output, "query-designer.png"));

        var menu = new JoePro.Documents.Menus.MenuDocument();
        var fileMenu = new JoePro.Documents.Menus.MenuNode { Prompt = "\\<File" };
        fileMenu.Items.Add(new JoePro.Documents.Menus.MenuNode { Prompt = "\\<New invoice", KeyName = "CTRL+N", KeyText = "Ctrl+N", Command = "DO FORM invoice" });
        fileMenu.Items.Add(new JoePro.Documents.Menus.MenuNode { Prompt = "\\-" });
        fileMenu.Items.Add(new JoePro.Documents.Menus.MenuNode { Prompt = "E\\<xit", Command = "CLEAR EVENTS" });
        var reportsMenu = new JoePro.Documents.Menus.MenuNode { Prompt = "\\<Reports" };
        reportsMenu.Items.Add(new JoePro.Documents.Menus.MenuNode { Prompt = "\\<Orders", Command = "REPORT FORM orders PREVIEW", SkipFor = "!USED('rpt')" });
        menu.Items.Add(fileMenu);
        menu.Items.Add(reportsMenu);
        menu.Save(Path.Combine(dir, "main.jpmenu"));
        window.OpenMenu(Path.Combine(dir, "main.jpmenu"));
        Pump();
        Save(window, Path.Combine(output, "menu-designer.png"));

        var project = new JoePro.Documents.Projects.ProjectDocument { Name = "billing" };
        foreach (var f in new[] { "report.prg", "customer.jpform", "orders.jpreport", "controls.jpclass", "main.jpmenu", "sales.jpdb" }) project.Add(f);
        project.Main = "report.prg";
        project.Save(Path.Combine(dir, "billing.jpproj"));
        window.Run("MODIFY PROJECT billing");
        Pump();
        Save(window, Path.Combine(output, "project-manager.png"));

        var legacy = Path.Combine(work, "legacy-app");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "main.prg"), "* Order entry\nDECLARE INTEGER GetTickCount IN kernel32\nSET LIBRARY TO foxtools.fll\nDO FORM orders\nREAD EVENTS\n");
        File.WriteAllText(Path.Combine(legacy, "util.prg"), "FUNCTION Tax(n)\nRETURN n * 0.08\n");
        var wizard = window.OpenMigrationWizard(legacy);
        wizard.TargetFolder = Path.Combine(work, "legacy-app-joepro");
        wizard.ShowReportWhenDone = true;
        wizard.OpenProjectWhenDone = false;
        wizard.SetDefaultWhenDone = false;
        wizard.Next();
        var import = wizard.ImportAsync();
        while (!import.IsCompleted) Dispatcher.UIThread.RunJobs();
        Pump();
        Save(window, Path.Combine(output, "migration-report.png"));
    }
    window.Close();
}
Console.WriteLine($"Screenshots written to {output}");

static void Pump()
{
    for (int i = 0; i < 5; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}

static void Save(Window w, string path)
{
    var frame = w.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame rendered.");
#pragma warning disable CS0618
    frame.Save(path);
#pragma warning restore CS0618
    Console.WriteLine(path);
}

sealed class ShotApp : Application
{
    public override void Initialize()
    {
        Styles.Add(JoePro.Ide.IdeTheme.CreateFluent());
        JoePro.Ide.IdeTheme.AddResources(this);
        var b = new Uri("avares://JoePro.Screenshots/");
        Styles.Add(new StyleInclude(b) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(b) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
    }
}
