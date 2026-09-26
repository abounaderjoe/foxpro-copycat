using System.Globalization;
using JoePro.Core;

namespace JoePro.Documents;

/// <summary>Formats values as FoxPro literals and recognizes literal text, for property values.</summary>
public static class Literal
{
    /// <summary>A FoxPro string literal: "…" unless the text contains a double quote, then '…', then […].</summary>
    public static string Quote(string s)
    {
        if (!s.Contains('"')) return "\"" + s + "\"";
        if (!s.Contains('\'')) return "'" + s + "'";
        if (!s.Contains(']')) return "[" + s + "]";
        // All three delimiters appear: concatenate pieces.
        return string.Join(" + [\"] + ", s.Split('"').Select(p => "\"" + p + "\""));
    }

    public static string QuoteIfNeeded(string s) =>
        s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '\\' or '/' or ':' or '-') ? s : Quote(s);

    public static string Format(Value v) => v.Kind switch
    {
        ValueKind.Character => Quote(v.AsString),
        ValueKind.Logical => v.AsBool ? ".T." : ".F.",
        ValueKind.Null => ".NULL.",
        ValueKind.Number => FormatNumber(v.AsNumber),
        ValueKind.Currency => "$" + v.AsCurrency.ToString(CultureInfo.InvariantCulture),
        ValueKind.Date => v.IsEmpty ? "{}" : "{^" + Julian.ToDate(v.JulianDay).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "}",
        ValueKind.DateTime => v.IsEmpty ? "{/:}" : "{^" + Julian.ToDateTime(v.JulianMs).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "}",
        _ => throw new ArgumentException($"A {v.Kind} value cannot be written as a property literal."),
    };

    public static string FormatNumber(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>{}, {^2024-01-31}, {/:} and similar date/datetime literals.</summary>
    public static bool IsDateLiteral(string text) => System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^\{[\^\d\s/:\-\.APMapm]*\}$");

    /// <summary>True if <paramref name="text"/> is exactly the string literal for <paramref name="expected"/> (any delimiter).</summary>
    public static bool IsString(string text, string expected) =>
        TryParseString(text, out var s) && s.Equals(expected, StringComparison.OrdinalIgnoreCase);

    public static bool TryParseString(string text, out string value)
    {
        value = "";
        text = text.Trim();
        if (text.Length < 2) return false;
        var open = text[0];
        var close = open switch { '"' => '"', '\'' => '\'', '[' => ']', _ => '\0' };
        if (close == '\0' || text[^1] != close) return false;
        var inner = text[1..^1];
        if (inner.Contains(close)) return false;
        value = inner;
        return true;
    }

    /// <summary>Parses a literal (string, number, logical, null, date, RGB()) to a value; null if the text is an expression.</summary>
    public static Value? TryParse(string text)
    {
        text = text.Trim();
        if (TryParseString(text, out var s)) return Value.String(s);
        switch (text.ToUpperInvariant())
        {
            case ".T." or ".Y.": return Value.True;
            case ".F." or ".N.": return Value.False;
            case ".NULL." or "NULL": return Value.Null;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            var dot = text.IndexOf('.');
            return Value.Number(d, dot >= 0 && !text.Contains('E', StringComparison.OrdinalIgnoreCase) ? text.Length - dot - 1 : 0);
        }
        var rgb = System.Text.RegularExpressions.Regex.Match(text, @"^RGB\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (rgb.Success)
            return Value.Number(int.Parse(rgb.Groups[1].Value) + int.Parse(rgb.Groups[2].Value) * 256 + int.Parse(rgb.Groups[3].Value) * 65536);
        return null;
    }
}
