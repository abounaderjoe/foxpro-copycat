using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime;

public sealed partial class Interpreter
{
    /// <summary>CAST(): converts a value to a field type, as when storing it in a field of that type.</summary>
    internal Value Cast(Value v, CastExpr ce)
    {
        if (v.IsNull) return v;
        var t = ce.Type;
        switch (t)
        {
            case 'C' or 'V' or 'M':
            {
                var text = v.Kind switch
                {
                    ValueKind.Character => v.AsString,
                    ValueKind.Date or ValueKind.DateTime or ValueKind.Logical => Formatter.ToDisplay(v, Options),
                    _ => Builtins.Library.TransformDefault(v, Options),
                };
                if (t == 'C') { var w = ce.Width > 0 ? ce.Width : Math.Max(1, text.Length); return Value.String(text.Length >= w ? text[..w] : text.PadRight(w)); }
                if (t == 'V' && ce.Width > 0 && text.Length > ce.Width) text = text[..ce.Width];
                return Value.String(t == 'V' ? text.TrimEnd() : text);
            }
            case 'N' or 'F' or 'B' or 'I' or 'Y':
            {
                double d = v.Kind switch
                {
                    ValueKind.Number => v.AsNumber,
                    ValueKind.Currency => (double)v.AsCurrency,
                    ValueKind.Character => double.TryParse(v.AsString.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0,
                    ValueKind.Logical => v.AsBool ? 1 : 0,
                    _ => throw VfpException.TypeMismatch(),
                };
                return t switch
                {
                    'I' => Value.Number(Math.Round(d, MidpointRounding.AwayFromZero), 0), // TODO(oracle): rounding vs truncation
                    'Y' => Value.Currency((decimal)d),
                    'B' => Value.Number(d, ce.Decimals),
                    _ => Value.Number(Formatter.RoundHalfAwayFromZero(d, ce.Decimals), ce.Decimals),
                };
            }
            case 'D':
                return v.Kind switch
                {
                    ValueKind.Date => v,
                    ValueKind.DateTime => Value.FromJulian(v.JulianDay),
                    ValueKind.Character => Builtins.Library.CallByName(this, "CTOD", v),
                    _ => throw VfpException.TypeMismatch(),
                };
            case 'T':
                return v.Kind switch
                {
                    ValueKind.DateTime => v,
                    ValueKind.Date => Value.DateTimeFromJulianMs(v.JulianMs),
                    ValueKind.Character => Builtins.Library.CallByName(this, "CTOT", v),
                    _ => throw VfpException.TypeMismatch(),
                };
            case 'L':
                return v.Kind switch
                {
                    ValueKind.Logical => v,
                    ValueKind.Number => Value.Logical(v.AsNumber != 0),
                    ValueKind.Character => Value.Logical(v.AsString.Trim().ToUpperInvariant() is "T" or ".T." or "Y" or ".Y." or "TRUE"),
                    _ => throw VfpException.TypeMismatch(),
                };
            case 'W' or 'Q' or 'G':
                return v.Kind == ValueKind.Binary ? v : Value.Binary(System.Text.Encoding.UTF8.GetBytes(Builtins.Library.TransformDefault(v, Options)));
            default:
                throw VfpException.TypeMismatch();
        }
    }
}
