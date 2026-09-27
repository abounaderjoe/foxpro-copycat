using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using JoePro.Core;
using JoePro.Ide;
using JoePro.Legacy.Formats;
using JoePro.Migration;

namespace JoePro.Ui.Tests;

public class MigrationWizardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-migwiz-tests", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public MigrationWizardTests()
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

    private string WriteLegacyApp()
    {
        var legacy = Path.Combine(_dir, "sales");
        Directory.CreateDirectory(Path.Combine(legacy, "Data"));
        File.WriteAllText(Path.Combine(legacy, "main.prg"), "* Sales\nDECLARE INTEGER GetTickCount IN kernel32\nPUBLIC gnTicks\ngnTicks = 1\nDO FORM orders\n");
        DbfWriter.Write(Path.Combine(legacy, "Data", "orders.dbf"), [new FieldDef("ID", 'N', 5), new FieldDef("CUSTOMER", 'C', 20)],
            [(false, [Value.Number(1), Value.String("Acme")]), (false, [Value.Number(2), Value.String("Globex")])]);
        new JoePro.Tests.Documents.Frx().Band(1, 0.3).Band(4, 0.2).Field(4, "orders.customer", 0, 0, 2).Save(Path.Combine(legacy, "orders.frx"));
        FieldDef[] fields = [new("NAME", 'M'), new("TYPE", 'C', 1), new("EXCLUDE", 'L'), new("MAINPROG", 'L'), new("OUTFILE", 'M'), new("COMMENTS", 'M')];
        (bool, Value[]) Row(string name, string type, bool main = false, bool exclude = false) =>
            (false, [Value.String(name + "\0"), Value.String(type), Value.Logical(exclude), Value.Logical(main), Value.String(""), Value.String("")]);
        DbfWriter.Write(Path.Combine(legacy, "sales.pjx"), fields,
            [Row("sales.pjx", "H"), Row("main.prg", "P", main: true), Row("orders.frx", "R"), Row("data\\orders.dbf", "D", exclude: true)]);
        return legacy;
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (!task.IsCompleted && DateTime.UtcNow < until) Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted, "The import did not finish.");
        task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void Wizard_scans_imports_and_opens_the_project_and_report()
    {
        var legacy = WriteLegacyApp();
        var wizard = _window.OpenMigrationWizard(legacy);
        Assert.IsType<MigrationWizardTab>(_window.Documents.SelectedItem);
        Assert.Equal(0, wizard.Page);
        var scan = wizard.ScanResult!;
        Assert.Equal((1, 1, 1, 1), (scan.Count("Projects"), scan.Count("Tables"), scan.Count("Reports"), scan.Count("Programs")));
        Assert.Equal(["sales.pjx"], scan.Projects);
        Assert.Equal(legacy + "-joepro", wizard.TargetFolder);

        wizard.Next();
        Assert.Equal(1, wizard.Page);
        wizard.TargetFolder = legacy;   // the application's own folder is refused
        Wait(wizard.ImportAsync());
        Assert.Equal(1, wizard.Page);
        Assert.Contains("different folder", wizard.Message);

        var target = Path.Combine(_dir, "sales-jp");
        wizard.TargetFolder = target;
        Wait(wizard.ImportAsync());
        Assert.Equal(3, wizard.Page);
        Assert.Equal([Path.Combine(target, "sales.jpproj")], wizard.ConvertedProjects);
        Assert.True(File.Exists(Path.Combine(target, "Data", "orders.jpt")));
        Assert.True(File.Exists(Path.Combine(target, "orders.jpreport")));
        Assert.True(File.Exists(Path.Combine(target, "migration-report.json")));

        // Finished: SET DEFAULT to the new folder, the project and the report open (the report in front).
        Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(_session.Runtime.Options.Default_));
        Assert.Contains(_window.Documents.Items, i => i is ProjectManagerTab);
        var view = Assert.IsType<MigrationReportTab>(_window.Documents.SelectedItem).View;

        // The report opens on what needs attention: the DLL declaration in main.prg.
        Assert.Equal(MigrationReportView.NeedsAttention, view.StatusFilter);
        Assert.All(view.Visible, f => Assert.True(MigrationReportView.IsAttention(f)));
        var declare = Assert.Single(view.Visible, f => f.Rule == "CODE.DLL.DECLARE");
        view.Selected = declare;
        Assert.Contains("GetTickCount", view.Details);
        Assert.Equal((Path.Combine(target, "main.prg"), 2), view.ConvertedFile(declare));
        view.OpenSelected(converted: true);
        var editor = Assert.IsType<CodeEditorTab>(_window.Documents.SelectedItem);
        Assert.Equal(Path.Combine(target, "main.prg"), editor.FilePath);
        Assert.Equal(2, editor.Editor.TextArea.Caret.Line);

        // Filters: status, category, text.
        view.StatusFilter = MigrationReportView.AllStatuses;
        Assert.Equal(view.Report.Findings.Count, view.Visible.Count);
        view.CategoryFilter = "project";
        Assert.All(view.Visible, f => Assert.Equal("project", f.Category));
        Assert.Contains(view.Visible, f => f.Rule == "PROJECT.CONVERTED");
        view.CategoryFilter = MigrationReportView.AllCategories;
        view.Search = "kernel32";
        Assert.Equal("CODE.DLL.DECLARE", Assert.Single(view.Visible).Rule);   // found by its code snippet

        // migration-report.json opens the same view from disk (replacing the tab for that folder).
        _window.OpenAny(Path.Combine(target, "migration-report.json"));
        var reopened = Assert.IsType<MigrationReportTab>(_window.Documents.SelectedItem).View;
        Assert.Equal(view.Report.Findings.Count, reopened.Report.Findings.Count);
        Assert.Equal(target, reopened.Report.Target);
        Assert.Single(_window.Documents.Items.OfType<MigrationReportTab>());
    }

    [AvaloniaFact]
    public void Wizard_needs_an_existing_folder_with_files()
    {
        var wizard = _window.OpenMigrationWizard(Path.Combine(_dir, "nope"));
        Assert.Null(wizard.ScanResult);
        Assert.Contains("does not exist", wizard.Message);
        wizard.Next();
        Assert.Equal(0, wizard.Page);
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        wizard.SourceFolder = empty;
        Assert.Contains("empty", wizard.Message);
        wizard.Next();
        Assert.Equal(0, wizard.Page);
        Assert.Same(wizard, _window.OpenMigrationWizard());   // one wizard at a time
    }
}
