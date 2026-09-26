namespace JoePro.Tests;

internal static class TestPaths
{
    public static string Corpus(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "corpus", .. parts]);

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "joepro-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
