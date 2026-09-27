using JoePro.Runtime;
using JoePro.Core;
using JoePro.Documents.Projects;

namespace JoePro.Tests.Runtime;

/// <summary>Packages: publish a class library, consume it with version pinning, folder sources, builds.</summary>
public class PackageTests : RuntimeHarness
{
    private string Registry => Path.Combine(Dir, "registry");

    /// <summary>A library project with one class whose Greet() returns a greeting for the given version.</summary>
    private string Library(string version, string greeting)
    {
        var dir = Path.Combine(Dir, "uikit");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "uikit.jpclass"), $"""
            *-- Joe Pro class library v1

            DEFINE CLASS Greeter AS Custom
                PROCEDURE Greet(tcName)
                    RETURN "{greeting} " + tcName
                ENDPROC
            ENDDEFINE
            """);
        var p = new ProjectDocument { Name = "uikit", Version = version };
        p.Add("uikit.jpclass");
        p.Save(Path.Combine(dir, "uikit.jpproj"));
        return dir;
    }

    private (ProjectDocument Project, string Dir) App()
    {
        var dir = Path.Combine(Dir, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.prg"), "PUBLIC gcOut\nSET CLASSLIB TO uikit.jpclass\no = CREATEOBJECT('Greeter')\ngcOut = o.Greet('Joe')\n");
        var p = new ProjectDocument { Name = "app", Registry = "../registry" };
        p.Add("main.prg");
        return (p, dir);
    }

    [Fact]
    public void Semantic_version_ranges()
    {
        Assert.True(SemVer.Satisfies("1.4.2", "^1.2"));
        Assert.False(SemVer.Satisfies("2.0.0", "^1.2"));
        Assert.True(SemVer.Satisfies("0.3.9", "^0.3.1"));
        Assert.False(SemVer.Satisfies("0.4.0", "^0.3.1"));
        Assert.True(SemVer.Satisfies("1.2.9", "~1.2.3"));
        Assert.False(SemVer.Satisfies("1.3.0", "~1.2.3"));
        Assert.True(SemVer.Satisfies("3.0.0", ">=1.0"));
        Assert.True(SemVer.Satisfies("1.2.0", "1.2"));
        Assert.False(SemVer.Satisfies("1.2.1", "1.2.0"));
        Assert.True(SemVer.Satisfies("9.9.9", "*"));
        Assert.True(SemVer.Compare("1.10.0", "1.9.0") > 0);
    }

    [Fact]
    public void A_published_class_library_is_consumed_with_version_pinning()
    {
        var lib = Library("1.0.0", "Hello");
        PackageManager.Publish(ProjectDocument.Load(Path.Combine(lib, "uikit.jpproj")), lib, Registry);
        Assert.Throws<VfpException>(() => PackageManager.Publish(ProjectDocument.Load(Path.Combine(lib, "uikit.jpproj")), lib, Registry));   // versions are immutable
        var (app, dir) = App();
        Assert.Equal("1.0.0", PackageManager.Add(app, dir, "uikit"));
        Assert.Equal("^1.0.0", app.Dependencies.Single().Version);
        app.Save(Path.Combine(dir, "app.jpproj"));
        Assert.True(File.Exists(Path.Combine(dir, "packages", "uikit", "uikit.jpclass")));
        Assert.Equal("1.0.0", PackageManager.ReadLock(dir).Single().Version);

        // Loading the project puts its packages on the path.
        Run($"BUILD PROJECT \"{Path.Combine(dir, "app.jpproj")}\"\nSET DEFAULT TO \"{dir}\"\nDO main");
        Assert.Equal("Hello Joe", Rt.Evaluate("gcOut").AsString);

        // A compatible 1.1.0 is published: restore keeps the pinned 1.0.0 until asked to update.
        Library("1.1.0", "Howdy");
        PackageManager.Publish(ProjectDocument.Load(Path.Combine(lib, "uikit.jpproj")), lib, Registry);
        var restored = PackageManager.Restore(app, dir);
        Assert.Equal("1.0.0", restored.Packages.Single().Version);
        Assert.Empty(restored.Messages);   // already installed
        Assert.Equal("1.1.0", PackageManager.Restore(app, dir, update: true).Packages.Single().Version);
        Assert.Contains("Howdy", File.ReadAllText(Path.Combine(dir, "packages", "uikit", "uikit.jpclass")));
        // Pin an exact version.
        Assert.Equal("1.0.0", PackageManager.Add(app, dir, "uikit", "1.0.0"));
        Assert.Contains("Hello", File.ReadAllText(Path.Combine(dir, "packages", "uikit", "uikit.jpclass")));
        Assert.Contains("No version of uikit matches ^2.0", Assert.Throws<VfpException>(() => PackageManager.Add(app, dir, "uikit", "^2.0")).Message);

        // The application carries its packages.
        app.Dependencies[0] = new PackageReference("uikit", "1.0.0");
        PackageManager.Restore(app, dir);
        var build = ProjectBuilder.BuildApp(app, dir, Path.Combine(Dir, "dist", "app.jpapp"));
        Assert.True(build.Succeeded, string.Join("; ", build.Errors));
        Run("RELEASE ALL\nCLEAR CLASSLIB uikit.jpclass");
        Rt.Options.Path.Clear();
        Run($"SET DEFAULT TO \"{Dir}\"\nDO dist/app.jpapp");
        Assert.Equal("Hello Joe", Rt.Evaluate("gcOut").AsString);

        PackageManager.Remove(app, dir, "uikit");
        Assert.False(Directory.Exists(Path.Combine(dir, "packages", "uikit")));
        Assert.Empty(PackageManager.ReadLock(dir));
    }

    [Fact]
    public void Folder_sources_and_project_files_keep_dependencies()
    {
        Library("2.3.0", "Hi");
        var (app, dir) = App();
        PackageManager.Add(app, dir, "uikit", "^2.0", source: "../uikit");
        app.Save(Path.Combine(dir, "app.jpproj"));
        var back = ProjectDocument.Load(Path.Combine(dir, "app.jpproj"));
        Assert.Equal(new PackageReference("uikit", "^2.0", "../uikit"), back.Dependencies.Single());
        Assert.Equal("../registry", back.Registry);
        Assert.Equal("folder:../uikit", PackageManager.ReadLock(dir).Single().Source);
        Assert.Throws<VfpException>(() => PackageManager.Add(app, dir, "uikit", "^3.0", source: "../uikit"));
    }
}
