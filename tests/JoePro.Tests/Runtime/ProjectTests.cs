using JoePro.Documents.Projects;

namespace JoePro.Tests.Runtime;

/// <summary>Projects: BUILD PROJECT/APP/EXE, DO app.jpapp, CREATE PROJECT, legacy PJX files.</summary>
public class ProjectTests : RuntimeHarness
{
    private void WriteApp()
    {
        Directory.CreateDirectory(Path.Combine(Dir, "src", "forms"));
        Directory.CreateDirectory(Path.Combine(Dir, "src", "data"));
        File.WriteAllText(Path.Combine(Dir, "src", "main.prg"), "#INCLUDE app.h\nPUBLIC gcOut\ngcOut = GREETING + \" \" + Helper()\nSELECT 0\nUSE data/items\ngcOut = gcOut + \" \" + TRANSFORM(RECCOUNT())\n");
        File.WriteAllText(Path.Combine(Dir, "src", "app.h"), "#DEFINE GREETING \"hello\"\n");
        File.WriteAllText(Path.Combine(Dir, "src", "helper.prg"), "FUNCTION Helper\nRETURN \"from helper\"\n");
        File.WriteAllText(Path.Combine(Dir, "src", "forms", "about.jpform"), "*-- Joe Pro form v1\n\nDEFINE CLASS about AS Form\n    Caption = \"About\"\nENDDEFINE\n");
        Run("CREATE TABLE src/data/items (name C(10))\nINSERT INTO items VALUES ('a')\nINSERT INTO items VALUES ('b')\nUSE");
        var p = new ProjectDocument { Name = "demo" };
        foreach (var f in new[] { "main.prg", "helper.prg", "forms/about.jpform", "data/items.jpt" }) p.Add(f);
        p.Save(Path.Combine(Dir, "src", "demo.jpproj"));
    }

    [Fact]
    public void Build_app_packages_the_project_and_do_runs_it()
    {
        WriteApp();
        var project = ProjectDocument.Load(Path.Combine(Dir, "src", "demo.jpproj"));
        Assert.Equal("main.prg", project.Main);
        Assert.True(project.Find("data/items.jpt")!.Exclude); // data files start excluded
        Run("BUILD APP out/demo FROM src/demo");
        Assert.True(Rt.LastBuild!.Succeeded, string.Join("; ", Rt.LastBuild.Errors));
        Assert.True(File.Exists(Path.Combine(Dir, "out", "demo.jpapp")));
        Assert.DoesNotContain("data/items.jpt", Rt.LastBuild.Files);
        // Run it with SET DEFAULT where the data is.
        Run("SET DEFAULT TO src\nDO ../out/demo.jpapp");
        Assert.Equal("hello from helper 2", Rt.Evaluate("gcOut").AsString);
    }

    [Fact]
    public void Build_errors_are_reported_and_written_to_an_err_file()
    {
        WriteApp();
        File.WriteAllText(Path.Combine(Dir, "src", "helper.prg"), "FUNCTION Helper\nIF .T.\nRETURN 1\n"); // missing ENDIF
        var o = Run("BUILD PROJECT src/demo");
        Assert.False(Rt.LastBuild!.Succeeded);
        Assert.Contains(Rt.LastBuild.Errors, e => e.File == "helper.prg");
        Assert.Contains(StatusMessages, m => m.Contains("failed with 1 error"));
        var ex = Assert.Throws<JoePro.Core.VfpException>(() => Rt.ExecuteCommand("BUILD APP out/demo FROM src/demo"));
        Assert.Contains("Build failed", ex.Message);
        Assert.Contains("helper.prg", File.ReadAllText(Path.Combine(Dir, "out", "demo.err")));
    }

    [Fact]
    public void Build_exe_makes_a_folder_with_launchers_and_data()
    {
        WriteApp();
        Run("BUILD EXE dist/demo FROM src/demo");
        Assert.True(Rt.LastBuild!.Succeeded);
        var folder = Path.Combine(Dir, "dist", "demo");
        Assert.True(File.Exists(Path.Combine(folder, "demo.jpapp")));
        Assert.True(File.Exists(Path.Combine(folder, "demo.cmd")));
        Assert.Contains("joepro run", File.ReadAllText(Path.Combine(folder, "demo.sh")));
        Assert.True(File.Exists(Path.Combine(folder, "data", "items.jpt"))); // excluded data travels next to the app
    }

    [Fact]
    public void Create_project_without_a_project_manager_writes_an_empty_project()
    {
        Run("CREATE PROJECT inventory");
        var p = ProjectDocument.Load(Path.Combine(Dir, "inventory.jpproj"));
        Assert.Equal("inventory", p.Name);
        Assert.Empty(p.Files);
    }
}
