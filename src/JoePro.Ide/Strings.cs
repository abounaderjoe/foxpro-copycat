using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace JoePro.Ide;

/// <summary>
/// The IDE's user-interface text. The English text is the key (gettext style), so untranslated text still reads well.
/// Catalogs are JSON files, one per culture: Localization/&lt;culture&gt;.json embedded in the IDE, then a file of the same
/// name in the user's Joe Pro settings folder, which wins. The culture comes from JOEPRO_UI_CULTURE or the operating
/// system. The pseudo-locale "qps-ploc" accents every translated string, so text that bypasses the catalog stands out.
/// </summary>
public static class Strings
{
    public const string PseudoLocale = "qps-ploc";

    private static Dictionary<string, string> _catalog = new();

    public static string Culture { get; private set; } = "en";

    static Strings() => Use(Environment.GetEnvironmentVariable("JOEPRO_UI_CULTURE") is { Length: > 0 } c ? c : CultureInfo.CurrentUICulture.Name);

    /// <summary>The translation of <paramref name="english"/> in the current culture (the English text when there is none).</summary>
    /// <summary>Every text the IDE has asked to translate (for catalog checks).</summary>
    public static IReadOnlyCollection<string> Requested => _requested;
    private static readonly HashSet<string> _requested = new();

    public static string T(string english)
    {
        lock (_requested) _requested.Add(english);
        if (Culture == PseudoLocale) return Pseudo(english);
        return _catalog.TryGetValue(english, out var t) && t.Length > 0 ? t : english;
    }

    /// <summary>Switches the culture: "de-CH" uses de-CH.json, then de.json.</summary>
    public static void Use(string culture)
    {
        Culture = string.IsNullOrWhiteSpace(culture) ? "en" : culture;
        _catalog = new Dictionary<string, string>();
        if (Culture == PseudoLocale) return;
        var names = new List<string>();
        var dash = Culture.IndexOf('-');
        if (dash > 0) names.Add(Culture[..dash]);
        names.Add(Culture);
        foreach (var name in names)
        {
            Merge(ReadEmbedded(name));
            var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Joe Pro", "Localization", name + ".json");
            if (File.Exists(user)) Merge(File.ReadAllText(user));
        }
    }

    /// <summary>The cultures with a built-in catalog.</summary>
    public static IEnumerable<string> BuiltInCultures() =>
        typeof(Strings).Assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Localization.") && n.EndsWith(".json"))
            .Select(n => n[(n.IndexOf(".Localization.") + ".Localization.".Length)..^".json".Length]);

    /// <summary>A built-in catalog's entries (for tests and translators).</summary>
    public static Dictionary<string, string> Catalog(string culture) =>
        ReadEmbedded(culture) is { } json ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new() : new();

    private static string? ReadEmbedded(string culture)
    {
        var asm = typeof(Strings).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Localization." + culture + ".json", StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res)!;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    private static void Merge(string? json)
    {
        if (json == null) return;
        try
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new()) _catalog[k] = v;
        }
        catch (JsonException) { } // a broken user catalog leaves the text in English
    }

    private static string Pseudo(string s)
    {
        var sb = new StringBuilder("[");
        foreach (var ch in s)
            sb.Append(ch switch
            {
                'a' => 'á', 'e' => 'é', 'i' => 'í', 'o' => 'ó', 'u' => 'ú', 'A' => 'Å', 'E' => 'É', 'I' => 'Î', 'O' => 'Ö', 'U' => 'Ü',
                'c' => 'ç', 'n' => 'ñ', 's' => 'š', 'y' => 'ý', 'C' => 'Ç', 'N' => 'Ñ', 'S' => 'Š',
                _ => ch,
            });
        return sb.Append(']').ToString();
    }
}
