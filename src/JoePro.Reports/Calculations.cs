using JoePro.Core;
using JoePro.Documents.Reports;

namespace JoePro.Reports;

/// <summary>One report calculation (a calculated field or a report variable): Count, Sum, Average, Lowest, Highest, StdDev, Variance.</summary>
internal sealed class Accumulator
{
    private readonly CalcType _type;
    private double _count, _sum, _sumSq;
    private int _decimals;
    private Value? _min, _max;

    public Accumulator(CalcType type) => _type = type;

    public void Reset()
    {
        _count = _sum = _sumSq = 0;
        _decimals = 0;
        _min = _max = null;
    }

    public void Add(Value v)
    {
        if (v.IsNull) return;
        _count++;
        if (v.Kind is ValueKind.Number or ValueKind.Currency)
        {
            var d = v.AsNumber;
            _sum += d;
            _sumSq += d * d;
            _decimals = Math.Max(_decimals, v.Decimals);
        }
        if (_min == null || Compare(v, _min.Value) < 0) _min = v;
        if (_max == null || Compare(v, _max.Value) > 0) _max = v;
    }

    private static int Compare(Value a, Value b) => (a.Kind, b.Kind) switch
    {
        (ValueKind.Number or ValueKind.Currency, ValueKind.Number or ValueKind.Currency) => a.AsNumber.CompareTo(b.AsNumber),
        (ValueKind.Character, ValueKind.Character) => string.CompareOrdinal(a.AsString.TrimEnd(), b.AsString.TrimEnd()),
        (ValueKind.Date or ValueKind.DateTime, ValueKind.Date or ValueKind.DateTime) => a.JulianMs.CompareTo(b.JulianMs),
        _ => 0,
    };

    public Value Result(Value initial)
    {
        if (_count == 0) return _type is CalcType.Lowest or CalcType.Highest ? initial : _type == CalcType.None ? initial : Value.Number(0);
        var mean = _sum / _count;
        return _type switch
        {
            CalcType.Count => Value.Number(_count),
            CalcType.Sum => Value.Number(Math.Round(_sum, 10), _decimals),
            CalcType.Average => Value.Number(mean, Math.Max(_decimals, 2)),
            CalcType.Lowest => _min!.Value,
            CalcType.Highest => _max!.Value,
            CalcType.Variance => Value.Number(Math.Max(0, _sumSq / _count - mean * mean), 2),
            CalcType.StdDev => Value.Number(Math.Sqrt(Math.Max(0, _sumSq / _count - mean * mean)), 2),
            _ => initial,
        };
    }
}
