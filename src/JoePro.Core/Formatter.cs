using System.Globalization;
using System.Text;

namespace JoePro.Core;

/// <summary>Text formatting of values following the current SET options (display, DTOC, TTOC).</summary>
public static class Formatter
{
    /// <summary>Formats a value the way the ? command displays it.</summary>
    public static string ToDisplay(Value v, SetOptions o) => v.Kind switch
    {
        ValueKind.Null => ".NULL.",
        ValueKind.Logical => v.AsBool ? ".T." : ".F.",
        // TODO(oracle): confirm VFP's display width rules for numbers; integers pad to 10 columns.
        ValueKind.Number => FormatNumber(v.AsNumber, v.Decimals, o).PadLeft(10),
        ValueKind.Currency => FormatNumber((double)v.AsCurrency, 4, o, v.AsCurrency),
        ValueKind.Character => v.AsString,
        ValueKind.Date => DateToString(v.JulianDay, o),
        ValueKind.DateTime => DateTimeToString(v.JulianMs, o),
        ValueKind.Binary => "0h" + Convert.ToHexString(v.AsBinary),
        ValueKind.Object => "(Object)",
        _ => "",
    };

    public static string FormatNumber(double d, int decimals, SetOptions o, decimal? exact = null)
    {
        decimals = Math.Clamp(decimals, 0, 18);
        var s = exact is { } m
            ? m.ToString("F" + decimals, CultureInfo.InvariantCulture)
            : RoundHalfAwayFromZero(d, decimals).ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (s.StartsWith("-") && s.Trim('-', '0', '.').Length == 0) s = s[1..];
        return o.Point == '.' ? s : s.Replace('.', o.Point);
    }

    /// <summary>VFP rounds half away from zero.</summary>
    public static double RoundHalfAwayFromZero(double d, int decimals)
    {
        if (decimals > 15) return d;
        return Math.Round(d, decimals, MidpointRounding.AwayFromZero);
    }

    private static (string order, char sep, bool longForm) DateLayout(SetOptions o)
    {
        var (order, sep) = o.Date switch
        {
            DateFormat.American or DateFormat.Mdy => ("MDY", '/'),
            DateFormat.Usa => ("MDY", '-'),
            DateFormat.Ansi => ("YMD", '.'),
            DateFormat.British or DateFormat.French or DateFormat.Dmy => ("DMY", '/'),
            DateFormat.German => ("DMY", '.'),
            DateFormat.Italian => ("DMY", '-'),
            DateFormat.Japan or DateFormat.Taiwan or DateFormat.Ymd => ("YMD", '/'),
            _ => ("MDY", '/'),
        };
        return (order, o.Mark ?? sep, o.Date == DateFormat.Long);
    }

    public static string DateToString(long julian, SetOptions o)
    {
        var (order, sep, longForm) = DateLayout(o);
        var yearWidth = o.Century ? 4 : 2;
        if (julian == 0)
        {
            var parts = order.Select(c => new string(' ', c == 'Y' ? yearWidth : 2));
            return string.Join(sep, parts);
        }
        var d = Julian.ToDate(julian);
        if (longForm) return d.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        foreach (var c in order)
        {
            if (sb.Length > 0) sb.Append(sep);
            sb.Append(c switch
            {
                'M' => d.Month.ToString("00"),
                'D' => d.Day.ToString("00"),
                _ => o.Century ? d.Year.ToString("0000") : (d.Year % 100).ToString("00"),
            });
        }
        return sb.ToString();
    }

    public static string TimeToString(long msOfDay, SetOptions o, bool empty = false)
    {
        var t = TimeSpan.FromMilliseconds(msOfDay);
        if (empty) return o.Hours == 12 ? (o.Seconds ? "  :  :   " : "  :   ") + "  " : (o.Seconds ? "  :  :  " : "  :  ");
        var h = t.Hours;
        string suffix = "";
        if (o.Hours == 12)
        {
            suffix = h >= 12 ? " PM" : " AM";
            h %= 12;
            if (h == 0) h = 12;
        }
        var s = $"{h:00}:{t.Minutes:00}";
        if (o.Seconds) s += $":{t.Seconds:00}";
        return s + suffix;
    }

    public static string DateTimeToString(long julianMs, SetOptions o)
    {
        if (julianMs == 0) return DateToString(0, o) + " " + TimeToString(0, o, empty: true);
        return DateToString(julianMs / Julian.MsPerDay, o) + " " + TimeToString(julianMs % Julian.MsPerDay, o);
    }

    /// <summary>Parses a date in the current SET DATE format (CTOD). Returns 0 (empty) if invalid.</summary>
    public static long ParseDate(string s, SetOptions o)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        var (order, _, _) = DateLayout(o);
        var parts = s.Split(['/', '-', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return 0;
        int y = 0, m = 0, d = 0;
        for (int i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], out var n)) return 0;
            switch (order[i]) { case 'Y': y = n; break; case 'M': m = n; break; default: d = n; break; }
        }
        if (parts[order.IndexOf('Y')].Length <= 2) y += y < 50 ? 2000 : 1900; // SET ROLLOVER-style default
        try { return Julian.FromDate(new DateOnly(y, m, d)); }
        catch (ArgumentOutOfRangeException) { return 0; }
    }
}
