using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using JoePro.Core;
using JoePro.Runtime;
using JoePro.Ui.Runtime;

namespace JoePro.Ui.Tests;

public class FormRuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-ui-tests", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly Interpreter _rt;
    private readonly AvaloniaUiHost _host;
    private readonly List<VfpException> _errors = new();

    public FormRuntimeTests()
    {
        Directory.CreateDirectory(_dir);
        _rt = new Interpreter(new TextWriterOutput(_out), _dir);
        _rt.ExecuteCommand("SET TALK OFF");
        _host = new AvaloniaUiHost(_rt);
        _host.Error += _errors.Add;
    }

    public void Dispose()
    {
        foreach (var f in _host.OpenForms.ToList()) _rt.Release(f);
        _rt.Session.Dispose();
    }

    private VfpObject Form(string variable = "oForm") => (VfpObject)_rt.GetVariable(variable).AsObject;

    private T Control<T>(VfpObject form, string path) where T : Control =>
        (T)(AvaloniaUiHost.Find(form, path)?.Native ?? throw new InvalidOperationException($"{path} has no control"));

    private static void LoseFocus(Control c) => c.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private const string CustomerTable = """
        CREATE TABLE customer (id I, name C(20), active L)
        INSERT INTO customer VALUES (1, "Smith", .T.)
        INSERT INTO customer VALUES (2, "Jones", .F.)
        GO TOP
        """;

    [AvaloniaFact]
    public void Form_inputs_get_accessible_names_from_labels_and_tooltips()
    {
        _rt.ExecuteCommand("""
            oForm = CREATEOBJECT("NamedForm")
            oForm.Show()
            DEFINE CLASS NamedForm AS Form
               ADD OBJECT lblName AS Label WITH Caption = "\<Customer:", Left = 10, Top = 20, Width = 70, Height = 17
               ADD OBJECT txtName AS TextBox WITH Left = 90, Top = 18, Height = 23
               ADD OBJECT lblFar AS Label WITH Caption = "Other", Left = 10, Top = 120, Width = 70, Height = 17
               ADD OBJECT txtPhone AS TextBox WITH Left = 90, Top = 50, Height = 23, ToolTipText = "Phone number"
               ADD OBJECT cmdOk AS CommandButton WITH Caption = "OK", Left = 90, Top = 90
            ENDDEFINE
            """);
        string Name(string path) => Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(Control<Control>(Form(), path)).GetName();
        Assert.Equal("Customer", Name("txtName"));
        Assert.Equal("Phone number", Name("txtPhone"));
        Assert.Equal("OK", Name("cmdOk"));
    }

    [AvaloniaFact]
    public void On_key_label_runs_its_command_when_the_key_is_pressed_in_a_form()
    {
        _rt.ExecuteCommand("""
            PUBLIC gHits
            gHits = 0
            ON KEY LABEL CTRL+F2 gHits = gHits + 1
            oForm = CREATEOBJECT("Form")
            oForm.Show()
            """);
        var window = (Window)Form().Native!;
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2, KeyModifiers = KeyModifiers.Control });
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F3, KeyModifiers = KeyModifiers.Control });
        Assert.Empty(_errors);
        Assert.Equal(1, _rt.GetVariable("gHits").AsNumber);
        _rt.ExecuteCommand("ON KEY LABEL CTRL+F2");
        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2, KeyModifiers = KeyModifiers.Control });
        Assert.Equal(1, _rt.GetVariable("gHits").AsNumber);
    }

    [AvaloniaFact]
    public void Shows_form_with_bound_textbox_and_writes_back_on_valid()
    {
        _rt.ExecuteCommand(CustomerTable + """

            oForm = CREATEOBJECT("CustForm")
            oForm.Show()
            DEFINE CLASS CustForm AS Form
               Caption = "Customers"
               Width = 400
               Height = 200
               Changes = 0
               ADD OBJECT lblName AS Label WITH Caption = "Name:", Left = 10, Top = 12
               ADD OBJECT txtName AS TextBox WITH ControlSource = "customer.name", Left = 80, Top = 10, Width = 200
               PROCEDURE txtName.InteractiveChange
                  ThisForm.Changes = ThisForm.Changes + 1
               ENDPROC
            ENDDEFINE
            """);
        var form = Form();
        var window = _host.WindowOf(form)!;
        Assert.True(window.IsVisible);
        Assert.Equal("Customers", window.Title);
        var tb = Control<TextBox>(form, "txtName");
        Assert.Equal("Smith", tb.Text);
        Assert.Equal("Name:", Control<TextBlock>(form, "lblName").Text);

        tb.Text = "Smythe";
        Assert.Equal(1, _rt.Evaluate("oForm.Changes").AsNumber);
        Assert.Equal("Smith", _rt.Evaluate("ALLTRIM(customer.name)").AsString); // not written until the control loses focus
        LoseFocus(tb);
        Assert.Empty(_errors);
        Assert.Equal("Smythe", _rt.Evaluate("ALLTRIM(customer.name)").AsString);
    }

    [AvaloniaFact]
    public void Valid_returning_false_keeps_the_old_value_in_the_table()
    {
        _rt.ExecuteCommand(CustomerTable + """

            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               ADD OBJECT txtName AS TextBox WITH ControlSource = "customer.name"
               PROCEDURE txtName.Valid
                  RETURN !EMPTY(This.Value)
               ENDPROC
            ENDDEFINE
            """);
        var tb = Control<TextBox>(Form(), "txtName");
        tb.Text = "";
        LoseFocus(tb);
        Assert.Equal("Smith", _rt.Evaluate("ALLTRIM(customer.name)").AsString);
    }

    [AvaloniaFact]
    public void Button_click_runs_code_and_refresh_updates_controls()
    {
        _rt.ExecuteCommand(CustomerTable + """

            oForm = CREATEOBJECT("NavForm")
            oForm.Show()
            DEFINE CLASS NavForm AS Form
               ADD OBJECT txtName AS TextBox WITH ControlSource = "customer.name"
               ADD OBJECT chkActive AS CheckBox WITH ControlSource = "customer.active", Caption = "Active", Top = 30
               ADD OBJECT cmdNext AS CommandButton WITH Caption = "\<Next", Top = 60
               ADD OBJECT lblStatus AS Label WITH Top = 100, Caption = ""
               PROCEDURE cmdNext.Click
                  SKIP
                  ThisForm.lblStatus.Caption = "Record " + TRANSFORM(RECNO())
                  ThisForm.Refresh()
               ENDPROC
            ENDDEFINE
            """);
        var form = Form();
        Assert.Equal(true, Control<CheckBox>(form, "chkActive").IsChecked);
        Assert.Equal("_Next", Control<Button>(form, "cmdNext").Content);
        Click(Control<Button>(form, "cmdNext"));
        Assert.Empty(_errors);
        Assert.Equal("Jones", Control<TextBox>(form, "txtName").Text);
        Assert.Equal(false, Control<CheckBox>(form, "chkActive").IsChecked);
        Assert.Equal("Record 2", Control<TextBlock>(form, "lblStatus").Text);

        Control<CheckBox>(form, "chkActive").IsChecked = true;
        Assert.True(_rt.Evaluate("customer.active").AsBool);
    }

    [AvaloniaFact]
    public void Page_frames_option_groups_and_combo_boxes()
    {
        _rt.ExecuteCommand("""
            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               Picked = ""
               ADD OBJECT pgf AS PageFrame WITH PageCount = 3, Width = 300, Height = 200
               ADD OBJECT pgf.Page2.txtNote AS TextBox WITH Value = "on page 2"
               ADD OBJECT opgSize AS OptionGroup WITH ButtonCount = 3, Top = 210, Height = 80
               ADD OBJECT cboColor AS ComboBox WITH RowSourceType = 1, RowSource = "Red,Green,Blue", Top = 300
               PROCEDURE cboColor.InteractiveChange
                  ThisForm.Picked = This.Value
               ENDPROC
            ENDDEFINE
            """);
        var form = Form();
        var tabs = Control<TabControl>(form, "pgf");
        Assert.Equal(3, tabs.ItemCount);
        Assert.Equal("on page 2", Control<TextBox>(form, "pgf.Page2.txtNote").Text);

        var opg = AvaloniaUiHost.Find(form, "opgSize")!;
        Assert.Equal(3, opg.Members.Count);
        Control<RadioButton>(form, "opgSize.Option2").IsChecked = true;
        Assert.Equal(2, _rt.Evaluate("oForm.opgSize.Value").AsNumber);
        Assert.Equal(1, _rt.Evaluate("oForm.opgSize.Option2.Value").AsNumber);

        var combo = Control<ComboBox>(form, "cboColor");
        Assert.Equal(3, combo.ItemCount);
        combo.SelectedIndex = 1;
        Assert.Equal("Green", _rt.Evaluate("oForm.Picked").AsString);

        tabs.SelectedIndex = 2;
        Assert.Equal(3, _rt.Evaluate("oForm.pgf.ActivePage").AsNumber);
    }

    [AvaloniaFact]
    public void Code_changes_update_controls()
    {
        _rt.ExecuteCommand("""
            oForm = CREATEOBJECT("F")
            oForm.Show()
            oForm.lbl.Caption = "Changed"
            oForm.txt.Value = 42
            oForm.txt.Enabled = .F.
            oForm.Caption = "New title"
            DEFINE CLASS F AS Form
               ADD OBJECT lbl AS Label WITH Caption = "Before"
               ADD OBJECT txt AS TextBox WITH Top = 30, Value = 0
            ENDDEFINE
            """);
        var form = Form();
        Assert.Equal("Changed", Control<TextBlock>(form, "lbl").Text);
        Assert.Equal("42", Control<TextBox>(form, "txt").Text);
        Assert.False(Control<TextBox>(form, "txt").IsEnabled);
        Assert.Equal("New title", _host.WindowOf(form)!.Title);
    }

    [AvaloniaFact]
    public void Grid_shows_record_source_and_edits_write_back()
    {
        _rt.ExecuteCommand(CustomerTable + """

            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               ADD OBJECT grd AS Grid WITH RecordSource = "customer"
            ENDDEFINE
            """);
        var grid = Control<DataGrid>(Form(), "grd");
        var rows = (System.Collections.ObjectModel.ObservableCollection<BrowseRow>)grid.ItemsSource!;
        Assert.Equal(2, rows.Count);
        Assert.Equal("Jones", rows[1][1]);
        Assert.Equal(4, grid.Columns.Count); // deleted mark + 3 fields
    }

    [AvaloniaFact]
    public void Release_runs_destroy_and_unload_and_closes_the_window()
    {
        _rt.ExecuteCommand("""
            PUBLIC gcLog
            gcLog = ""
            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               ADD OBJECT txt AS TextBox
               PROCEDURE Load
                  gcLog = gcLog + "Load,"
               ENDPROC
               PROCEDURE txt.Init
                  gcLog = gcLog + "txt.Init,"
               ENDPROC
               PROCEDURE Init
                  gcLog = gcLog + "Init,"
               ENDPROC
               PROCEDURE Destroy
                  gcLog = gcLog + "Destroy,"
               ENDPROC
               PROCEDURE Unload
                  gcLog = gcLog + "Unload"
               ENDPROC
            ENDDEFINE
            """);
        var form = Form();
        var window = _host.WindowOf(form)!;
        _rt.ExecuteCommand("oForm.Release()");
        Assert.Equal("Load,txt.Init,Init,Destroy,Unload", _rt.GetVariable("gcLog").AsString);
        Assert.Null(_host.WindowOf(form));
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Closing_the_window_asks_query_unload_which_can_cancel()
    {
        _rt.ExecuteCommand("""
            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               AllowClose = .F.
               PROCEDURE QueryUnload
                  IF !This.AllowClose
                     NODEFAULT
                  ENDIF
               ENDPROC
            ENDDEFINE
            """);
        var form = Form();
        var window = _host.WindowOf(form)!;
        window.Close();
        Assert.True(window.IsVisible);
        Assert.False(form.Released);
        _rt.ExecuteCommand("oForm.AllowClose = .T.");
        window.Close();
        Assert.True(form.Released);
    }

    [AvaloniaFact]
    public void Do_form_runs_a_jpform_file()
    {
        File.WriteAllText(Path.Combine(_dir, "hello.jpform"), """
            *-- Joe Pro form v1
            DEFINE CLASS frmHello AS Form
               Caption = "Hello"
               ADD OBJECT lblGreeting AS Label WITH Caption = "Hi"
               PROCEDURE Init(cWho)
                  This.lblGreeting.Caption = "Hi " + cWho
               ENDPROC
            ENDDEFINE
            """);
        _rt.ExecuteCommand("DO FORM hello NAME oHello WITH 'Joe'");
        var form = Form("oHello");
        Assert.Equal("Hello", _host.WindowOf(form)!.Title);
        Assert.Equal("Hi Joe", Control<TextBlock>(form, "lblGreeting").Text);
    }

    [AvaloniaFact]
    public void Private_data_sessions_isolate_work_areas()
    {
        _rt.ExecuteCommand(CustomerTable + """

            USE
            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               DataSession = 2
               PROCEDURE Load
                  USE customer
               ENDPROC
               ADD OBJECT txtName AS TextBox WITH ControlSource = "customer.name"
            ENDDEFINE
            """);
        var form = Form();
        Assert.Equal("Smith", Control<TextBox>(form, "txtName").Text);
        Assert.False(_rt.Evaluate("USED('customer')").AsBool);
        Assert.NotEqual(_rt.Session.Id, (int)_rt.Evaluate("oForm.DataSessionId").AsNumber);
        _rt.ExecuteCommand("oForm.Release()");
    }

    [AvaloniaFact]
    public void Timer_fires_its_event()
    {
        _rt.ExecuteCommand("""
            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               Ticks = 0
               ADD OBJECT tmr AS Timer WITH Interval = 10
               PROCEDURE tmr.Timer
                  ThisForm.Ticks = ThisForm.Ticks + 1
                  IF ThisForm.Ticks >= 2
                     This.Enabled = .F.
                  ENDIF
               ENDPROC
            ENDDEFINE
            """);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_rt.Evaluate("oForm.Ticks").AsNumber < 2 && DateTime.UtcNow < deadline)
            Avalonia.Threading.Dispatcher.UIThread.MainLoop(new CancellationTokenSource(50).Token);
        Assert.Equal(2, _rt.Evaluate("oForm.Ticks").AsNumber);
    }
}
