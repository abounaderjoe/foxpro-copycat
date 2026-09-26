using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime.Builtins;

public delegate Value BuiltinFunction(CallContext ctx);

/// <summary>Arguments of a built-in function call. Arguments are evaluated lazily (IIF, array arguments).</summary>
public sealed class CallContext
{
    private readonly Value?[] _values;

    internal CallContext(Interpreter rt, List<Expr> args, string name)
    {
        Rt = rt;
        Exprs = args;
        Name = name;
        _values = new Value?[args.Count];
    }

    public Interpreter Rt { get; }
    public List<Expr> Exprs { get; }
    public string Name { get; }
    public int Count => Exprs.Count;
    public SetOptions Options => Rt.Options;

    public bool Has(int i) => i < Exprs.Count && Exprs[i] is not EmptyArgExpr;

    public Value this[int i]
    {
        get
        {
            if (i >= Exprs.Count) throw VfpException.InvalidArgument();
            return _values[i] ??= Rt.Eval(Exprs[i]);
        }
    }

    public Value Arg(int i, Value fallback) => Has(i) ? this[i] : fallback;

    public string Str(int i)
    {
        var v = this[i];
        return v.Kind == ValueKind.Character ? v.AsString : throw VfpException.InvalidArgument();
    }

    public string Str(int i, string fallback) => Has(i) ? Str(i) : fallback;

    public double Num(int i)
    {
        var v = this[i];
        return v.Kind is ValueKind.Number or ValueKind.Currency ? v.AsNumber : throw VfpException.InvalidArgument();
    }

    public int Int(int i) => (int)Num(i);
    public int Int(int i, int fallback) => Has(i) ? (int)Num(i) : fallback;

    public bool Bool(int i)
    {
        var v = this[i];
        return v.Kind == ValueKind.Logical ? v.AsBool : v.Kind is ValueKind.Number ? v.AsNumber != 0 : throw VfpException.InvalidArgument();
    }

    public bool Bool(int i, bool fallback) => Has(i) ? Bool(i) : fallback;

    public void Require(int min, int max = int.MaxValue)
    {
        if (Count < min || Count > max) throw new VfpException(ErrorCodes.InvalidArgument, $"Function argument value, type, or count is invalid ({Name.ToUpperInvariant()}).");
    }

    public string ArrayName(int i) => Exprs[i] switch
    {
        NameExpr n => n.Name,
        ByRefExpr b => b.Name,
        MemVarExpr m => m.Name,
        CallExpr { Args.Count: 0 } c => c.Name,
        _ => throw VfpException.InvalidArgument(),
    };

    /// <summary>Returns the array named by argument <paramref name="i"/>.</summary>
    public VfpArray Array(int i)
    {
        if (Exprs[i] is MemberExpr me) return Rt.ResolveArray(me) ?? throw VfpException.InvalidArgument();
        var name = ArrayName(i);
        return Rt.FindVariable(name)?.Array ?? throw new VfpException(232, $"'{name.ToUpperInvariant()}' is not an array.", name);
    }

    /// <summary>Creates or replaces the array named by argument <paramref name="i"/> (AFIELDS, ALINES, ADIR…).</summary>
    public VfpArray NewArray(int i, int rows, int cols)
    {
        var name = ArrayName(i);
        var v = Rt.FindVariable(name);
        if (v == null)
        {
            Rt.SetVariable(name, Value.False);
            v = Rt.FindVariable(name)!;
        }
        v.Array = new VfpArray(Math.Max(1, rows), cols);
        return v.Array;
    }

    public WorkArea Area(int i) => Has(i) ? Rt.ResolveWorkArea(Rt.EvalAlias(Exprs[i])) : Rt.Session.Current;
}

/// <summary>Accumulates SUM/COUNT/AVG/MIN/MAX (SQL aggregates and CALCULATE).</summary>
public sealed class AggregateAccumulator
{
    private readonly HashSet<string>? _distinct;
    private int _count;
    private double _sum;
    private decimal _sumCurrency;
    private bool _currency;
    private int _decimals;
    private Value _min = Value.Null, _max = Value.Null;
    private double _sumSq;

    public bool Distinct { get => _distinct != null; init { if (value) _distinct = new HashSet<string>(); } }

