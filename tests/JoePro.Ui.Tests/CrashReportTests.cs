using JoePro.Ide;

namespace JoePro.Ui.Tests;

public class CrashReportTests
{
    [Fact]
    public void Writes_a_report_with_context_and_announces_it_once()
    {
        var saved = CrashReports.Folder;
        CrashReports.Folder = Path.Combine(Path.GetTempPath(), "joepro-crash", Guid.NewGuid().ToString("N"));
        try
        {
            CrashReports.RecentActivity = () => ["USE customer", "BROWSE"];
            Exception boom;
            try { throw new InvalidOperationException("boom"); } catch (InvalidOperationException ex) { boom = ex; }
            var path = CrashReports.Write(boom, "unhandled exception");
            Assert.NotNull(path);
            var text = File.ReadAllText(path!);
            Assert.Contains("InvalidOperationException: boom", text);
            Assert.Contains(JoePro.Runtime.Interpreter.ProductVersion, text);
            Assert.Contains("BROWSE", text);
            Assert.Equal([path], CrashReports.TakeNew());
            Assert.Empty(CrashReports.TakeNew());
        }
        finally
        {
            CrashReports.RecentActivity = null;
            CrashReports.Folder = saved;
        }
    }
}
