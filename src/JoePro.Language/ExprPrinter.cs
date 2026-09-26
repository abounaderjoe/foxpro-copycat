using System.Globalization;
using System.Text;
using JoePro.Core;

namespace JoePro.Language;

/// <summary>Turns an expression tree back into FoxPro source text (used for index keys, rules and defaults).</summary>
public static class ExprPrinter
{
    public static string Print(Expr e)
    {
        var sb = new StringBuilder();
        Write(sb, e, 0);
        return sb.ToString();
    }

    private static int Precedence(string op) => op switch
    {
        "OR" => 1,
        "AND" => 2,
        "=" or "==" or "<>" or "<" or ">" or "<=" or ">=" or "$" => 4,
        "+" or "-" => 5,
        "*" or "/" or "%" => 6,
        "^" => 8,
        _ => 9,
    };

    private static void Write(StringBuilder sb, Expr e, int parentPrec)
    {
        switch (e)
        {
            case LiteralExpr l:
                sb.Append(Literal(l.Value));
                break;
            case NameExpr n:
                sb.Append(n.Name);
                break;
            case MemVarExpr m:
                sb.Append("m.").Append(m.Name);
                break;
            case MacroExpr m:
                sb.Append('&').Append(m.VarName).Append('.');
                break;
            case SpecialObjectExpr s:
                if (s.Which != "WITH") sb.Append(s.Which);
                break;
            case MemberExpr m:
                Write(sb, m.Target, 10);
                sb.Append('.').Append(m.Name);
                break;
            case AliasFieldExpr a:
                sb.Append(a.Alias).Append("->").Append(a.Field);
                break;
            case CallExpr c:
                sb.Append(c.Name).Append('(');
                Args(sb, c.Args);
                sb.Append(')');
                break;
            case MethodCallExpr mc:
                Write(sb, mc.Target, 10);
                sb.Append('.').Append(mc.Name).Append('(');
                Args(sb, mc.Args);
                sb.Append(')');
                break;
            case IndexExpr ix:
                Write(sb, ix.Target, 10);
                sb.Append('[');
                Args(sb, ix.Args);
                sb.Append(']');
                break;
            case ByRefExpr b:
                sb.Append('@').Append(b.Name);
                break;
            case EmptyArgExpr:
                break;
            case UnaryExpr u:
                if (u.Op == "NOT") { sb.Append('!'); Write(sb, u.Operand, 7); }
                else { sb.Append(u.Op); Write(sb, u.Operand, 7); }
                break;
            case BinaryExpr b:
            {
                var p = Precedence(b.Op);
                if (p < parentPrec) sb.Append('(');
                Write(sb, b.Left, p);
                sb.Append(b.Op is "AND" or "OR" ? $" {b.Op} " : b.Op);
                Write(sb, b.Right, p + 1);
                if (p < parentPrec) sb.Append(')');
                break;
            }
            case InListExpr i:
                Write(sb, i.Value, 4);
                sb.Append(i.Not ? " NOT IN (" : " IN (");
                Args(sb, i.Items);
                sb.Append(')');
                break;
            case BetweenExpr bt:
                Write(sb, bt.Value, 4);
                sb.Append(bt.Not ? " NOT BETWEEN " : " BETWEEN ");
                Write(sb, bt.Low, 5);
                sb.Append(" AND ");
                Write(sb, bt.High, 5);
                break;
            case LikeExpr lk:
                Write(sb, lk.Value, 4);
                sb.Append(lk.Not ? " NOT LIKE " : " LIKE ");
                Write(sb, lk.Pattern, 5);
                break;
            case CastExpr ce:
                sb.Append("CAST(");
                Write(sb, ce.Value, 0);
                sb.Append(" AS ").Append(ce.Type);
                if (ce.Width > 0) sb.Append('(').Append(ce.Width).Append(ce.Decimals > 0 ? "," + ce.Decimals : "").Append(')');
                if (ce.Nullable == true) sb.Append(" NULL"); else if (ce.Nullable == false) sb.Append(" NOT NULL");
                sb.Append(')');
                break;
            case ViewParamExpr vp:
                sb.Append('?');
                if (vp.Inner is NameExpr or MemVarExpr or MemberExpr) Write(sb, vp.Inner, 9);
                else { sb.Append('('); Write(sb, vp.Inner, 0); sb.Append(')'); }
                break;
            case IsNullExpr isn:
                Write(sb, isn.Value, 4);
                sb.Append(isn.Not ? " IS NOT NULL" : " IS NULL");
                break;
            default:
                sb.Append("(?)");
                break;
        }
    }

    private static void Args(StringBuilder sb, List<Expr> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Write(sb, args[i], 0);
        }
    }

    public static string Literal(Value v) => v.Kind switch
    {
        ValueKind.Character => v.AsString.Contains('"')
            ? (v.AsString.Contains('\'') ? "[" + v.AsString + "]" : "'" + v.AsString + "'")
            : "\"" + v.AsString + "\"",
        ValueKind.Number => v.AsNumber.ToString(v.Decimals > 0 ? "F" + v.Decimals : "R", CultureInfo.InvariantCulture),
        ValueKind.Currency => "$" + v.AsCurrency.ToString(CultureInfo.InvariantCulture),
        ValueKind.Logical => v.AsBool ? ".T." : ".F.",
        ValueKind.Null => ".NULL.",
        ValueKind.Date => v.IsEmptyDate ? "{}" : "{^" + Julian.ToDate(v.JulianDay).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "}",
        ValueKind.DateTime => v.IsEmptyDate ? "{/:}" : "{^" + Julian.ToDateTime(v.JulianMs).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "}",
        _ => "0h" + Convert.ToHexString(v.Kind == ValueKind.Binary ? v.AsBinary : []),
    };
}
