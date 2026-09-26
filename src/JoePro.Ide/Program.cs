using Avalonia;

namespace JoePro.Ide;

public static class Program
{
    /// <summary>joepro-ide [folder | file]: opens the IDE in a folder, or opens a file (program, table or database).</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            var target = Path.GetFullPath(args[0]);
            if (Directory.Exists(target)) App.StartDirectory = target;
            else if (File.Exists(target))
            {
                App.StartDirectory = Path.GetDirectoryName(target)!;
                App.StartFile = target;
            }
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
