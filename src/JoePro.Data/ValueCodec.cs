using JoePro.Core;

namespace JoePro.Data;

/// <summary>Maps FoxPro values to SQLite storage values and back.</summary>
internal static class ValueCodec
{
    public static string SqlType(FieldDef f) => f.Type switch
    {
        'C' or 'V' or 'M' => "TEXT",
        'N' or 'F' or 'B' => "REAL",
        'I' or 'Y' or 'D' or 'T' or 'L' => "INTEGER",
        _ => "BLOB",
    };

    public static object ToDb(FieldDef f, Value v)
    {
        if (v.IsNull) return DBNull.Value;
        return f.Type switch
        {
            'C' => v.AsString.TrimEnd(' '),
            'V' or 'M' => v.AsString,
            'N' or 'F' or 'B' => v.AsNumber,
            'I' => (long)v.AsNumber,
            'Y' => (long)(v.AsCurrency * 10000m),
            'D' => v.JulianDay,
            'T' => v.JulianMs,
            'L' => v.AsBool ? 1L : 0L,
            _ => v.AsBinary,
        };
    }

    public static Value FromDb(FieldDef f, object? o)
    {
        if (o is null or DBNull) return Value.Null;
        return f.Type switch
        {
            'C' => Value.String(((string)o).PadRight(f.Width)),
            'V' or 'M' => Value.String((string)o),
            'N' or 'F' or 'B' => Value.Number(Convert.ToDouble(o), f.Decimals),
            'I' => Value.Number(Convert.ToInt64(o), 0),
            'Y' => Value.Currency(Convert.ToInt64(o) / 10000m),
            'D' => Value.FromJulian(Convert.ToInt64(o)),
            'T' => Value.DateTimeFromJulianMs(Convert.ToInt64(o)),
            'L' => Value.Logical(Convert.ToInt64(o) != 0),
            _ => Value.Binary(o as byte[] ?? []),
        };
    }
}
