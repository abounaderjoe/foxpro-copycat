using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using JoePro.Core;
using JoePro.Reports;
using JoePro.Runtime;
using JoePro.Ui.Runtime;

namespace JoePro.AppHost;

/// <summary>
/// Runs a Joe Pro application with a user interface — the runtime a built application ships with. The main
/// window is the application's _SCREEN: it shows the program's menu bar and the ? output; forms open in their own
/// windows, reports in preview windows. The application ends when its main program finishes (after READ EVENTS)
/// with no form left open, or on QUIT.
/// </summary>
public sealed class AppRunner : IDisposable
{
    private readonly string _target;
    private readonly Menu _menu = new() { IsVisible = false };
    private readonly TextBox _screenBox = new()
    {
        IsReadOnly = true, AcceptsReturn = true, BorderThickness = new Thickness(0),
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13,
    };

    public AppRunner(string target)
    {
        _target = Path.GetFullPath(target);
        var dir = Path.GetDirectoryName(_target)!;
        Screen = new ScreenOutput();
        Runtime = new Interpreter(Screen, dir);
        Runtime.ExecuteCommand("SET TALK OFF");
        Host = new AvaloniaUiHost(Runtime);
        Reports = ReportEngine.Attach(Runtime);
        Reports.PreviewHandler = (report, _) =>
        {
            var preview = new ReportPreview(report, Reports);
            var w = new Window { Title = report.Title, Width = 900, Height = 1000, Content = preview };
            w.Show(Window);
            return true;
        };
        Host.Error += ex => Error(ex);
        Host.MenusUpdated += RebuildMenu;
        var pending = false;
        Screen.Changed += () =>
        {
            if (pending) return;
            pending = true;
            Dispatcher.UIThread.Post(() => { pending = false; _screenBox.Text = Screen.Text; _screenBox.CaretIndex = _screenBox.Text?.Length ?? 0; });
        };
        var root = new DockPanel();
        DockPanel.SetDock(_menu, Dock.Top);
        root.Children.Add(_menu);
        root.Children.Add(_screenBox);
        Window = new Window
        {
            Title = Path.GetFileNameWithoutExtension(_target),
            Width = 1024, Height = 700,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = root,
        };
        Host.Owner = Window;
        var titleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        titleTimer.Tick += (_, _) => { if (Finished) titleTimer.Stop(); else UpdateTitle(); };
        titleTimer.Start();
    }

    public Interpreter Runtime { get; }
    public AvaloniaUiHost Host { get; }
    public ReportEngine Reports { get; }
    public ScreenOutput Screen { get; }
    public Window Window { get; }
    public Menu MenuBar => _menu;
    public List<VfpException> Errors { get; } = new();
    /// <summary>Set when the application has ended (its program finished or QUIT ran).</summary>
    public bool Finished { get; private set; }
    public event Action? Exited;
    /// <summary>Shows errors to the user; tests replace it.</summary>
    public Action<VfpException> ErrorHandler { get; set; }

    private void Error(VfpException ex)
    {
        Errors.Add(ex);
        ErrorHandler?.Invoke(ex);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        foreach (var item in Host.ActiveMenuItems()) _menu.Items.Add(item);
        _menu.IsVisible = _menu.Items.Count > 0;
        UpdateTitle();
    }

    /// <summary>The main window shows _SCREEN.Caption.</summary>
    public void UpdateTitle()
    {
        try
        {
            if (Runtime.Evaluate("_SCREEN.Caption") is { Kind: ValueKind.Character } c && c.AsString.Length > 0 && c.AsString != "Joe Pro") Window.Title = c.AsString;
        }
        catch (VfpException) { }
    }

    /// <summary>Runs the application's main program (call on the UI thread once the window is shown).</summary>
    public void Run()
    {
        try
        {
            if (Path.GetExtension(_target).Equals(".jpapp", StringComparison.OrdinalIgnoreCase)) Runtime.RunApp(_target);
            else Runtime.RunProgram(_target, []);
        }
        catch (VfpException ex) { Error(ex); }
        catch (QuitException) { End(); return; }
        UpdateTitle();
        // The program returned: the application ends unless it left forms open (modeless forms without READ EVENTS).
        if (Host.OpenForms.Count == 0) End();
    }

    public void End()
    {
        if (Finished) return;
        Finished = true;
        foreach (var f in Host.OpenForms.ToList()) Runtime.Release(f);
        Exited?.Invoke();
    }

    public void Dispose() => Runtime.Session.Dispose();
}
