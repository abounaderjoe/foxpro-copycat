using Avalonia.Headless.XUnit;
using JoePro.Ide;
using JoePro.Tooling;

namespace JoePro.Ui.Tests;

public class EditorIntelligenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "joepro-ide-intel", Guid.NewGuid().ToString("N"));
    private readonly IdeSession _session;
    private readonly MainWindow _window;

    public EditorIntelligenceTests()
    {
        Directory.CreateDirectory(_dir);
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
    }

    public void Dispose()
    {
        _window.Close();
        _session.Dispose();
    }

    [AvaloniaFact]
    public void Editor_shows_diagnostics_completions_and_navigates_to_definitions()
    {
        var path = Path.Combine(_dir, "main.prg");
        File.WriteAllText(path, "x = Helper(2)\nIF x\n   y = (1 +\nENDIF\nFUNCTION Helper(n)\n   RETURN n\nENDFUNC");
        var tab = _window.OpenFile(path);
        var intel = tab.Intelligence!;
        intel.RefreshDiagnostics();
        var error = Assert.Single(intel.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(3, error.Line);
        Assert.Contains(error.Message, intel.HoverAt(3, 8));

        tab.Editor.Text = "x = Helper(2)\n? ALLT\nFUNCTION Helper(n)\n   RETURN n\nENDFUNC";
        intel.RefreshDiagnostics();
        Assert.DoesNotContain(intel.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        tab.Editor.TextArea.Caret.Line = 2;
        tab.Editor.TextArea.Caret.Column = 7;
        Assert.Contains(intel.CompletionsAtCaret(), c => c.Label == "ALLTRIM");
        intel.ShowCompletion();

        Assert.Equal("(function) Helper(n)", intel.HoverAt(1, 6));
        tab.Editor.TextArea.Caret.Line = 1;
        tab.Editor.TextArea.Caret.Column = 6;
        intel.GoToDefinition();
        Assert.Equal(3, tab.Editor.TextArea.Caret.Line);
    }
}
