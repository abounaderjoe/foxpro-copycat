using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JoePro.Ide;

namespace JoePro.Ui.Tests;

/// <summary>
/// Designers and tool windows show their text through the string catalog: under the pseudo-locale every label, button,
/// tab, placeholder and tooltip is accented and bracketed, so English text that bypasses the catalog stands out.
/// </summary>
public class LocalizationTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "joepro-l10n", Guid.NewGuid().ToString("N"))).FullName;
    private IdeSession? _session;
    private MainWindow? _window;
    private readonly string _saved = Strings.Culture;

    public void Dispose()
    {
        _window?.Close();
        _session?.Dispose();
        Strings.Use(_saved);
    }

    /// <summary>Visible interface text that did not come through the catalog (data, names and code are not interface text).</summary>
    public static List<string> Untranslated(Visual root)
    {
        var found = new List<string>();
        void Check(string? text, Control where, string kind)
        {
            if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('[')) return;
            if (!IsProse(text)) return;
            found.Add($"{kind} \"{text.Trim()}\" in {Where(where)}");
        }
        foreach (var v in root.GetVisualDescendants().Prepend(root))
        {
            if (v is not Control c || !c.IsEffectivelyVisible || IsData(c)) continue;
            switch (c)
            {
                case TextBlock tb when tb.TemplatedParent is not (TextBox or ContentPresenter { TemplatedParent: ComboBox }):
                    Check(tb.Text, c, "text");
                    break;
                case TextBox box:
                    Check(box.Watermark, c, "placeholder");
                    break;
            }
            if (ToolTip.GetTip(c) is string tip) Check(tip, c, "tooltip");
        }
        return found.Distinct().ToList();
    }

    /// <summary>Text with at least one English word (letters, not an identifier, expression or number).</summary>
    private static bool IsProse(string text)
    {
        var t = text.Trim();
        if (t.Length < 2) return false;
        if (!t.Contains(' ') && t.Any(ch => char.IsDigit(ch) || ch == '.')) return false; // form1.jpform, Form1
        // Identifiers, file names, expressions and field lists come from the user's data, not the interface.
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\w.\\/:()\[\],=<>+\-*'""#@&%$ ]*$") && !System.Text.RegularExpressions.Regex.IsMatch(t, @"\b[A-Z][a-z]+\b.*\b[a-z]{2,}\b|^[A-Z][a-z]{2,}"))
            return false;
        return t.Any(char.IsLetter);
    }

    /// <summary>Controls that show the user's own content: lists, grids, editors, the design surfaces and running forms.</summary>
    private static bool IsData(Control c)
    {
        for (Visual? v = c; v != null; v = v.GetVisualParent())
        {
            if (v is ListBox or TreeView or DataGrid or AvaloniaEdit.TextEditor or ComboBox or Menu) return true;
            if (v is Control x && x.Classes.Contains("data")) return true;
            var ns = v.GetType().Namespace ?? "";
            if (ns.StartsWith("JoePro.Ui", StringComparison.Ordinal) && !ns.StartsWith("JoePro.Ui.Tests", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string Where(Control c)
    {
        var parts = new List<string>();
        for (Visual? v = c; v != null && parts.Count < 4; v = v.GetVisualParent())
            if (v is Control x) parts.Add(x.GetType().Name);
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private MainWindow OpenEverything()
    {
        _session = new IdeSession(_dir);
        var w = _window = new MainWindow(_session);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        _session.Execute("CREATE DATABASE shop\nCREATE TABLE items (id I, name C(20))\nCREATE CLASSLIB lib1\nCREATE CLASS cusA OF lib1 AS Custom");
        var db = Path.Combine(_dir, "shop.jpdb");
        var opened = new List<(string What, Action Open)>
        {
            ("main window", () => { }),
            ("form designer", () => w.OpenForm(null, create: true)),
            ("class designer", () => w.OpenClass(Path.Combine(_dir, "lib1.jpclass"), "cusA")),
            ("class browser", () => w.OpenClassBrowser(Path.Combine(_dir, "lib1.jpclass"))),
            ("report designer", () => w.OpenReport(null)),
            ("label designer", () => w.OpenReport(null, label: true)),
            ("menu designer", () => w.OpenMenu(null)),
            ("query designer", () => w.OpenQuery(null)),
            ("database designer", () => w.OpenDatabaseDesigner(db)),
            ("table designer", () => w.OpenTableDesigner(db, "items")),
            ("stored procedures", () => w.OpenStoredProcedures(db)),
            ("migration wizard", () => w.OpenMigrationWizard()),
            ("help", () => w.OpenDesigner(new JoePro.Runtime.DesignerRequest("HELP", "", false, null))),
        };
        foreach (var (what, open) in opened)
        {
            open();
            Dispatcher.UIThread.RunJobs();
            foreach (var m in Untranslated(w)) _missing.Add($"[{what}] {m}");
        }
        return w;
    }

    private readonly SortedSet<string> _missing = new();

    [AvaloniaFact]
    public void Designers_and_tool_windows_show_their_text_through_the_catalog()
    {
        Strings.Use(Strings.PseudoLocale);
        OpenEverything();
        Assert.True(_missing.Count == 0, $"{_missing.Count} texts outside the catalog:\n" + string.Join("\n", _missing.Take(400)));
    }
}
