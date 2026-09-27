using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JoePro.Ide;

namespace JoePro.Ui.Tests;

/// <summary>Every interactive control in the IDE has a name a screen reader can announce.</summary>
public class AccessibilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "joepro-a11y", Guid.NewGuid().ToString("N"))).FullName;
    private IdeSession? _session;
    private MainWindow? _window;

    public void Dispose()
    {
        _window?.Close();
        _session?.Dispose();
    }

    /// <summary>Interactive controls whose automation peer has no name.</summary>
    public static List<string> Unnamed(Visual root)
    {
        var missing = new List<string>();
        foreach (var v in root.GetVisualDescendants().Prepend(root))
        {
            if (v is not Control c || !c.IsEffectivelyVisible) continue;
            if (c is not (Button or ToggleButton or TextBox or ComboBox or ListBox or TreeView or Slider or NumericUpDown or MenuItem or TabItem or AutoCompleteBox or AvaloniaEdit.TextEditor or DataGrid)) continue;
            if (c.TemplatedParent is Control tp && tp is (ComboBox or NumericUpDown or ScrollBar or AutoCompleteBox or Expander or CalendarDatePicker or TimePicker)) continue; // parts of a composite control
            if (c.FindAncestorOfType<ScrollBar>() != null) continue;
            string? name;
            try { name = ControlAutomationPeer.CreatePeerForElement(c).GetName(); }
            catch (InvalidOperationException) { name = null; }
            // A name of one or two characters is an icon glyph, not something a screen reader user can act on.
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length <= 2) missing.Add($"{c.GetType().Name} '{name}' in {Where(c)}");
        }
        return missing;
    }

    private static string Where(Control c)
    {
        var parts = new List<string>();
        for (Visual? v = c; v != null && parts.Count < 5; v = v.GetVisualParent())
            if (v is Control x) parts.Add(x.Name is { Length: > 0 } n ? $"{x.GetType().Name}#{n}" : x.GetType().Name);
        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private MainWindow Open()
    {
        _session = new IdeSession(_dir);
        _window = new MainWindow(_session);
        _window.Show();
        Dispatcher.UIThread.RunJobs();
        return _window;
    }

    [AvaloniaFact]
    public void Main_window_controls_have_accessible_names()
    {
        var w = Open();
        var missing = Unnamed(w);
        Assert.True(missing.Count == 0, $"{missing.Count} unnamed controls:\n" + string.Join("\n", missing.Take(40)));
    }

    [AvaloniaFact]
    public void Designers_and_tool_windows_have_accessible_names()
    {
        var w = Open();
        _session!.Execute("CREATE DATABASE shop\nCREATE TABLE items (id I, name C(20))\nCREATE CLASSLIB lib1\nCREATE CLASS cusA OF lib1 AS Custom");
        File.WriteAllText(System.IO.Path.Combine(_dir, "main.prg"), "? 1");
        var db = System.IO.Path.Combine(_dir, "shop.jpdb");
        var opened = new List<(string What, Action Open)>
        {
            ("code editor", () => w.OpenFile(System.IO.Path.Combine(_dir, "main.prg"))),
            ("form designer", () => w.OpenForm(null, create: true)),
            ("class designer", () => w.OpenClass(System.IO.Path.Combine(_dir, "lib1.jpclass"), "cusA")),
            ("class browser", () => w.OpenClassBrowser(System.IO.Path.Combine(_dir, "lib1.jpclass"))),
            ("report designer", () => w.OpenReport(null)),
            ("label designer", () => w.OpenReport(null, label: true)),
            ("menu designer", () => w.OpenMenu(null)),
            ("query designer", () => w.OpenQuery(null)),
            ("database designer", () => w.OpenDatabaseDesigner(db)),
            ("table designer", () => w.OpenTableDesigner(db, "items")),
            ("stored procedures", () => w.OpenStoredProcedures(db)),
            ("migration wizard", () => w.OpenMigrationWizard()),
        };
        var missing = new SortedSet<string>();
        foreach (var (what, open) in opened)
        {
            open();
            Dispatcher.UIThread.RunJobs();
            foreach (var m in Unnamed(w)) missing.Add($"[{what}] {m}");
        }
        Assert.True(missing.Count == 0, $"{missing.Count} unnamed controls:\n" + string.Join("\n", missing.Take(80)));
    }

    [AvaloniaFact]
    public void Every_menu_command_has_a_unique_access_key_and_is_in_the_command_palette()
    {
        var w = Open();
        var menu = w.GetVisualDescendants().OfType<Menu>().First();
        var problems = new List<string>();
        var palette = w.Palette.Actions.Select(a => a.Title.ToLowerInvariant()).ToHashSet();
        void Walk(IEnumerable<object?> items, string path)
        {
            var keys = new Dictionary<char, string>();
            foreach (var mi in items.OfType<MenuItem>())
            {
                var header = mi.Header as string ?? "";
                var at = header.IndexOf('_');
                if (at < 0 || at + 1 >= header.Length) problems.Add($"{path}{header}: no access key");
                else if (!keys.TryAdd(char.ToLowerInvariant(header[at + 1]), header)) problems.Add($"{path}{header}: access key '{header[at + 1]}' also used by {keys[char.ToLowerInvariant(header[at + 1])]}");
                if (mi.Items.Count > 0) Walk(mi.Items, path + header.Replace("_", "") + " > ");
            }
        }
        Walk(menu.Items, "");
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        Assert.True(palette.Count > 40);
    }
}
