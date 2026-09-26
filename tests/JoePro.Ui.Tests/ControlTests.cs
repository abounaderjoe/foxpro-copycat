using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using JoePro.Core;
using JoePro.Runtime;
using JoePro.Ui.Runtime;

namespace JoePro.Ui.Tests;

/// <summary>InputMask/Format, When refusing focus, grid dynamic properties, toolbars, form sets and hyperlinks.</summary>
public class ControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-control-tests", Guid.NewGuid().ToString("N"));
    private readonly Interpreter _rt;
    private readonly AvaloniaUiHost _host;
    private readonly List<VfpException> _errors = new();

    public ControlTests()
    {
        Directory.CreateDirectory(_dir);
        _rt = new Interpreter(new TextWriterOutput(TextWriter.Null), _dir);
        _rt.ExecuteCommand("SET TALK OFF");
        _host = new AvaloniaUiHost(_rt);
        _host.Error += _errors.Add;
    }

    public void Dispose()
    {
        foreach (var f in _host.OpenForms.ToList()) _rt.Release(f);
        _rt.Session.Dispose();
    }

    private VfpObject Obj(string variable = "oForm") => (VfpObject)_rt.GetVariable(variable).AsObject;

    private static T Control<T>(VfpObject form, string path) where T : Control =>
        (T)(AvaloniaUiHost.Find(form, path)?.Native ?? throw new InvalidOperationException($"{path} has no control"));

    [Fact]
    public void Character_masks_filter_and_insert_literals()
    {
        var phone = new MaskedInput(null, "(999) 999-9999");
        Assert.Equal("(555) 123-4567", phone.ApplyCharacterMask("5551234567"));
        Assert.Equal("(555) 12", phone.ApplyCharacterMask("(555) 12"));
        Assert.Equal("(555", phone.ApplyCharacterMask("5a5b5"));
        var code = new MaskedInput("R", "AA-999");
        Assert.Equal("ab-123", code.ApplyCharacterMask("ab123")); // A accepts letters as typed
        Assert.Equal("AB-123", new MaskedInput(null, "!!-999").ApplyCharacterMask("ab123"));
        Assert.Equal("ab123", code.StoredText("ab-123"));
        Assert.Equal("ab-123", code.EditText("ab123"));
        Assert.Equal("HELLO", new MaskedInput("!", null).ApplyCharacterMask("hello"));
        Assert.Equal("-12.5", MaskedInput.FilterNumber("-1x2.5.3", '.').Replace("3", ""));
    }

    [AvaloniaFact]
    public void Text_box_input_mask_and_format_apply_while_typing_and_when_displayed()
    {
        _rt.ExecuteCommand("""
            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               ADD OBJECT txtPhone AS TextBox WITH Value = "", InputMask = "999-9999", Format = "R"
               ADD OBJECT txtCode AS TextBox WITH Value = "", Format = "!", Top = 30
               ADD OBJECT txtAmount AS TextBox WITH Value = 1234.5, InputMask = "99,999.99", Top = 60
            ENDDEFINE
            """);
        var form = Obj();
        var phone = Control<TextBox>(form, "txtPhone");
        phone.Text = "5551234";
        Assert.Equal("555-1234", phone.Text);
        Assert.Equal("5551234", _rt.Evaluate("oForm.txtPhone.Value").AsString); // R: literals not stored
        var code = Control<TextBox>(form, "txtCode");
        code.Text = "abc";
        Assert.Equal("ABC", _rt.Evaluate("oForm.txtCode.Value").AsString);
        Assert.Equal(" 1,234.50", Control<TextBox>(form, "txtAmount").Text);
        var amount = Control<TextBox>(form, "txtAmount");
        amount.Text = "12a3";
        Assert.Equal("123", amount.Text);
        Assert.Equal(123, _rt.Evaluate("oForm.txtAmount.Value").AsNumber);
    }

    [AvaloniaFact]
    public void When_returning_false_refuses_the_focus()
    {
        _rt.ExecuteCommand("""
            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               Visited = ""
               ADD OBJECT txtA AS TextBox
               ADD OBJECT txtB AS TextBox WITH Top = 30
               ADD OBJECT txtC AS TextBox WITH Top = 60
               PROCEDURE txtB.When
                  RETURN .F.
               ENDPROC
               PROCEDURE txtB.GotFocus
                  ThisForm.Visited = ThisForm.Visited + "B"
               ENDPROC
               PROCEDURE txtC.GotFocus
                  ThisForm.Visited = ThisForm.Visited + "C"
               ENDPROC
            ENDDEFINE
            """);
        var form = Obj();
        _host.WindowOf(form)!.Activate();
        Dispatcher.UIThread.RunJobs();
        Control<TextBox>(form, "txtB").Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(_errors);
        Assert.Equal("C", _rt.Evaluate("oForm.Visited").AsString);
        Assert.True(Control<TextBox>(form, "txtC").IsFocused);
    }

    [AvaloniaFact]
    public void Grid_dynamic_properties_color_rows()
    {
        _rt.ExecuteCommand("""
            CREATE TABLE orders (id I, amount N(10,2))
            INSERT INTO orders VALUES (1, 50)
            INSERT INTO orders VALUES (2, 5000)
            GO TOP
            oForm = CREATEOBJECT("F")
            oForm.Show()
            DEFINE CLASS F AS Form
               Width = 400
               Height = 300
               ADD OBJECT grd AS Grid WITH RecordSource = "orders", ColumnCount = 2, Width = 380, Height = 250
               PROCEDURE Init
                  This.grd.Column1.ControlSource = "orders.id"
                  This.grd.Column2.ControlSource = "orders.amount"
                  This.grd.Column2.DynamicBackColor = "IIF(orders.amount > 1000, RGB(255,0,0), RGB(255,255,255))"
                  This.grd.Column2.DynamicFontBold = "orders.amount > 1000"
                  This.grd.Refresh()
               ENDPROC
            ENDDEFINE
            """);
        Assert.Empty(_errors);
        var grid = Control<DataGrid>(Obj(), "grd");
        var rows = (System.Collections.ObjectModel.ObservableCollection<BrowseRow>)grid.ItemsSource!;
        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].Styles![0]);
        Assert.Equal(255, rows[1].Styles![1]!.BackColor);
        Assert.Equal(true, rows[1].Styles![1]!.Bold);
        Assert.Equal(false, rows[0].Styles![1]!.Bold);
    }

    [AvaloniaFact]
    public void Toolbars_show_their_controls_side_by_side_and_hide_and_dock()
    {
        _rt.ExecuteCommand("""
            oTb = CREATEOBJECT("MyToolbar")
            oTb.Show()
            DEFINE CLASS MyToolbar AS Toolbar
               Caption = "Tools"
               Clicks = 0
               ADD OBJECT cmdNew AS CommandButton WITH Caption = "New", Width = 50
               ADD OBJECT sep1 AS Separator
               ADD OBJECT cmdOpen AS CommandButton WITH Caption = "Open", Width = 50
               PROCEDURE cmdOpen.Click
                  This.Parent.Clicks = This.Parent.Clicks + 1
               ENDPROC
            ENDDEFINE
            """);
        var tb = Obj("oTb");
        var window = _host.WindowOf(tb)!;
        Assert.True(window.IsVisible);
        Assert.Equal("Tools", window.Title);
        var strip = Assert.IsType<StackPanel>(window.Content);
        Assert.Equal(3, strip.Children.Count);
        Assert.Equal("Open", ((Button)strip.Children[2]).Content);
        ((Button)strip.Children[2]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, _rt.Evaluate("oTb.Clicks").AsNumber);
        _rt.ExecuteCommand("oTb.Dock(0)");
        Assert.True(_rt.Evaluate("oTb.Docked").AsBool);
        Assert.Equal(0, _rt.Evaluate("oTb.DockPosition").AsNumber);
        _rt.ExecuteCommand("oTb.Hide()");
        Assert.False(window.IsVisible);
        _rt.ExecuteCommand("RELEASE oTb");
        Assert.Null(_host.WindowOf(tb));
    }

    [AvaloniaFact]
    public void Form_sets_show_each_form_in_its_own_window()
    {
        _rt.ExecuteCommand("""
            oSet = CREATEOBJECT("MySet")
            oSet.Show()
            DEFINE CLASS MySet AS FormSet
               ADD OBJECT frmOne AS Form WITH Caption = "One", Visible = .T.
               ADD OBJECT frmTwo AS Form WITH Caption = "Two", Visible = .T.
            ENDDEFINE
            """);
        Assert.Empty(_errors);
        var set = Obj("oSet");
        Assert.Equal(["One", "Two"], _host.OpenForms.Select(f => _host.WindowOf(f)!.Title).Order().ToArray());
        Assert.Null(_host.WindowOf(set));
        _rt.ExecuteCommand("oSet.Release()");
        Assert.Empty(_host.OpenForms);
    }

    [Fact]
    public void Hyperlinks_navigate_and_keep_history()
    {
        var opened = new List<string>();
        _rt.UrlLauncher = opened.Add;
        _rt.ExecuteCommand("""
            oLink = CREATEOBJECT("Hyperlink")
            oLink.NavigateTo("https://example.com/a")
            oLink.NavigateTo("https://example.com/b")
            oLink.GoBack()
            oLink.GoForward()
            lOk = oLink.GoForward()
            """);
        Assert.Equal(["https://example.com/a", "https://example.com/b", "https://example.com/a", "https://example.com/b"], opened);
        Assert.False(_rt.GetVariable("lOk").AsBool);
    }
}
