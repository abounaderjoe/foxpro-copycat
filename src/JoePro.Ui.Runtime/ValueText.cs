using System.Globalization;
using Avalonia.Media;
using JoePro.Core;

namespace JoePro.Ui.Runtime;

/// <summary>Converts between FoxPro values and the text shown in controls.</summary>
public static class ValueText
{
    public static string ToText(Value v, SetOptions o) => v.Kind switch
    {
        ValueKind.Character => v.AsString.TrimEnd(' '),
        ValueKind.Number => Formatter.FormatNumber(v.AsNumber, v.Decimals, o),
        ValueKind.Currency => Formatter.FormatNumber((double)v.AsCurrency, 4, o, v.AsCurrency),
        ValueKind.Null => "",
        ValueKind.Logical => v.AsBool ? "T" : "F",
        ValueKind.Date when v.IsEmptyDate => "",
        ValueKind.DateTime when v.IsEmptyDate => "",
        _ => Formatter.ToDisplay(v, o),
    };

    /// <summary>Parses text typed by the user into a value of the same type as <paramref name="like"/>.</summary>
    public static Value Parse(string text, Value like, SetOptions o, char fieldType = '\0')
    {
        var kind = fieldType switch
        {
            'C' or 'V' or 'M' => ValueKind.Character,
            'N' or 'F' or 'B' or 'I' => ValueKind.Number,
            'Y' => ValueKind.Currency,
            'D' => ValueKind.Date,
            'T' => ValueKind.DateTime,
            'L' => ValueKind.Logical,
            _ => like.Kind == ValueKind.Null ? ValueKind.Character : like.Kind,
        };
        text = text ?? "";
        switch (kind)
        {
            case ValueKind.Number:
            {
                var t = text.Trim().Replace(o.Separator.ToString(), "").Replace(o.Point, '.');
                var d = t.Length == 0 ? 0 : double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
                return Value.Number(d, like.Kind == ValueKind.Number ? like.Decimals : Math.Max(0, t.Length - t.IndexOf('.') - 1) * (t.Contains('.') ? 1 : 0));
            }
            case ValueKind.Currency:
            {
                var t = text.Trim().TrimStart('$').Replace(o.Separator.ToString(), "").Replace(o.Point, '.');
                return Value.Currency(t.Length == 0 ? 0 : decimal.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            case ValueKind.Date:
                return Value.FromJulian(Formatter.ParseDate(text, o));
            case ValueKind.DateTime:
            {
                var t = text.Trim();
                if (t.Length == 0) return Value.EmptyDateTime;
                var parts = t.Split(' ', 2, StringSplitOptions.TrimEntries);
                var day = Formatter.ParseDate(parts[0], o);
                if (day == 0) return Value.EmptyDateTime;
                var time = parts.Length > 1 ? JoePro.Language.Parser.ParseDateLiteral("^2000-01-01 " + parts[1]).JulianMs % Julian.MsPerDay : 0;
                return Value.DateTimeFromJulianMs(day * Julian.MsPerDay + time);
            }
            case ValueKind.Logical:
                return Value.Logical(text.Trim().Length > 0 && "TtYy".Contains(text.Trim()[0]));
            default:
                return Value.String(text);
        }
    }

    public static bool TryParse(string text, Value like, SetOptions o, out Value result, char fieldType = '\0')
    {
        try
        {
            result = Parse(text, like, o, fieldType);
            return true;
        }
        catch (FormatException) { result = like; return false; }
        catch (OverflowException) { result = like; return false; }
        catch (VfpException) { result = like; return false; }
    }

    /// <summary>FoxPro colors are RGB integers: red + green × 256 + blue × 65536.</summary>
    public static Color ToColor(Value v)
    {
        var n = v.Kind is ValueKind.Number or ValueKind.Currency ? (long)v.AsNumber : 0;
        return Color.FromRgb((byte)(n & 0xFF), (byte)((n >> 8) & 0xFF), (byte)((n >> 16) & 0xFF));
    }

    /// <summary>Converts a FoxPro caption ("\&lt;Save") to an Avalonia access-key caption ("_Save").</summary>
    public static string Caption(Value v)
    {
        var s = v.Kind == ValueKind.Character ? v.AsString : "";
        return s.Replace("_", "__").Replace("\\<", "_");
    }

    public static bool IsTrue(Value v) => v.Kind switch
    {
        ValueKind.Logical => v.AsBool,
        ValueKind.Number or ValueKind.Currency => v.AsNumber != 0,
        _ => false,
    };
}
