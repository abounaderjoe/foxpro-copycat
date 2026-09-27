using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace JoePro.AppHost;

public sealed class App : Application
{
    public static string? Target { get; set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Default;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://joepro-app/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Target != null)
        {
            var runner = new AppRunner(Target);
            runner.ErrorHandler = ex => ShowError(runner.Window, ex);
            desktop.MainWindow = runner.Window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            runner.Exited += () => Dispatcher.UIThread.Post(() => desktop.Shutdown());
            desktop.Exit += (_, _) => runner.Dispose();
            runner.Window.Opened += (_, _) => Dispatcher.UIThread.Post(runner.Run);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowError(Window owner, JoePro.Core.VfpException ex)
    {
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var dialog = new Window
        {
            Title = "Program Error", Width = 460, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { new TextBlock { Text = $"Error {ex.Number}: {ex.Message}", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok } },
        };
        ok.Click += (_, _) => dialog.Close();
        _ = dialog.ShowDialog(owner);
    }
}
