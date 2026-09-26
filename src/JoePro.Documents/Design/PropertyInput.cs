using System.Globalization;
using JoePro.Core;

namespace JoePro.Documents.Design;

/// <summary>
/// The property sheet's rules, as in VFP: what you type is a value of the property's current type
/// (text needs no quotes), and "=expression" enters an expression that is evaluated when the object is created.
/// </summary>
public static class PropertyInput
{
    /// <summary>Converts typed text to the expression text stored in the document.</summary>
    public static string ToExpression(string input, Value current, string? property = null)
    {
        var text = input.Trim();
        if (text.StartsWith('=')) return text[1..].Trim();
        if (property != null && IsColor(property) && ParseRgb(text) is { } rgb) return $"RGB({rgb.R},{rgb.G},{rgb.B})";
        switch (current.Kind)
        {
            case ValueKind.Number or ValueKind.Currency:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return Literal.FormatNumber(d);
                break;
            case ValueKind.Logical:
                switch (text.ToUpperInvariant())
                {
                    case ".T." or "T" or "TRUE" or "Y" or ".Y." or "YES": return ".T.";
                    case ".F." or "F" or "FALSE" or "N" or ".N." or "NO": return ".F.";
                }
                break;
            case ValueKind.Date or ValueKind.DateTime:
                if (text.Length == 0) return current.Kind == ValueKind.Date ? "{}" : "{/:}";
                if (Literal.IsDateLiteral(text)) return text;
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return current.Kind == ValueKind.Date ? $"{{^{dt:yyyy-MM-dd}}}" : $"{{^{dt:yyyy-MM-dd HH:mm:ss}}}";
                break;
            case ValueKind.Null:
                if (text.Equals(".NULL.", StringComparison.OrdinalIgnoreCase)) return ".NULL.";
                break;
            case ValueKind.Character:
                return Literal.Quote(input);
        }
        // Unknown or mismatched type: keep literals, otherwise treat the text as a string.
        return Literal.TryParse(text) != null || Literal.IsDateLiteral(text) ? text : Literal.Quote(input);
    }

    /// <summary>How a stored expression is shown in the property sheet: literals as values, expressions with "=".</summary>
    public static string ToDisplay(string expression, string? property = null)
    {
        if (property != null && IsColor(property))
        {
            var m = System.Text.RegularExpressions.Regex.Match(expression, @"^RGB\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return $"{m.Groups[1].Value},{m.Groups[2].Value},{m.Groups[3].Value}";
            if (int.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return ColorText(n);
        }
        if (Literal.TryParseString(expression, out var s)) return s;
        if (Literal.TryParse(expression) != null || Literal.IsDateLiteral(expression)) return expression;
        return "=" + expression;
    }

    /// <summary>Color properties (BackColor, ForeColor, BorderColor…) are shown and typed as "r,g,b", as in VFP.</summary>
    public static bool IsColor(string property) => property.EndsWith("Color", StringComparison.OrdinalIgnoreCase);

    /// <summary>A VFP color number (red + green*256 + blue*65536) as "r,g,b".</summary>
    public static string ColorText(int color) => $"{color & 0xFF},{(color >> 8) & 0xFF},{(color >> 16) & 0xFF}";

    private static (int R, int G, int B)? ParseRgb(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return null;
        var values = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]) || values[i] is < 0 or > 255) return null;
        return (values[0], values[1], values[2]);
    }
}