    public void Add(Value v, Interpreter rt)
    {
        if (v.IsNull) return;
        if (_distinct != null && !_distinct.Add(v.VarType + Formatter.ToDisplay(v, SetOptions.Default).Trim())) return;
        _count++;
        if (v.Kind is ValueKind.Number or ValueKind.Currency)
        {
            if (v.Kind == ValueKind.Currency) { _currency = true; _sumCurrency += v.AsCurrency; }
            _sum += v.AsNumber;
            _sumSq += v.AsNumber * v.AsNumber;
            _decimals = Math.Max(_decimals, v.Decimals);
        }
        if (_min.IsNull || VfpCompare.Compare(v, _min, StringCompareMode.Padded) < 0) _min = v;
        if (_max.IsNull || VfpCompare.Compare(v, _max, StringCompareMode.Padded) > 0) _max = v;
    }

    public Value Result(string fn, SetOptions o) => fn switch
    {
        "COUNT" or "CNT" => Value.Number(_count, 0),
        "SUM" => _count == 0 ? Value.Null : _currency ? Value.Currency(_sumCurrency) : Value.Number(_sum, _decimals),
        "AVG" or "AVERAGE" => _count == 0 ? Value.Null : _currency ? Value.Currency(Math.Round(_sumCurrency / _count, 4)) : Value.Number(_sum / _count, Math.Max(_decimals, o.Decimals)),
        "MIN" => _min,
        "MAX" => _max,
        "STD" => _count == 0 ? Value.Null : Value.Number(Math.Sqrt(Math.Max(0, _sumSq / _count - Math.Pow(_sum / _count, 2))), Math.Max(_decimals, o.Decimals)),
        "VAR" => _count == 0 ? Value.Null : Value.Number(Math.Max(0, _sumSq / _count - Math.Pow(_sum / _count, 2)), Math.Max(_decimals, o.Decimals)),
        _ => throw VfpException.InvalidArgument(),
    };
}

