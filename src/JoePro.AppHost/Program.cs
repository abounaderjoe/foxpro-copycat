using Avalonia;

namespace JoePro.AppHost;

public static class Program
{
    /// <summary>joepro-app app.jpapp | main.prg: runs a Joe Pro application with its forms, menus and reports.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: joepro-app <application.jpapp | program.prg>");
            return 2;
        }
        App.Target = args[0];
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
