using System.Runtime.InteropServices;
using System.Text;

namespace JoePro.Ide;

/// <summary>
/// Writes a report when the IDE fails with an unhandled exception, and tells the user about it on the next start.
/// Reports stay on the computer (nothing is sent anywhere); the folder is shown so they can be attached to a bug report.
/// </summary>
public static class CrashReports
{
    /// <summary>Where reports go; tests point it at a scratch folder.</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Joe Pro", "CrashReports");

    /// <summary>Recent activity included in a report (for example the last Command Window lines).</summary>
    public static Func<IEnumerable<string>>? RecentActivity { get; set; }

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Write(ex, "unhandled exception"); };
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception, "unobserved task exception");
    }

    /// <summary>Writes a report; returns its path (null if even that failed).</summary>
    public static string? Write(Exception ex, string kind)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"Joe Pro {JoePro.Runtime.Interpreter.ProductVersion} {kind}");
            sb.AppendLine($"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"OS:      {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($".NET:    {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Culture: {Strings.Culture}");
            sb.AppendLine();
            sb.AppendLine(ex.ToString());
            if (RecentActivity?.Invoke() is { } recent && recent.Any())
            {
                sb.AppendLine();
                sb.AppendLine("Recent commands:");
                foreach (var line in recent) sb.AppendLine("  " + line);
            }
            File.WriteAllText(path, sb.ToString());
            return path;
        }
        catch (Exception) { return null; } // never fail while reporting a failure
    }

    /// <summary>Reports written since the last time the user was told about them (then marks them as seen).</summary>
    public static List<string> TakeNew()
    {
        try
        {
            if (!Directory.Exists(Folder)) return [];
            var marker = Path.Combine(Folder, ".seen");
            var seen = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : DateTime.MinValue;
            var fresh = Directory.GetFiles(Folder, "crash-*.txt").Where(f => File.GetLastWriteTimeUtc(f) > seen).Order().ToList();
            File.WriteAllText(marker, "");
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            return fresh;
        }
        catch (IOException) { return []; }
    }
}