/// <summary>The built-in function registry. Names may be abbreviated to four characters, as in VFP.</summary>
public static partial class Library
{
    private static readonly Dictionary<string, BuiltinFunction> Functions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BuiltinFunction?> Resolved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX", "CNT", "COUNT_DISTINCT", "SUM_DISTINCT", "AVG_DISTINCT", "CNT_DISTINCT",
    };

    static Library()
    {
        RegisterStrings();
        RegisterMath();
        RegisterDates();
        RegisterData();
        RegisterMisc();
        RegisterRemote();
    }

    public static IReadOnlyCollection<string> Names => Functions.Keys;

    private static void Add(string name, BuiltinFunction fn) => Functions[name] = fn;

    private static void Add(IEnumerable<string> names, BuiltinFunction fn)
    {
        foreach (var n in names) Functions[n] = fn;
    }

    public static bool IsAggregate(string name) => Aggregates.Contains(name);

    public static bool TryGet(string name, out BuiltinFunction fn)
    {
        lock (Resolved)
        {
            if (!Resolved.TryGetValue(name, out var f))
            {
                if (!Functions.TryGetValue(name, out f) && name.Length >= 4)
                {
                    var matches = Functions.Keys.Where(k => k.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    f = matches.Count == 1 ? Functions[matches[0]] : matches.Count > 1 ? Functions[matches.OrderBy(m => m.Length).First()] : null;
                }
                Resolved[name] = f;
            }
            fn = f!;
            return f != null;
        }
    }

    // ---- Shared helpers ---------------------------------------------------------------------

    /// <summary>TRANSFORM(x) with no format: the default text representation.</summary>
    public static string TransformDefault(Value v, SetOptions o) => v.Kind switch
    {
        ValueKind.Character => v.AsString,
        ValueKind.Number => Formatter.FormatNumber(v.AsNumber, v.Decimals, o),
        ValueKind.Currency => Formatter.FormatNumber((double)v.AsCurrency, 4, o, v.AsCurrency),
        _ => Formatter.ToDisplay(v, o),
    };

    /// <summary>LIKE() wildcard match: * matches any run of characters, ? matches one.</summary>
    public static bool LikeMatch(string pattern, string text)
    {
        var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text, rx, RegexOptions.Singleline);
    }

    /// <summary>SQL LIKE: % matches any run, _ matches one character.</summary>
    public static bool SqlLike(string pattern, string text)
    {
        var sb = new StringBuilder("^");
        foreach (var c in pattern)
            sb.Append(c switch { '%' => ".*", '_' => ".", _ => Regex.Escape(c.ToString()) });
        sb.Append('$');
        return Regex.IsMatch(text, sb.ToString(), RegexOptions.Singleline);
    }

    internal static Value N(double d, int decimals = 0) => Value.Number(d, decimals);
    internal static Value S(string s) => Value.String(s);
    internal static Value L(bool b) => Value.Logical(b);

    /// <summary>STR() formatting: right-justified, rounded, asterisks on overflow.</summary>
    public static string Str(double n, int len, int dec, SetOptions o)
    {
        if (len < 1) len = 1;
        dec = Math.Max(0, dec);
        var s = Formatter.FormatNumber(n, dec, o);
        while (s.Length > len && dec > 0)
        {
            dec--;
            s = Formatter.FormatNumber(n, dec, o);
        }
        if (s.Length > len)
        {
            var e = n.ToString("0.#E+00", CultureInfo.InvariantCulture);
            return e.Length <= len ? e.PadLeft(len) : new string('*', len);
        }
        return s.PadLeft(len);
    }

    /// <summary>TRANSFORM with a format string (@ functions and/or a picture mask).</summary>
    public static string Transform(Value v, string format, SetOptions o)
    {
        string funcs = "", mask = format;
        if (format.StartsWith('@'))
        {
            var sp = format.IndexOf(' ');
            funcs = (sp < 0 ? format[1..] : format[1..sp]).ToUpperInvariant();
            mask = sp < 0 ? "" : format[(sp + 1)..];
        }
        string result;
        if (v.Kind is ValueKind.Number or ValueKind.Currency)
        {
            var d = v.AsNumber;
            if (funcs.Contains('Z') && d == 0) return new string(' ', Math.Max(mask.Length, 1));
            if (mask.Length > 0) result = NumberMask(d, mask, funcs.Contains('L'), o);
            else result = TransformDefault(v, o);
            if (funcs.Contains('$')) result = "$" + result.TrimStart();
            if (funcs.Contains('(') && d < 0) result = "(" + result.Replace("-", "").Trim() + ")";
        }
        else if (v.Kind == ValueKind.Character)
        {
            var s = v.AsString;
            if (mask.Length > 0) s = CharMask(s, mask);
            result = s;
        }
        else if (v.Kind is ValueKind.Date or ValueKind.DateTime)
        {
            if (funcs.Contains('E')) { var o2 = o.Clone(); o2.Date = DateFormat.British; result = Formatter.ToDisplay(v, o2); }
            else if (funcs.Contains('D')) result = Formatter.ToDisplay(v, o);
            else result = Formatter.ToDisplay(v, o);
        }
        else result = TransformDefault(v, o);
        if (funcs.Contains('!')) result = result.ToUpperInvariant();
        if (funcs.Contains('T')) result = result.Trim();
        if (funcs.Contains('B')) result = result.Trim();
        return result;
    }

    private static string NumberMask(double d, string mask, bool leadingZeros, SetOptions o)
    {
        var dot = mask.IndexOf('.');
        int decimals = dot < 0 ? 0 : mask[(dot + 1)..].Count(c => c is '9' or '#');
        var digits = Formatter.FormatNumber(Math.Abs(d), decimals, o);
        var intPart = decimals > 0 ? digits[..digits.IndexOf(o.Point)] : digits;
        var fracPart = decimals > 0 ? digits[(digits.IndexOf(o.Point) + 1)..] : "";
        var intMask = dot < 0 ? mask : mask[..dot];
        int slots = intMask.Count(c => c is '9' or '#');
        if (d < 0) intPart = "-" + intPart;
        if (intPart.Length > slots) return new string('*', mask.Length);
        var padded = leadingZeros ? intPart.PadLeft(slots, '0') : intPart.PadLeft(slots);
        var sb = new StringBuilder();
        int pi = 0;
        bool started = false;
        foreach (var c in intMask)
        {
            if (c is '9' or '#')
            {
                var ch = padded[pi++];
                sb.Append(ch);
                if (ch != ' ') started = true;
            }
            else if (c == ',') sb.Append(started ? o.Separator : ' ');
            else sb.Append(c);
        }
        if (dot >= 0)
        {
            sb.Append(o.Point);
            int fi = 0;
            foreach (var c in mask[(dot + 1)..]) sb.Append(c is '9' or '#' ? fracPart[fi++] : c);
        }
        return sb.ToString();
    }

    private static string CharMask(string s, string mask)
    {
        var sb = new StringBuilder();
        int si = 0;
        foreach (var c in mask)
        {
            if (c is 'X' or '9' or 'A' or 'N' or '!' or '#')
            {
                var ch = si < s.Length ? s[si++] : ' ';
                sb.Append(c == '!' ? char.ToUpperInvariant(ch) : ch);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
