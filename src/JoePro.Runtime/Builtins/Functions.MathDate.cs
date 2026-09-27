using System.Globalization;
using JoePro.Core;

namespace JoePro.Runtime.Builtins;

public static partial class Library
{
    private static readonly Random Rng = new();

    private static Value Num1(CallContext c, Func<double, double> f, int? decimals = null) =>
        NullOr(c, () => N(f(c.Num(0)), decimals ?? Math.Max(c[0].Decimals, c.Options.Decimals)));

    private static void RegisterMath()
    {
        Add("ABS", c => NullOr(c, () => c[0].Kind == ValueKind.Currency ? Value.Currency(Math.Abs(c[0].AsCurrency)) : N(Math.Abs(c.Num(0)), c[0].Decimals)));
        Add("INT", c => NullOr(c, () => c[0].Kind == ValueKind.Currency ? Value.Currency(Math.Truncate(c[0].AsCurrency)) : N(Math.Truncate(c.Num(0)))));
        Add("ROUND", c => NullOr(c, () =>
        {
            var d = c.Int(1);
            if (c[0].Kind == ValueKind.Currency) return Value.Currency(Math.Round(c[0].AsCurrency, Math.Clamp(d, 0, 4), MidpointRounding.AwayFromZero));
            var n = c.Num(0);
            if (d >= 0) return N(Formatter.RoundHalfAwayFromZero(n, d), d);
            var f = Math.Pow(10, -d);
            return N(Math.Round(n / f, MidpointRounding.AwayFromZero) * f);
        }));
        Add("MOD", c => NullOr(c, () =>
        {
            var y = c.Num(1);
            if (y == 0) throw new VfpException(1307, "Division by zero.");
            return N(Operators.Mod(c.Num(0), y), Math.Max(c[0].Decimals, c[1].Decimals));
        }));
        Add("MAX", c => MinMax(c, max: true));
        Add("MIN", c => MinMax(c, max: false));
        Add("SQRT", c => NullOr(c, () =>
        {
            var n = c.Num(0);
            if (n < 0) throw VfpException.InvalidArgument();
            return N(Math.Sqrt(n), Math.Max(c[0].Decimals, c.Options.Decimals));
        }));
        Add("EXP", c => Num1(c, Math.Exp));
        Add("LOG", c => Num1(c, x => x <= 0 ? throw VfpException.InvalidArgument() : Math.Log(x)));
        Add("LOG10", c => Num1(c, x => x <= 0 ? throw VfpException.InvalidArgument() : Math.Log10(x)));
        Add("CEILING", c => NullOr(c, () => N(Math.Ceiling(c.Num(0)))));
        Add("FLOOR", c => NullOr(c, () => N(Math.Floor(c.Num(0)))));
        Add("SIGN", c => NullOr(c, () => N(Math.Sign(c.Num(0)))));
        Add("PI", c => N(Math.PI, Math.Max(c.Options.Decimals, 2)));
        Add("RAND", c =>
        {
            if (c.Has(0))
            {
                var seed = c.Int(0);
                lock (Rng) { _seeded = seed < 0 ? new Random() : new Random(seed); }
            }
            lock (Rng) return N((_seeded ?? Rng).NextDouble(), c.Options.Decimals);
        });
        Add("SIN", c => Num1(c, Math.Sin));
        Add("COS", c => Num1(c, Math.Cos));
        Add("TAN", c => Num1(c, Math.Tan));
        Add("ASIN", c => Num1(c, Math.Asin));
        Add("ACOS", c => Num1(c, Math.Acos));
        Add("ATAN", c => Num1(c, Math.Atan));
        Add("ATN2", c => NullOr(c, () => N(Math.Atan2(c.Num(0), c.Num(1)), c.Options.Decimals)));
        Add("DTOR", c => Num1(c, x => x * Math.PI / 180));
        Add("RTOD", c => Num1(c, x => x * 180 / Math.PI));
        Add("NTOM", c => NullOr(c, () => Value.Currency(Math.Round((decimal)c.Num(0), 4, MidpointRounding.AwayFromZero))));
        Add("MTON", c => NullOr(c, () => N((double)c[0].AsCurrency, 4)));
        Add("FV", c => NullOr(c, () =>
        {
            double pmt = c.Num(0), rate = c.Num(1), n = c.Num(2);
            return N(rate == 0 ? pmt * n : pmt * (Math.Pow(1 + rate, n) - 1) / rate, 2);
        }));
        Add("PV", c => NullOr(c, () =>
        {
            double pmt = c.Num(0), rate = c.Num(1), n = c.Num(2);
            return N(rate == 0 ? pmt * n : pmt * (1 - Math.Pow(1 + rate, -n)) / rate, 2);
        }));
        Add("PAYMENT", c => NullOr(c, () =>
        {
            double pv = c.Num(0), rate = c.Num(1), n = c.Num(2);
            return N(rate == 0 ? pv / n : pv * rate / (1 - Math.Pow(1 + rate, -n)), 2);
        }));
        Add("BITAND", c => N(Enumerable.Range(0, c.Count).Select(i => (long)c.Num(i)).Aggregate((a, b) => a & b)));
        Add("BITOR", c => N(Enumerable.Range(0, c.Count).Select(i => (long)c.Num(i)).Aggregate((a, b) => a | b)));
        Add("BITXOR", c => N(Enumerable.Range(0, c.Count).Select(i => (long)c.Num(i)).Aggregate((a, b) => a ^ b)));
        Add("BITNOT", c => N(~(int)c.Num(0)));
        Add("BITLSHIFT", c => N((int)c.Num(0) << c.Int(1)));
        Add("BITRSHIFT", c => N((int)c.Num(0) >> c.Int(1)));
        Add("BITTEST", c => L((((long)c.Num(0)) & (1L << c.Int(1))) != 0));
        Add("BITSET", c => N((long)c.Num(0) | (1L << c.Int(1))));
        Add("BITCLEAR", c => N((long)c.Num(0) & ~(1L << c.Int(1))));
        Add("RGB", c => N(c.Int(0) + c.Int(1) * 256 + c.Int(2) * 65536));
    }

