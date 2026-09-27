namespace JoePro.Language;

/// <summary>
/// Built-in versions of the header files FoxPro installs in HOME() (foxpro.h, foxpro_reporting.h). #INCLUDE
/// falls back to them when no file of that name is found, so migrated programs compile without the VFP install.
/// </summary>
public static class SystemHeaders
{
    private static readonly string[] Names = ["foxpro.h", "foxpro_reporting.h"];

    public static IReadOnlyList<string> All => Names;

    /// <summary>The built-in header for an #INCLUDE name (any folder, any case), or null.</summary>
    public static string? Find(string includeName)
    {
        var file = Path.GetFileName(includeName.Trim().Replace('\\', '/'));
        var name = Names.FirstOrDefault(n => n.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var stream = typeof(SystemHeaders).Assembly.GetManifestResourceStream("JoePro.Language.Headers." + name);
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
