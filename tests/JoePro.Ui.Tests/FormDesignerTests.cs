using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Core;
using JoePro.Documents;
using JoePro.Documents.Design;
using JoePro.Ide;
using JoePro.Runtime;

namespace JoePro.Ui.Tests;

public class FormDesignerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-designer-tests", Guid.NewGuid().ToString("N"));
    private readonly List<Window> _windows = new();

    public FormDesignerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
    }

    private FormDesigner NewDesigner(string name = "frmTest", string? path = null)
    {
        var designer = new FormDesigner(path, DesignSession.NewForm(name), _dir, dark: false);
        var window = new Window { Width = 1200, Height = 800, Content = designer };
        window.Show();
        _windows.Add(window);
        Dispatcher.UIThread.RunJobs();
        return designer;
    }

    private static string? P(FormDesigner d, string path, string property) => d.Session.GetProperty(path, property);

    [AvaloniaFact]
    public void New_form_renders_with_its_caption_and_size()
    {
        var d = NewDesigner();
        Assert.Null(d.RenderError);
        Assert.NotNull(d.Rendered);
        Assert.Equal(375, d.Rendered!.Width);
        Assert.Equal(250, d.Rendered.Height);
        Assert.Equal("", Assert.Single(d.Selection));
    }

    [AvaloniaFact]
    public void Toolbox_click_adds_a_control_snapped_to_the_grid()
    {
        var d = NewDesigner();
        d.ArmedTool = "TextBox";
        d.PointerDown(new Point(26, 40));
        d.PointerUp(new Point(26, 40));
        Assert.Equal("Text1", Assert.Single(d.Selection));
        Assert.Equal("24", P(d, "Text1", "Left"));
        Assert.Equal("36", P(d, "Text1", "Top"));
        Assert.Equal("100", P(d, "Text1", "Width"));
        Assert.Null(d.ArmedTool);
        Assert.IsType<TextBox>(d.Find("Text1")!.Native);
    }

    [AvaloniaFact]
    public void Dragging_a_tool_sets_the_size()
    {
        var d = NewDesigner();
        d.ArmedTool = "CommandButton";
        d.PointerDown(new Point(12, 12));
        d.PointerMove(new Point(100, 40));
        d.PointerUp(new Point(100, 40));
        Assert.Equal("Command1", d.Selection[0]);
        Assert.Equal("84", P(d, "Command1", "Width"));
        Assert.Equal("24", P(d, "Command1", "Height"));
        Assert.Equal("\"Command1\"", P(d, "Command1", "Caption"));
    }

    [AvaloniaFact]
    public void Drag_moves_and_handles_resize_with_undo_and_redo()
    {
        var d = NewDesigner();
        d.AddObjectAt("TextBox", new Point(24, 36));
        // Move by 25,25: snapped to 24,24.
        d.PointerDown(new Point(40, 45));
        d.PointerMove(new Point(65, 70));
        d.PointerUp(new Point(65, 70));
        Assert.Equal("48", P(d, "Text1", "Left"));
        Assert.Equal("60", P(d, "Text1", "Top"));
        // Bottom-right handle.
        d.PointerDown(new Point(148, 83));
        d.PointerMove(new Point(172, 107));
        d.PointerUp(new Point(172, 107));
        Assert.Equal("120", P(d, "Text1", "Width"));
        Assert.Equal("48", P(d, "Text1", "Height"));
        d.Undo();
        Assert.Equal("100", P(d, "Text1", "Width"));
        d.Undo();
        Assert.Equal("24", P(d, "Text1", "Left"));
        d.Redo();
        d.Redo();
        Assert.Equal("120", P(d, "Text1", "Width"));
        Assert.Equal(120, ((TextBox)d.Find("Text1")!.Native!).Width);
    }

    [AvaloniaFact]
    public void Clicking_empty_space_selects_the_form_and_a_band_selects_several()
    {
        var d = NewDesigner();
        d.AddObjectAt("Label", new Point(12, 12));
        d.AddObjectAt("Label", new Point(12, 48));
        d.PointerDown(new Point(300, 200));
        d.PointerUp(new Point(300, 200));
        Assert.Equal("", d.Selection[0]);
        d.PointerDown(new Point(2, 2));
        d.PointerMove(new Point(200, 100));
        d.PointerUp(new Point(200, 100));
        Assert.Equal(["Label1", "Label2"], d.Selection.OrderBy(x => x).ToArray());
        d.Align("Left");
        d.MoveSelection(5, 0);
        Assert.Equal("17", P(d, "Label1", "Left"));
        Assert.Equal("17", P(d, "Label2", "Left"));
    }

    [AvaloniaFact]
    public void Property_sheet_edits_follow_vfp_rules_and_rename_keeps_the_selection()
    {
        var d = NewDesigner();
        d.AddObjectAt("Label", new Point(12, 12));
        d.Properties.Commit("Caption", "Customer name");
        Assert.Equal("\"Customer name\"", P(d, "Label1", "Caption"));
        d.Properties.Commit("Width", "=40+2");
        Assert.Equal("40+2", P(d, "Label1", "Width"));
        Assert.Equal(42, d.Find("Label1")!.FindProperty("Width")!.Value.AsNumber);
        d.Properties.Commit("Name", "lblName");
        Assert.Equal("lblName", d.Selection[0]);
        Assert.NotNull(d.Find("lblName"));
        Assert.Contains("Caption", d.Properties.VisibleProperties);
    }

    [AvaloniaFact]
    public void Code_pane_stores_method_code()
    {
        var d = NewDesigner();
        d.AddObjectAt("CommandButton", new Point(12, 12));
        d.OpenMethod("Command1", "Click");
        Assert.Equal("Command1.Click", d.OpenMethodName);
        d.CodeEditor.Text = "THISFORM.Release()";
        d.CommitCode();
        Assert.Equal("THISFORM.Release()", d.Session.GetMethod("Command1", "Click")!.Trim());
        Assert.Contains("PROCEDURE Command1.Click", d.Session.Text);
    }

    [AvaloniaFact]
    public void Copy_paste_and_delete()
    {
        var d = NewDesigner();
        d.AddObjectAt("TextBox", new Point(12, 12));
        d.CopySelection();
        d.Paste();
        Assert.Equal("Text2", d.Selection[0]);
        Assert.Equal(2, d.Session.Class.Members.Count);
        d.Select(["Text1", "Text2"]);
        d.DeleteSelection();
        Assert.Empty(d.Session.Class.Members);
        d.Undo();
        Assert.Equal(2, d.Session.Class.Members.Count);
    }

    [AvaloniaFact]
    public void Objects_dropped_on_a_page_frame_go_to_its_active_page()
    {
        var d = NewDesigner();
        d.AddObjectAt("PageFrame", new Point(12, 12));
        d.Select([""]);
        d.AddObjectAt("TextBox", new Point(48, 84));
        Assert.Equal("Pageframe1.Page1.Text1", d.Selection[0]);
        Assert.StartsWith("ADD OBJECT Pageframe1.Page1.Text1", d.Session.Text.Split('\n').Select(l => l.Trim()).First(l => l.Contains("Page1.Text1")));
    }

    [AvaloniaFact]
    public void Saved_form_is_canonical_and_runs()
    {
        var path = Path.Combine(_dir, "orders.jpform");
        var d = NewDesigner("orders", path);
        d.AddObjectAt("TextBox", new Point(24, 24));
        d.Properties.Commit("Value", "hello");
        d.Save();
        Assert.False(d.Session.IsDirty);
        var text = File.ReadAllText(path);
        Assert.StartsWith("*-- Joe Pro form v1", text);
        Assert.Equal(text, ClassFileWriter.Write(ClassFileReader.Parse(text)));

        var rt = new Interpreter(new TextWriterOutput(TextWriter.Null), _dir);
        rt.ExecuteCommand("SET TALK OFF");
        rt.ExecuteCommand("DO FORM orders NAME oForm NOSHOW");
        var form = (VfpObject)rt.GetVariable("oForm").AsObject;
        Assert.Equal("hello", ((VfpObject)form.FindProperty("Text1")!.Value.AsObject).FindProperty("Value")!.Value.AsString);
        rt.Session.Dispose();
    }

    [AvaloniaFact]
    public void Data_environment_fields_become_bound_controls()
    {
        var rt = new Interpreter(new TextWriterOutput(TextWriter.Null), _dir);
        rt.ExecuteCommand("SET TALK OFF");
        rt.ExecuteCommand("CREATE TABLE customer (name C(20), active L, notes M)");
        rt.ExecuteCommand("CLOSE TABLES ALL");
        rt.Session.Dispose();

        var d = NewDesigner("custform", Path.Combine(_dir, "custform.jpform"));
        var alias = d.AddTable(Path.Combine(_dir, "customer.jpt"));
        Assert.Equal("customer", alias);
        Assert.Equal(["NAME", "ACTIVE", "NOTES"], d.FieldsOf("customer").Select(f => f.Name.ToUpperInvariant()).ToArray());
        var (label, control) = d.AddFieldControls("customer", "name");
        Assert.Equal("lblName", label);
        Assert.Equal("txtName", control);
        Assert.Equal("\"customer.name\"", P(d, "txtName", "ControlSource"));
        var (noLabel, check) = d.AddFieldControls("customer", "active");
        Assert.Equal("", noLabel);
        Assert.Equal("chkActive", check);
        Assert.Equal("edtNotes", d.AddFieldControls("customer", "notes").Control);
        Assert.Equal("grdCustomer", d.AddGridFor("customer"));
        Assert.Contains("ADD OBJECT Dataenvironment.Cursor1 AS Cursor", d.Session.Text);
        Assert.Contains("CursorSource = \"customer.jpt\"", d.Session.Text);
        // Controls stack below each other.
        Assert.True(double.Parse(P(d, "chkActive", "Top")!) > double.Parse(P(d, "txtName", "Top")!));
    }
}

