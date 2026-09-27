using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Documents.Projects;
using JoePro.Ide;

namespace JoePro.Ui.Tests;

public class ProjectManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-pm-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public ProjectManagerTests()
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

    [AvaloniaFact]
    public void Create_project_add_files_by_category_build_and_search()
    {
        _session.Execute("CREATE PROJECT sales");
        var pm = Assert.IsType<ProjectManagerTab>(_window.Documents.SelectedItem).Manager;
        Assert.True(File.Exists(Path.Combine(_dir, "sales.jpproj")));

        var main = pm.NewFile(ProjectFileType.Program, "main");
        File.WriteAllText(Path.Combine(_dir, "main.prg"), "PUBLIC gcRan\ngcRan = 'yes'\n* TODO: menus\n");
        pm.NewFile(ProjectFileType.Form, "forms/customer");
        pm.NewFile(ProjectFileType.Report, "reports/sales");
        pm.NewFile(ProjectFileType.Menu, "main");
        _session.Execute("CREATE TABLE items (name C(10))\nUSE");
        pm.AddFile(Path.Combine(_dir, "items.jpt"));
        Assert.Equal("main.prg", pm.Project.Main);
        Assert.Equal(["forms/customer.jpform", "reports/sales.jpreport"], pm.Visible("Documents"));
        Assert.Equal(["items.jpt"], pm.Visible("Data"));
        Assert.Equal(["main.jpmenu"], pm.Visible("Other"));
        pm.Filter = "cust";
        Assert.Equal(["forms/customer.jpform"], pm.Visible("All"));
        pm.Filter = "";
        // New files open in their designers.
        Assert.Contains(_window.Documents.Items.OfType<FormDesignerTab>(), t => t.Designer.FilePath!.EndsWith("customer.jpform"));
        Assert.Contains(_window.Documents.Items.OfType<ReportDesignerTab>(), t => t.Designer.FilePath!.EndsWith("sales.jpreport"));

        var check = pm.Build("PROJECT");
        Assert.True(check.Succeeded, string.Join("; ", check.Errors));
        var app = pm.Build("APP");
        Assert.True(File.Exists(app.Output));
        Assert.Contains(pm.Search("TODO"), h => h.File == "main.prg" && h.Line == 3);

        pm.Run("main.prg");
        Assert.Equal("yes", _session.Runtime.Evaluate("gcRan").AsString);
        Assert.Equal($"DO FORM \"{Path.Combine(_dir, "forms", "customer.jpform")}\"", pm.RunCommand("forms/customer.jpform"));

        pm.ToggleExclude("main.prg");
        Assert.True(pm.Project.Find("main.prg")!.Exclude);
        Assert.False(pm.Build("PROJECT").Succeeded); // the main program is excluded
        pm.SetMain("main.prg");
        Assert.False(pm.Project.Find("main.prg")!.Exclude);
        pm.RemoveFile("items.jpt");
        pm.Save();
        Assert.DoesNotContain("items.jpt", File.ReadAllText(Path.Combine(_dir, "sales.jpproj")));
    }

    [AvaloniaFact]
    public void Project_manager_publishes_and_adds_packages()
    {
        var lib = Path.Combine(_dir, "mylib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "util.prg"), "FUNCTION Double(n)\nRETURN n * 2\n");
        var libProject = new ProjectDocument { Name = "mylib", Version = "1.2.0" };
        libProject.Add("util.prg");
        libProject.Save(Path.Combine(lib, "mylib.jpproj"));
        _session.Execute($"MODIFY PROJECT \"{Path.Combine(lib, "mylib.jpproj")}\"");
        var libPm = Assert.IsType<ProjectManagerTab>(_window.Documents.SelectedItem).Manager;
        Assert.EndsWith(".jppkg", libPm.PublishPackage("../registry"));

        _session.Execute("CREATE PROJECT consumer");
        var pm = Assert.IsType<ProjectManagerTab>(_window.Documents.SelectedItem).Manager;
        pm.Project.Registry = "registry";
        Assert.Equal("1.2.0", pm.AddPackage("mylib"));
        Assert.Equal("^1.2.0", ProjectDocument.Load(Path.Combine(_dir, "consumer.jpproj")).Dependencies.Single().Version);
        _session.Execute("SET PROCEDURE TO util ADDITIVE");
        Assert.Equal(8, _session.Runtime.Evaluate("Double(4)").AsNumber);
        Assert.Empty(pm.RestorePackages().Messages);
        pm.RemovePackage("mylib");
        Assert.Empty(pm.Project.Dependencies);
    }
}