    private static Random? _seeded;

    private static Value MinMax(CallContext c, bool max)
    {
        c.Require(2);
        Value best = c[0];
        for (int i = 1; i < c.Count; i++)
        {
            var v = c[i];
            if (v.IsNull || best.IsNull) return Value.Null;
            var cmp = VfpCompare.Compare(v, best, StringCompareMode.Padded);
            if (max ? cmp > 0 : cmp < 0) best = v;
        }
        return best;
    }

    // ---- Dates ------------------------------------------------------------------------------

    private static DateOnly D(Value v) => v.IsEmptyDate ? throw VfpException.InvalidArgument() : Julian.ToDate(v.JulianDay);

    private static Value DatePart(CallContext c, Func<DateOnly, int> f) => NullOr(c, () =>
    {
        var v = c[0];
        if (v.Kind is not (ValueKind.Date or ValueKind.DateTime)) throw VfpException.InvalidArgument();
        return v.IsEmptyDate ? N(0) : N(f(D(v)));
    });

    private static Value TimePart(CallContext c, Func<DateTime, int> f) => NullOr(c, () =>
    {
        var v = c[0];
        if (v.Kind != ValueKind.DateTime) throw VfpException.InvalidArgument();
        return v.IsEmptyDate ? N(0) : N(f(Julian.ToDateTime(v.JulianMs)));
    });

    private static readonly string[] DayNames = CultureInfo.InvariantCulture.DateTimeFormat.DayNames;
    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;

