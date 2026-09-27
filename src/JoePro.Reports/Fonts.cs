using System.Collections.Concurrent;
using SkiaSharp;

namespace JoePro.Reports;

/// <summary>
/// Font resolution and text measurement (SkiaSharp). Layout and every renderer resolve fonts here, so measured
/// line breaks match what is drawn. Common Windows fonts fall back to metric-compatible fonts where installed.
/// </summary>
public static class Fonts
{
    private static readonly ConcurrentDictionary<(string, bool, bool), (SKTypeface Face, string Used)> Cache = new();

    private static readonly Dictionary<string, string[]> Substitutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arial"] = ["Liberation Sans", "Arimo", "Helvetica", "FreeSans", "DejaVu Sans"],
        ["Helvetica"] = ["Arial", "Liberation Sans", "FreeSans", "DejaVu Sans"],
        ["Tahoma"] = ["Verdana", "DejaVu Sans", "Liberation Sans"],
        ["Verdana"] = ["DejaVu Sans", "Liberation Sans"],
        ["Segoe UI"] = ["Noto Sans", "DejaVu Sans", "Liberation Sans"],
        ["MS Sans Serif"] = ["Arial", "Liberation Sans", "DejaVu Sans"],
        ["Times New Roman"] = ["Liberation Serif", "Tinos", "Times", "FreeSerif", "DejaVu Serif"],
        ["Times"] = ["Times New Roman", "Liberation Serif", "FreeSerif", "DejaVu Serif"],
        ["Courier New"] = ["Liberation Mono", "Cousine", "Courier", "FreeMono", "DejaVu Sans Mono"],
        ["Courier"] = ["Courier New", "Liberation Mono", "FreeMono", "DejaVu Sans Mono"],
        ["FoxFont"] = ["Courier New", "Liberation Mono", "DejaVu Sans Mono"],
        ["Foxprint"] = ["Courier New", "Liberation Mono", "DejaVu Sans Mono"],
    };

    private static bool Installed(string family, out SKTypeface face, SKFontStyle style)
    {
        face = SKTypeface.FromFamilyName(family, style);
        return face != null && face.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The typeface for a font, and the family actually used.</summary>
    public static (SKTypeface Face, string Used) Resolve(FontSpec f) => Cache.GetOrAdd((f.Family, f.Bold, f.Italic), key =>
    {
        var (family, bold, italic) = key;
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        if (Installed(family, out var face, style)) return (face, family);
        foreach (var alt in Substitutes.GetValueOrDefault(family) ?? [])
            if (Installed(alt, out var altFace, style)) return (altFace, alt);
        var fallback = SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
        return (fallback, fallback.FamilyName);
    });

    public static SKFont Font(FontSpec f) => new(Resolve(f).Face, (float)f.Size) { Subpixel = true, LinearMetrics = true };

    public static double Measure(FontSpec f, string text)
    {
        using var font = Font(f);
        return font.MeasureText(text);
    }

    /// <summary>Line height and ascent in points.</summary>
    public static (double LineHeight, double Ascent) Metrics(FontSpec f)
    {
        using var font = Font(f);
        var m = font.Metrics;
        return (font.Spacing, -m.Ascent);
    }

    /// <summary>Breaks text into lines that fit a width: at line breaks, then between words, then inside long words.</summary>
    public static List<string> Wrap(FontSpec f, string text, double width)
    {
        using var font = Font(f);
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (font.MeasureText(paragraph) <= width + 0.01 || width <= 0) { lines.Add(paragraph); continue; }
            var current = "";
            foreach (var word in SplitKeepingSpaces(paragraph))
            {
                var candidate = current + word;
                if (font.MeasureText(candidate.TrimEnd()) <= width + 0.01) { current = candidate; continue; }
                if (current.Trim().Length > 0) { lines.Add(current.TrimEnd()); current = ""; }
                var w = word.TrimStart();
                // A word wider than the line is broken between characters.
                while (w.Length > 0 && font.MeasureText(w.TrimEnd()) > width + 0.01)
                {
                    int n = 1;
                    while (n < w.Length && font.MeasureText(w[..(n + 1)]) <= width + 0.01) n++;
                    lines.Add(w[..n]);
                    w = w[n..];
                }
                current = w;
            }
            lines.Add(current.TrimEnd());
        }
        return lines;
    }

    private static IEnumerable<string> SplitKeepingSpaces(string s)
    {
        int start = 0;
        for (int i = 1; i < s.Length; i++)
            if (s[i] == ' ' && s[i - 1] != ' ') { yield return s[start..i]; start = i; }
        yield return s[start..];
    }
}
