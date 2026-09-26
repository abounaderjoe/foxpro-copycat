using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Core;
using JoePro.Documents;
using JoePro.Ide;
using JoePro.Runtime;

namespace JoePro.Ui.Tests;

public class ClassDesignerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-classdesigner-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public ClassDesignerTests()
    {
        Directory.CreateDirectory(_dir);
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        _session.Execute("SET TALK OFF");
    }

    public void Dispose()
    {
        foreach (var f in _session.Host.OpenForms.ToList()) _session.Runtime.Release(f);
        _window.Close();
        _session.Dispose();
    }

    private FormDesigner ActiveDesigner() => Assert.IsType<FormDesignerTab>(_window.Documents.SelectedItem).Designer;

    [AvaloniaFact]
    public void Create_class_opens_the_class_designer_on_a_container_class()
    {
        Assert.True(_session.Execute("CREATE CLASS cntAddress OF controls AS Container"));
        var d = ActiveDesigner();
        Assert.True(d.IsClass);
        Assert.Equal("cntAddress", d.Session.ClassName);
        Assert.Equal(Path.Combine(_dir, "controls.jpclass"), d.FilePath);
        Assert.Null(d.RenderError);
        Assert.Equal("Container", d.Root!.Class.BaseClass);

        d.AddObjectAt("TextBox", new Point(12, 12));
        Assert.Equal("Text1", d.Selection[0]);
        d.Properties.Commit("Width", "80");
        d.Select([""]);
        d.Properties.Commit("Width", "240");
        d.Properties.Commit("Height", "60");
        d.Save();

        var lib = ClassLibrary.Load(Path.Combine(_dir, "controls.jpclass"));
        var cls = lib.Find("cntAddress")!;
        Assert.Equal("240", cls.Properties["Width"]);
        Assert.Equal("Text1", Assert.Single(cls.Members).Path);

        // The runtime sees the saved class at once.
        _session.Execute("o = NEWOBJECT(\"cntAddress\", \"controls\")");
        var o = (VfpObject)_session.Runtime.GetVariable("o").AsObject;
        Assert.Equal(240, o.FindProperty("Width")!.Value.AsNumber);
        Assert.Equal(80, ((VfpObject)o.FindProperty("Text1")!.Value.AsObject).FindProperty("Width")!.Value.AsNumber);
    }

    [AvaloniaFact]
    public void Controls_cannot_contain_objects_but_have_custom_members()
    {
        _session.Execute("CREATE CLASS cmdOk OF buttons AS CommandButton");
        var d = ActiveDesigner();
        Assert.Equal("", d.AddObjectAt("TextBox", new Point(5, 5)));
        Assert.IsType<Button>(d.Root!.Native);

        d.NewProperty("lConfirm", ".T.", "PROTECTED", "Ask before closing", access: false, assign: true);
        d.NewMethod("DoConfirm", description: "Asks the user");
        Assert.Equal("DoConfirm", d.OpenMethodName);
        d.CodeEditor.Text = "RETURN MESSAGEBOX(\"Sure?\", 4) = 6";
        d.CommitCode();
        var members = d.CustomMembers();
        Assert.Contains(members, m => m is { Name: "lConfirm", Kind: "Property", Visibility: "PROTECTED", Description: "Ask before closing", InheritedFrom: null });
        Assert.Contains(members, m => m is { Name: "DoConfirm", Kind: "Method", InheritedFrom: null });
        Assert.Contains(members, m => m.Name == "lConfirm_Assign");

        // The description pane shows the custom property's description.
        d.Select([""]);
        d.Properties.ShowDescription("lConfirm");
        Assert.Equal("lConfirm: Ask before closing", d.Properties.DescriptionText);
        d.Properties.ShowDescription("Caption");
        Assert.StartsWith("Caption: ", d.Properties.DescriptionText);

        d.SetMemberVisibility("lConfirm", null);
        d.RemoveMember("DoConfirm");
        Assert.DoesNotContain(d.CustomMembers(), m => m.Name == "DoConfirm");
        d.SetClassInfo("The OK button", null, null, olePublic: false);
        d.Save();
        var text = File.ReadAllText(Path.Combine(_dir, "buttons.jpclass"));
        Assert.Contains("*-- Description: The OK button", text);
        Assert.Contains("*-- Member lConfirm: Ask before closing", text);
        Assert.DoesNotContain("PROTECTED lConfirm", text);
    }

    [AvaloniaFact]
    public void Subclass_designer_lists_inherited_members_and_saving_keeps_other_classes()
    {
        _session.Execute("CREATE CLASS cusBase OF biz AS Custom");
        var baseDesigner = ActiveDesigner();
        baseDesigner.NewProperty("nTotal", "0", description: "Running total");
        baseDesigner.NewMethod("Recalc");
        baseDesigner.Save();
        _session.Execute("CREATE CLASS cusOrder OF biz AS cusBase");
        var sub = ActiveDesigner();
        Assert.NotSame(baseDesigner, sub);
        var inherited = sub.CustomMembers().Where(m => m.InheritedFrom != null).ToList();
        Assert.Contains(inherited, m => m is { Name: "nTotal", InheritedFrom: "cusbase" } or { Name: "nTotal", InheritedFrom: "cusBase" });
        Assert.Contains(inherited, m => m.Name.Equals("Recalc", StringComparison.OrdinalIgnoreCase));
        sub.Properties.ShowDescription("nTotal");
        Assert.Equal("nTotal: Running total", sub.Properties.DescriptionText);

        // Edit the base class again in its tab after the subclass was created: saving must keep cusOrder.
        baseDesigner.NewProperty("cCurrency", "\"USD\"");
        baseDesigner.Save();
        var lib = ClassLibrary.Load(Path.Combine(_dir, "biz.jpclass"));
        Assert.NotNull(lib.Find("cusOrder"));
        Assert.True(lib.Find("cusBase")!.Properties.Contains("cCurrency"));
    }

    [AvaloniaFact]
    public void Modify_class_reuses_the_open_designer_and_run_creates_a_preview()
    {
        _session.Execute("CREATE CLASS frmDialog OF forms AS Form");
        var d = ActiveDesigner();
        d.Properties.Commit("Caption", "My dialog");
        d.Save();
        _window.Documents.SelectedIndex = 0;
        _session.Execute("MODIFY CLASS frmDialog OF forms");
        Assert.Same(d, ActiveDesigner());
        _window.RunActive();
        Dispatcher.UIThread.RunJobs();
        var form = Assert.Single(_session.Host.OpenForms);
        Assert.Equal("My dialog", form.FindProperty("Caption")!.Value.AsString);
    }

    [AvaloniaFact]
    public void Class_browser_shows_the_hierarchy_members_and_code_and_edits_the_library()
    {
        _session.Execute("""
            CREATE CLASS txtBase OF ui AS TextBox
            CREATE CLASS txtDate OF ui AS txtBase
            CREATE CLASS cmdBase OF ui AS CommandButton
            """);
        var lib = ClassLibrary.Load(Path.Combine(_dir, "ui.jpclass"));
        lib.Find("txtBase")!.Properties["nMaxDays"] = "31";
        lib.Find("txtBase")!.MemberDescriptions["nMaxDays"] = "Days allowed";
        lib.Find("txtDate")!.Methods.Add(new MethodDocument { Name = "Valid", Code = "RETURN .T." });
        ClassFileWriter.Save(lib, Path.Combine(_dir, "ui.jpclass"));

        var tab = _window.OpenClassBrowser(Path.Combine(_dir, "ui.jpclass"));
        var b = tab.Browser;
        Assert.Equal(["cmdBase", "txtBase", "txtDate"], b.VisibleClasses);
        b.SelectClass("txtDate");
        var members = b.Members("txtDate");
        Assert.Contains(members, m => m is { Name: "Valid", Kind: "Method", InheritedFrom: null });
        Assert.Contains(members, m => m is { Name: "nMaxDays", Value: "31", Description: "Days allowed", InheritedFrom: "txtBase" });
        Assert.StartsWith("DEFINE CLASS txtDate AS txtBase", b.ClassCode);

        b.Filter = "date";
        Assert.Equal(["txtDate"], b.VisibleClasses);
        b.Filter = "";

        b.NewClass("txtNumber", "txtBase");
        Assert.Contains("txtNumber", b.VisibleClasses);
        b.RenameClass("txtBase", "txtCore");
        Assert.Equal("txtCore", ClassLibrary.Load(b.LibraryPath).Find("txtDate")!.ParentClass);
        b.Redefine("txtNumber", "Spinner", null);
        Assert.Equal("Spinner", ClassLibrary.Load(b.LibraryPath).Find("txtNumber")!.ParentClass);
        b.CopyClassTo("cmdBase", Path.Combine(_dir, "shared.jpclass"));
        Assert.NotNull(ClassLibrary.Load(Path.Combine(_dir, "shared.jpclass")).Find("cmdBase"));
        Assert.Empty(b.RemoveClass("cmdBase"));
        Assert.DoesNotContain("cmdBase", b.VisibleClasses);
        b.ExportCode("txtDate", Path.Combine(_dir, "txtdate.prg"));
        Assert.Contains("DEFINE CLASS txtDate AS txtCore", File.ReadAllText(Path.Combine(_dir, "txtdate.prg")));

        // The runtime picks up the renamed class.
        _session.Execute("o = NEWOBJECT(\"txtDate\", \"ui\")");
        Assert.Equal("txtcore", ((VfpObject)_session.Runtime.GetVariable("o").AsObject).FindProperty("ParentClass")!.Value.AsString.ToLowerInvariant());
    }

    [AvaloniaFact]
    public void Class_browser_opens_legacy_libraries_read_only_and_saves_them_as_jpclass()
    {
        foreach (var f in new[] { "cprogres.vcx", "cprogres.VCT" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "corpus", "foxunit", f), Path.Combine(_dir, f));
        _session.Execute("MODIFY CLASSLIB cprogres.vcx");
        var b = Assert.IsType<ClassBrowserTab>(_window.Documents.SelectedItem).Browser;
        Assert.True(b.IsLegacy);
        Assert.Contains("cprogressbar", b.VisibleClasses, StringComparer.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => b.NewClass("cusX", "Custom"));
        var saved = b.SaveAsJpclass();
        Assert.EndsWith("cprogres.jpclass", saved);
        Assert.False(b.IsLegacy);
        b.NewClass("cusX", "Custom");
        Assert.NotNull(ClassLibrary.Load(saved).Find("cusX"));
    }
}
