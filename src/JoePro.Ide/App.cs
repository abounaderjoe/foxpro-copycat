using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace JoePro.Ide;

public sealed class App : Application
{
    public static string StartDirectory { get; set; } = Directory.GetCurrentDirectory();
    public static string? StartFile { get; set; }

    public override void Initialize()
    {
        RequestedThemeVariant = IdeTheme.LoadVariant(); // the theme chosen last time (View › Light/Dark/System), else the OS setting
        Styles.Add(IdeTheme.CreateFluent());
        IdeTheme.AddResources(this);
        var baseUri = new Uri("avares://JoePro.Ide/");
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CrashReports.Install();
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) => CrashReports.Write(e.Exception, "unhandled exception on the UI thread");
            var session = new IdeSession(StartDirectory);
            CrashReports.RecentActivity = () => session.History.TakeLast(20);
            var window = new MainWindow(session);
            var crashes = CrashReports.TakeNew();
            if (crashes.Count > 0)
                window.Opened += (_, _) => window.SetStatus(Strings.F("Joe Pro closed unexpectedly last time. The report is in {0} (nothing was sent).", crashes[^1]));
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => session.Dispose();
            if (StartFile != null) window.Opened += (_, _) => window.OpenAny(StartFile);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
