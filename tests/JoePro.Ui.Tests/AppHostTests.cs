using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.AppHost;
using JoePro.Documents.Menus;
using JoePro.Documents.Projects;
using JoePro.Runtime;

namespace JoePro.Ui.Tests;

/// <summary>joepro-app: a built application runs with its menu bar, forms and READ EVENTS, and ends when its program does.</summary>
public class AppHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-apphost-tests", Guid.NewGuid().ToString("N"));

    public AppHostTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { }

    private string BuildSampleApp()
    {
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "main.prg"), """
            _SCREEN.Caption = "Sales App"
            PUBLIC gcLog
            gcLog = "start;"
            DO main.mpr
            DO FORM about
            READ EVENTS
            gcLog = gcLog + "end;"
            RELEASE ALL EXTENDED
            QUIT
            """);
        File.WriteAllText(Path.Combine(src, "about.jpform"), "*-- Joe Pro form v1\n\nDEFINE CLASS about AS Form\n    Caption = \"About\"\n\n    PROCEDURE Init\n        gcLog = gcLog + \"form;\"\n    ENDPROC\nENDDEFINE\n");
        var menu = new MenuDocument();
        var file = new MenuNode { Prompt = "\\<File" };
        file.Items.Add(new MenuNode { Prompt = "E\\<xit", Command = "CLEAR EVENTS" });
        menu.Items.Add(file);
        menu.Save(Path.Combine(src, "main.jpmenu"));
        var project = new ProjectDocument { Name = "sales" };
        foreach (var f in new[] { "main.prg", "about.jpform", "main.jpmenu" }) project.Add(f);
        var result = ProjectBuilder.BuildApp(project, src, Path.Combine(_dir, "sales.jpapp"));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return result.Output!;
    }

    [AvaloniaFact]
    public void A_built_application_runs_its_menu_and_forms_until_clear_events()
    {
        var runner = new AppRunner(BuildSampleApp());
        runner.ErrorHandler = _ => { };
        runner.Window.Show();
        var exited = false;
        runner.Exited += () => exited = true;
        // While READ EVENTS runs: the form is open and the menu is showing; choosing File → Exit clears events.
        Dispatcher.UIThread.Post(() =>
        {
            Assert.Single(runner.Host.OpenForms);
            Assert.True(runner.MenuBar.IsVisible);
            var fileMenu = runner.MenuBar.Items.OfType<MenuItem>().Single();
            var exit = fileMenu.Items.OfType<MenuItem>().Single();
            exit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        });
        runner.Run();
        Assert.Empty(runner.Errors);
        Assert.True(exited);
        Assert.True(runner.Finished);
        Assert.Equal("Sales App", runner.Window.Title);
        Assert.Equal("start;form;end;", runner.Runtime.Evaluate("gcLog").AsString);
        runner.Window.Close();
        runner.Dispose();
    }

    [AvaloniaFact]
    public void Errors_are_reported_and_a_finished_program_ends_the_application()
    {
        var prg = Path.Combine(_dir, "bad.prg");
        File.WriteAllText(prg, "x = 1 + \"a\"\n");
        var errors = new List<string>();
        var runner = new AppRunner(prg) { ErrorHandler = ex => errors.Add(ex.Message) };
        runner.Window.Show();
        runner.Run();
        Assert.Single(errors);
        Assert.True(runner.Finished);
        runner.Window.Close();
        runner.Dispose();
    }
}
