using System.Globalization;

namespace JoePro.Core;

/// <summary>Runtime type of a value, using the letters returned by VFP's VARTYPE().</summary>
public enum ValueKind : byte
{
    Logical,   // L
    Number,    // N  (Numeric, Float, Double, Integer)
    Currency,  // Y
    Character, // C  (Character, Varchar, Memo)
    Date,      // D
    DateTime,  // T
    Binary,    // Q  (Varbinary, Blob, General)
    Object,    // O
    Null,      // X
}

/// <summary>
/// An immutable FoxPro value. Numbers carry the number of decimal places used for display,
/// the same way VFP tracks it (for example, 1.50 keeps 2 decimals).
/// Dates are stored as Julian day numbers, where 0 means an empty date <c>{}</c>.
/// DateTimes are stored as milliseconds since Julian day 0, where 0 means empty.
/// </summary>
public readonly struct Value : IEquatable<Value>
{
    private readonly double _num;
    private readonly long _long;
    private readonly object? _ref;

    public ValueKind Kind { get; }

    private Value(ValueKind kind, double num = 0, long l = 0, object? r = null)
    {
        Kind = kind;
        _num = num;
        _long = l;
        _ref = r;
    }

    public static readonly Value Null = new(ValueKind.Null);
    public static readonly Value True = new(ValueKind.Logical, l: 1);
    public static readonly Value False = new(ValueKind.Logical, l: 0);
    public static readonly Value EmptyString = new(ValueKind.Character, r: "");
    public static readonly Value EmptyDate = new(ValueKind.Date, l: 0);
    public static readonly Value EmptyDateTime = new(ValueKind.DateTime, l: 0);
    public static readonly Value Zero = Number(0);

    public static Value Logical(bool b) => b ? True : False;
    public static Value Number(double d, int decimals = -1) =>
        new(ValueKind.Number, d, decimals < 0 ? InferDecimals(d) : decimals);
    public static Value Currency(decimal d) => new(ValueKind.Currency, r: d);
    public static Value String(string s) => new(ValueKind.Character, r: s);
    public static Value Binary(byte[] b) => new(ValueKind.Binary, r: b);
    public static Value Object(object o) => new(ValueKind.Object, r: o);
    public static Value FromJulian(long julianDay) => new(ValueKind.Date, l: julianDay);
    public static Value DateOf(DateOnly d) => FromJulian(Julian.FromDate(d));
    public static Value DateTimeFromJulianMs(long ms) => new(ValueKind.DateTime, l: ms);
    public static Value DateTimeOf(DateTime dt) => DateTimeFromJulianMs(Julian.ToJulianMs(dt));

    private static int InferDecimals(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d) || d == Math.Floor(d)) return 0;
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        var e = s.IndexOfAny(['e', 'E']);
        if (e >= 0) return 2;
        var p = s.IndexOf('.');
        return p < 0 ? 0 : Math.Min(s.Length - p - 1, 18);
    }

    public bool IsNull => Kind == ValueKind.Null;
    public bool AsBool => Kind == ValueKind.Logical ? _long != 0 : throw VfpException.TypeMismatch();
    public double AsNumber => Kind switch
    {
        ValueKind.Number => _num,
        ValueKind.Currency => (double)(decimal)_ref!,
        _ => throw VfpException.TypeMismatch(),
    };
    public decimal AsCurrency => Kind switch
    {
        ValueKind.Currency => (decimal)_ref!,
        ValueKind.Number => (decimal)_num,
        _ => throw VfpException.TypeMismatch(),
    };
    public int Decimals => Kind == ValueKind.Number ? (int)_long : Kind == ValueKind.Currency ? 4 : 0;
    public string AsString => Kind == ValueKind.Character ? (string)_ref! : throw VfpException.TypeMismatch();
    public byte[] AsBinary => Kind == ValueKind.Binary ? (byte[])_ref! : throw VfpException.TypeMismatch();
    public object AsObject => Kind == ValueKind.Object ? _ref! : throw VfpException.TypeMismatch();
    /// <summary>Julian day number for a Date (0 = empty).</summary>
    public long JulianDay => Kind switch
    {
        ValueKind.Date => _long,
        ValueKind.DateTime => _long / Julian.MsPerDay,
        _ => throw VfpException.TypeMismatch(),
    };
    /// <summary>Milliseconds since Julian day 0 for a DateTime (0 = empty).</summary>
    public long JulianMs => Kind switch
    {
        ValueKind.DateTime => _long,
        ValueKind.Date => _long * Julian.MsPerDay,
        _ => throw VfpException.TypeMismatch(),
    };
    public bool IsEmptyDate => (Kind == ValueKind.Date || Kind == ValueKind.DateTime) && _long == 0;

    /// <summary>The letter VARTYPE() returns for this value.</summary>
    public char VarType => Kind switch
    {
        ValueKind.Logical => 'L',
        ValueKind.Number => 'N',
        ValueKind.Currency => 'Y',
        ValueKind.Character => 'C',
        ValueKind.Date => 'D',
        ValueKind.DateTime => 'T',
        ValueKind.Binary => 'Q',
        ValueKind.Object => 'O',
        _ => 'X',
    };

    /// <summary>VFP EMPTY(): blank string, zero, empty date, .F., empty binary.</summary>
    public bool IsEmpty => Kind switch
    {
        ValueKind.Logical => _long == 0,
        ValueKind.Number => _num == 0,
        ValueKind.Currency => (decimal)_ref! == 0m,
        ValueKind.Character => string.IsNullOrWhiteSpace(((string)_ref!).Replace("\t", " ").Replace("\r", " ").Replace("\n", " ")),
        ValueKind.Date or ValueKind.DateTime => _long == 0,
        ValueKind.Binary => ((byte[])_ref!).Length == 0,
        ValueKind.Object => false,
        _ => false,
    };

    public Value WithDecimals(int decimals) =>
        Kind == ValueKind.Number ? new Value(ValueKind.Number, _num, decimals) : this;

    public bool Equals(Value other) =>
        Kind == other.Kind && _num.Equals(other._num) && _long == other._long && Equals(_ref, other._ref);
    public override bool Equals(object? obj) => obj is Value v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Kind, _num, _long, _ref);

    /// <summary>Debug-friendly text. Display formatting lives in <see cref="Formatter"/>.</summary>
    public override string ToString() => Formatter.ToDisplay(this, SetOptions.Default);
}
