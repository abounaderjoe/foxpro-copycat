namespace JoePro.Core;

/// <summary>
/// A table column definition using VFP field type letters:
/// C Character, V Varchar, M Memo, N Numeric, F Float, B Double, I Integer, Y Currency,
/// D Date, T DateTime, L Logical, G General, W Blob, Q Varbinary.
/// </summary>
public sealed record FieldDef(string Name, char Type, int Width = 0, int Decimals = 0)
{
    public bool Nullable { get; init; }
    /// <summary>True when the field is stored without code page translation (NOCPTRANS / binary).</summary>
    public bool Binary { get; init; }
    public long? AutoIncNext { get; init; }
    public int AutoIncStep { get; init; } = 1;
    public string? DefaultExpr { get; init; }
    public string? RuleExpr { get; init; }
    public string? RuleText { get; init; }
    public string? Caption { get; init; }

    public static readonly string ValidTypes = "CVMNFBIYDTLGWQ";

    /// <summary>Returns a copy with default width/decimals applied for fixed-size types.</summary>
    public FieldDef Normalize()
    {
        var t = char.ToUpperInvariant(Type);
        if (!ValidTypes.Contains(t)) throw new VfpException(ErrorCodes.SyntaxError, $"Invalid field type '{Type}'.");
        var w = t switch
        {
            'D' or 'T' or 'B' or 'Y' => 8,
            'I' or 'M' or 'G' or 'W' => 4,
            'L' => 1,
            _ => Width,
        };
        if ((t is 'C' or 'V' or 'Q') && (w < 1 || w > 254)) throw new VfpException(ErrorCodes.SyntaxError, $"Invalid width for field {Name}.");
        if ((t is 'N' or 'F') && (w < 1 || w > 20)) throw new VfpException(ErrorCodes.SyntaxError, $"Invalid width for field {Name}.");
        return this with { Name = Name.ToUpperInvariant(), Type = t, Width = w, Decimals = t is 'N' or 'F' or 'B' ? Decimals : 0 };
    }

    /// <summary>The value a new blank record gets for this field.</summary>
    public Value BlankValue() => Type switch
    {
        'C' => Value.String(new string(' ', Width)),
        'V' or 'M' => Value.EmptyString,
        'N' or 'F' or 'B' or 'I' => Value.Number(0, Decimals),
        'Y' => Value.Currency(0m),
        'D' => Value.EmptyDate,
        'T' => Value.EmptyDateTime,
        'L' => Value.False,
        _ => Value.Binary([]),
    };

    /// <summary>Converts a value for storage in this field, applying width rules. Throws on type mismatch.</summary>
    public Value Coerce(Value v)
    {
        if (v.IsNull)
        {
            return v;
        }
        switch (Type)
        {
            case 'C':
                if (v.Kind != ValueKind.Character) throw VfpException.TypeMismatch();
                var s = v.AsString;
                return Value.String(s.Length >= Width ? s[..Width] : s.PadRight(Width));
            case 'V':
                if (v.Kind != ValueKind.Character) throw VfpException.TypeMismatch();
                var sv = v.AsString;
                return Value.String(sv.Length > Width ? sv[..Width] : sv);
            case 'M':
                if (v.Kind != ValueKind.Character) throw VfpException.TypeMismatch();
                return v;
            case 'N' or 'F' or 'B':
                if (v.Kind is not (ValueKind.Number or ValueKind.Currency)) throw VfpException.TypeMismatch();
                var d = Type == 'B' ? v.AsNumber : Formatter.RoundHalfAwayFromZero(v.AsNumber, Decimals);
                if (Type != 'B' && OverflowsWidth(d)) throw new VfpException(1988, "Numeric overflow. Data was lost.");
                return Value.Number(d, Decimals);
            case 'I':
                if (v.Kind is not (ValueKind.Number or ValueKind.Currency)) throw VfpException.TypeMismatch();
                var n = Math.Round(v.AsNumber, MidpointRounding.AwayFromZero);
                if (n < int.MinValue || n > int.MaxValue) throw new VfpException(1988, "Numeric overflow. Data was lost.");
                return Value.Number(n, 0);
            case 'Y':
                if (v.Kind is not (ValueKind.Number or ValueKind.Currency)) throw VfpException.TypeMismatch();
                return Value.Currency(Math.Round(v.AsCurrency, 4, MidpointRounding.AwayFromZero));
            case 'D':
                if (v.Kind is not (ValueKind.Date or ValueKind.DateTime)) throw VfpException.TypeMismatch();
                return Value.FromJulian(v.JulianDay);
            case 'T':
                if (v.Kind is not (ValueKind.Date or ValueKind.DateTime)) throw VfpException.TypeMismatch();
                return Value.DateTimeFromJulianMs(v.JulianMs);
            case 'L':
                if (v.Kind != ValueKind.Logical) throw VfpException.TypeMismatch();
                return v;
            default:
                if (v.Kind == ValueKind.Character) return Value.Binary(System.Text.Encoding.UTF8.GetBytes(v.AsString));
                if (v.Kind != ValueKind.Binary) throw VfpException.TypeMismatch();
                return v;
        }
    }

    private bool OverflowsWidth(double d)
    {
        var s = Formatter.FormatNumber(d, Decimals, SetOptions.Default);
        return s.Length > Width;
    }
}