    private static void RegisterDates()
    {
        Add("DATE", c =>
        {
            if (c.Count == 0) return Value.DateOf(DateOnly.FromDateTime(DateTime.Now));
            try { return Value.DateOf(new DateOnly(c.Int(0), c.Int(1), c.Int(2))); }
            catch (ArgumentOutOfRangeException) { throw VfpException.InvalidArgument(); }
        });
        Add("DATETIME", c =>
        {
            if (c.Count == 0) { var now = DateTime.Now; return Value.DateTimeOf(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond))); }
            try { return Value.DateTimeOf(new DateTime(c.Int(0), c.Int(1), c.Int(2), c.Int(3, 0), c.Int(4, 0), c.Int(5, 0))); }
            catch (ArgumentOutOfRangeException) { throw VfpException.InvalidArgument(); }
        });
        Add("TIME", c =>
        {
            var now = DateTime.Now;
            var s = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            if (c.Count > 0) s += "." + (now.Millisecond / 10).ToString("00");
            return S(s);
        });
        Add("SECONDS", c => N(DateTime.Now.TimeOfDay.TotalSeconds, 3));
        Add("YEAR", c => DatePart(c, d => d.Year));
        Add("MONTH", c => DatePart(c, d => d.Month));
        Add("DAY", c => DatePart(c, d => d.Day));
        Add("DOW", c => NullOr(c, () =>
        {
            var v = c[0];
            if (v.IsEmptyDate) return N(0);
            var first = c.Int(1, 1);
            if (first == 0) first = c.Options.Fdow;
            var dow = (int)D(v).DayOfWeek + 1; // 1 = Sunday
            return N((dow - first + 7) % 7 + 1);
        }));
        Add("CDOW", c => NullOr(c, () => S(c[0].IsEmptyDate ? "" : DayNames[(int)D(c[0]).DayOfWeek])));
        Add("CMONTH", c => NullOr(c, () => S(c[0].IsEmptyDate ? "" : MonthNames[D(c[0]).Month - 1])));
        Add("DTOC", c => NullOr(c, () =>
        {
            var v = c[0];
            if (c.Has(1)) return S(v.IsEmptyDate ? "        " : D(v).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            return S(Formatter.DateToString(v.JulianDay, c.Options));
        }));
        Add("DTOS", c => NullOr(c, () => S(c[0].IsEmptyDate ? "        " : D(c[0]).ToString("yyyyMMdd", CultureInfo.InvariantCulture))));
        Add("CTOD", c => NullOr(c, () =>
        {
            var s = c.Str(0).Trim();
            if (s.StartsWith('^')) return Parse(s);
            return Value.FromJulian(Formatter.ParseDate(s, c.Options));
        }));
        Add("CTOT", c => NullOr(c, () =>
        {
            var s = c.Str(0).Trim();
            if (s.Length == 0) return Value.EmptyDateTime;
            if (s.StartsWith('^')) return Parse(s) is var v && v.Kind == ValueKind.Date ? Value.DateTimeFromJulianMs(v.JulianMs) : v;
            var parts = s.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var day = Formatter.ParseDate(parts[0], c.Options);
            if (day == 0) return Value.EmptyDateTime;
            if (parts.Length == 1) return Value.DateTimeFromJulianMs(day * Julian.MsPerDay);
            var t = Language.Parser.ParseDateLiteral("^2000-01-01 " + parts[1]);
            return Value.DateTimeFromJulianMs(day * Julian.MsPerDay + t.JulianMs % Julian.MsPerDay);
        }));
        Add("TTOC", c => NullOr(c, () =>
        {
            var v = c[0];
            var mode = c.Int(1, 0);
            if (v.IsEmptyDate) return S(mode == 1 ? new string(' ', 14) : mode == 3 ? "" : Formatter.DateTimeToString(0, c.Options));
            var dt = Julian.ToDateTime(v.JulianMs);
            return S(mode switch
            {
                1 => dt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                2 => Formatter.TimeToString(v.JulianMs % Julian.MsPerDay, c.Options),
                3 => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                _ => Formatter.DateTimeToString(v.JulianMs, c.Options),
            });
        }));
        Add("TTOD", c => NullOr(c, () => Value.FromJulian(c[0].IsEmptyDate ? 0 : c[0].JulianDay)));
        Add("DTOT", c => NullOr(c, () => c[0].IsEmptyDate ? Value.EmptyDateTime : Value.DateTimeFromJulianMs(c[0].JulianDay * Julian.MsPerDay)));
        Add("GOMONTH", c => NullOr(c, () =>
        {
            var v = c[0];
            if (v.IsEmptyDate) return Value.EmptyDate;
            var d = D(v).AddMonths(c.Int(1));
            return v.Kind == ValueKind.DateTime
                ? Value.DateTimeFromJulianMs(Julian.FromDate(d) * Julian.MsPerDay + v.JulianMs % Julian.MsPerDay)
                : Value.DateOf(d);
        }));
        Add("HOUR", c => TimePart(c, t => t.Hour));
        Add("MINUTE", c => TimePart(c, t => t.Minute));
        Add("SEC", c => TimePart(c, t => t.Second));
        Add("WEEK", c => NullOr(c, () =>
        {
            var v = c[0];
            if (v.IsEmptyDate) return N(0);
            var d = D(v);
            var mode = c.Int(1, 1);
            if (mode == 0) mode = c.Options.Fweek;
            var fd = c.Int(2, 1);
            if (fd == 0) fd = c.Options.Fdow;
            var firstDay = (DayOfWeek)((fd + 6) % 7);
            var rule = mode switch { 2 => CalendarWeekRule.FirstFourDayWeek, 3 => CalendarWeekRule.FirstFullWeek, _ => CalendarWeekRule.FirstDay };
            return N(CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(d.ToDateTime(TimeOnly.MinValue), rule, firstDay));
        }));
        Add("DMY", c => NullOr(c, () =>
        {
            var d = D(c[0]);
            return S($"{d.Day} {MonthNames[d.Month - 1]} {(c.Options.Century ? d.Year.ToString("0000") : (d.Year % 100).ToString("00"))}");
        }));
        Add("MDY", c => NullOr(c, () =>
        {
            var d = D(c[0]);
            return S($"{MonthNames[d.Month - 1]} {d.Day:00}, {(c.Options.Century ? d.Year.ToString("0000") : (d.Year % 100).ToString("00"))}");
        }));
        Add("QUARTER", c => DatePart(c, d => (d.Month - 1) / 3 + 1));
    }

    private static Value Parse(string strict) => Language.Parser.ParseDateLiteral(strict);
}
