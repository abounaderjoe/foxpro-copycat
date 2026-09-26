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
        RequestedThemeVariant = ThemeVariant.Default; // follow the OS light/dark setting
        Styles.Add(new FluentTheme());
        var baseUri = new Uri("avares://JoePro.Ide/");
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var session = new IdeSession(StartDirectory);
            var window = new MainWindow(session);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => session.Dispose();
            if (StartFile != null) window.Opened += (_, _) => window.OpenAny(StartFile);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
