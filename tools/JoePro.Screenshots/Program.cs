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

    if (variant == ThemeVariant.Light)
    {
        window.Documents.SelectedIndex = 1;
        Pump();
        Save(window, Path.Combine(output, "ide-editor.png"));
        window.Run("GO TOP\nDO FORM customer NAME oCust");
        var form = session.Host.OpenForms.First();
        var fw = session.Host.WindowOf(form)!;
        Pump();
        Save(fw, Path.Combine(output, "form-customer.png"));
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
        Styles.Add(new FluentTheme());
        var b = new Uri("avares://JoePro.Screenshots/");
        Styles.Add(new StyleInclude(b) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(b) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
    }
}