public class IdeFormDesignerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-ide-designer-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public IdeFormDesignerTests()
    {
        Directory.CreateDirectory(_dir);
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        _session.Execute("SET TALK OFF");
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    [AvaloniaFact]
    public void Create_form_opens_the_designer_and_modify_form_reuses_it()
    {
        _session.Execute("CREATE FORM invoice");
        var tab = Assert.IsType<FormDesignerTab>(_window.Documents.SelectedItem);
        Assert.Equal(Path.Combine(_dir, "invoice.jpform"), tab.Designer.FilePath);
        tab.Designer.AddObjectAt("Label", new Point(12, 12));
        tab.Designer.Save();
        Assert.True(File.Exists(Path.Combine(_dir, "invoice.jpform")));
        _window.Documents.SelectedIndex = 0;
        _session.Execute("MODIFY FORM invoice");
        Assert.Same(tab, _window.Documents.SelectedItem);
        Assert.Single(_window.Documents.Items.OfType<FormDesignerTab>());
    }

    [AvaloniaFact]
    public void Run_from_the_designer_runs_the_form()
    {
        _session.Execute("CREATE FORM hello");
        var tab = Assert.IsType<FormDesignerTab>(_window.Documents.SelectedItem);
        tab.Designer.Properties.Commit("Caption", "Hello there");
        _window.RunActive();
        Dispatcher.UIThread.RunJobs();
        var form = Assert.Single(_session.Host.OpenForms);
        Assert.Equal("Hello there", form.FindProperty("Caption")!.Value.AsString);
        _session.Runtime.Release(form);
    }
}
