using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using JoePro.Core;
using JoePro.Runtime.Sync;

namespace JoePro.Ide;

/// <summary>The sync dashboard in a document tab.</summary>
public sealed class SyncDashboardTab : DocumentTab
{
    public SyncDashboardTab(SyncDashboard dashboard)
    {
        Dashboard = dashboard;
        Content = dashboard;
        Title = Strings.T("Sync · ") + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dashboard.ConfigPath));
    }

    public SyncDashboard Dashboard { get; }
}

/// <summary>
/// Two-way sync during a transition: lag, the agent's pending batches, blocked rows and recent cycles, and the
/// conflict review queue, where the losing value of a conflict is re-applied to both sides with one click.
/// </summary>
public sealed class SyncDashboard : UserControl, IDisposable
{
    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace");
    private readonly SyncEngine _engine;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly ListBox _cycles = new() { FontFamily = Mono, FontSize = 12, MaxHeight = 160 };
    private readonly StackPanel _conflicts = new() { Spacing = 4 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };

    public SyncDashboard(string configPath)
    {
        ConfigPath = System.IO.Path.GetFullPath(configPath);
        _engine = new SyncEngine(ConfigPath);
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = Strings.T(text), Padding = new Thickness(8, 2), FontSize = 12 };
            ToolTip.SetTip(b, Strings.T(tip));
            b.Click += (_, _) =>
            {
                try { act(); }
                catch (Exception ex) when (ex is VfpException or IOException or InvalidOperationException) { _message.Text = ex.Message; }
            };
            return b;
        }
        var toolbar = new WrapPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                B("Run cycle", "Exchange the changes made since the last cycle", () => RunCycle()),
                B("Refresh", "Reload the status", Refresh),
                B("Write VFP programs", "Write joesync_agent.prg (and the trigger installer) into the legacy folder", () => { _engine.WriteLegacyPrograms(); _message.Text = Strings.T("Programs written to ") + _engine.Config.Legacy; }),
                B("Cut over…", "Make Joe Pro the system of record", () => _message.Text = string.Join(" ", Cutover())),
            },
        };
        var body = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 8,
            Children =
            {
                _summary,
                new TextBlock { Text = Strings.T("Recent cycles"), FontWeight = FontWeight.SemiBold },
                _cycles,
                new TextBlock { Text = Strings.T("Conflicts to review"), FontWeight = FontWeight.SemiBold },
                _conflicts,
                _message,
            },
        };
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        Refresh();
    }

    public string ConfigPath { get; }
    public SyncEngine Engine => _engine;
    public string Summary => _summary.Text ?? "";
    public IReadOnlyList<SyncConflict> Conflicts => _engine.State.Conflicts();

    public SyncCycle RunCycle()
    {
        var c = _engine.RunCycle();
        Refresh();
        _message.Text = Strings.F("Cycle done: {0} change(s) applied to Joe Pro, {1} sent to the VFP agent, {2} conflict(s), {3} error(s).", c.AppliedToJoe, c.AppliedToLegacy, c.Conflicts, c.Errors);
        return c;
    }

    public void Resolve(long id, SyncSide side)
    {
        _engine.Resolve(id, side);
        Refresh();
    }

    public IReadOnlyList<string> Cutover()
    {
        var steps = _engine.Cutover();
        Refresh();
        return steps;
    }

    private static string Line(SyncCycle c) =>
        $"{c.TimeUtc.ToLocalTime():HH:mm:ss}  legacy {c.LegacyChanges,4}  Joe Pro {c.JoeChanges,4}  → Joe Pro {c.AppliedToJoe,4}  → legacy {c.AppliedToLegacy,4}  conflicts {c.Conflicts,3}  errors {c.Errors,3}  {c.Milliseconds,6} ms";

    public void Refresh()
    {
        var st = _engine.Status();
        _summary.Text = (st.LastCycleUtc is { } t ? Strings.F("Last cycle {0:g} ({1:0} s ago).", t.ToLocalTime(), st.Lag!.Value.TotalSeconds) : Strings.T("No cycle has run yet."))
            + $"  Waiting for the VFP agent: {st.PendingBatches} batch(es), {st.PendingRows} row(s).  Blocked rows: {st.Blocked}.  Open conflicts: {st.OpenConflicts}."
            + (st.CutOver ? "  Cut over: Joe Pro is the system of record." : $"  System of record: {string.Join(", ", _engine.Config.Tables.Select(x => $"{x.Name} {x.Authority.ToString().ToLowerInvariant()}"))}.");
        _cycles.ItemsSource = st.Recent.Select(Line).ToList();
        _conflicts.Children.Clear();
        var open = _engine.State.Conflicts();
        if (open.Count == 0) _conflicts.Children.Add(new TextBlock { Text = Strings.T("No conflicts."), Opacity = 0.7 });
        foreach (var c in open)
        {
            var id = c.Id;
            var row = new WrapPanel { Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = $"#{c.Id}  {c.Table} {c.Key}  {c.Field}  ({c.Kind})  legacy: {c.LegacyValue ?? "—"}   Joe Pro: {c.JoeValue ?? "—"}   {c.Winner.ToString().ToLowerInvariant()} won",
                FontFamily = Mono, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            });
            foreach (var (label, act) in new (string, Action)[]
            {
                ("Use legacy value", () => Resolve(id, SyncSide.Legacy)),
                ("Use Joe Pro value", () => Resolve(id, SyncSide.Joe)),
                ("Dismiss", () => { _engine.Dismiss(id); Refresh(); }),
            })
            {
                var b = new Button { Content = Strings.T(label), FontSize = 11, Padding = new Thickness(6, 0), Margin = new Thickness(2, 0) };
                b.Click += (_, _) =>
                {
                    try { act(); }
                    catch (Exception ex) when (ex is VfpException or IOException) { _message.Text = ex.Message; }
                };
                row.Children.Add(b);
            }
            _conflicts.Children.Add(row);
        }
        foreach (var (table, key, reason) in _engine.State.Blocked())
            _conflicts.Children.Add(new TextBlock { Text = Strings.F("Blocked: {0} {1}: {2}", table, key, reason), Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap });
    }

    public void Dispose() => _engine.Dispose();
}
