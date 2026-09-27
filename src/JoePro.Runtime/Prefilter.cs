using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>
/// Turns the parts of a FOR condition that compare a field with a constant into a SQL condition, so LOCATE, SCAN FOR
/// and the scoped commands jump straight to candidate records instead of evaluating every record (Rushmore's job in VFP).
/// The SQL is a prefilter: every record it lets through still has the full FOR condition evaluated, so it only has to
/// be a superset of the matching records; parts that cannot be expressed that way are simply left out.
/// </summary>
internal sealed class Prefilter
{
    public required string Where { get; init; }
    public required (string, object?)[] Args { get; init; }

    private static readonly HashSet<string> PureFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "DATE", "CTOD", "CTOT", "UPPER", "LOWER", "ALLTRIM", "TRIM", "RTRIM", "LTRIM", "VAL", "INT", "ROUND", "ABS", "PADR", "PADL", "SPACE" };

    public static Prefilter? Build(Interpreter rt, WorkArea wa, Expr? condition)
    {
        if (condition == null || !wa.InUse) return null;
        var b = new Builder(rt, wa);
        var sql = b.Translate(condition);
        return sql == null ? null : new Prefilter { Where = sql, Args = b.Args.ToArray() };
    }

    private sealed class Builder(Interpreter rt, WorkArea wa)
    {
        public List<(string, object?)> Args { get; } = new();

        private string Param(object? v)
        {
            var name = "$p" + Args.Count;
            Args.Add((name, v));
            return name;
        }

        /// <summary>SQL for a superset of the records where <paramref name="e"/> is true, or null for "any record".</summary>
        public string? Translate(Expr e)
        {
            switch (e)
            {
                case BinaryExpr { Op: "AND" } and:
                {
                    var l = Translate(and.Left);
                    var r = Translate(and.Right);
                    return l == null ? r : r == null ? l : $"({l}) AND ({r})";
                }
                case BinaryExpr { Op: "OR" } or:
                {
                    var l = Translate(or.Left);
                    if (l == null) return null;
                    var r = Translate(or.Right);
                    return r == null ? null : $"({l}) OR ({r})";
                }
                case BinaryExpr { Op: "=" or "==" or "<>" or "<" or ">" or "<=" or ">=" } cmp:
                {
                    if (Field(cmp.Left) is { } lf && Constant(cmp.Right) is { } rv) return Compare(lf, cmp.Op, rv);
                    if (Field(cmp.Right) is { } rf && Constant(cmp.Left) is { } lv) return Compare(rf, Flip(cmp.Op), lv);
                    return null;
                }
                case UnaryExpr { Op: "NOT" } not when Field(not.Operand) is { } nf && wa.Table.Fields[nf].Type == 'L':
                    return $"{wa.Table.ColumnSql(nf)} = 0";
                default:
                    return Field(e) is { } bf && wa.Table.Fields[bf].Type == 'L' ? $"{wa.Table.ColumnSql(bf)} = 1" : null;
            }
        }

        private static string Flip(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

        private string? Compare(int field, string op, Value v)
        {
            if (v.IsNull) return null;
            var f = wa.Table.Fields[field];
            var col = wa.Table.ColumnSql(field);
            var sqlOp = op switch { "==" => "=", var o => o };
            switch (char.ToUpperInvariant(f.Type))
            {
                case 'N' or 'F' or 'B' or 'I' when v.Kind is ValueKind.Number or ValueKind.Currency:
                    return $"{col} {sqlOp} {Param(v.AsNumber)}";
                case 'Y' when v.Kind is ValueKind.Number or ValueKind.Currency:
                    return $"{col} {sqlOp} {Param(v.AsNumber * 10000.0)}";
                case 'D' when v.Kind == ValueKind.Date:
                    return $"{col} {sqlOp} {Param(v.JulianDay)}";
                case 'T' when v.Kind == ValueKind.DateTime:
                    return $"{col} {sqlOp} {Param(v.JulianMs)}";
                case 'L' when v.Kind == ValueKind.Logical && op is "=" or "==" or "<>":
                    return $"{col} {(op == "<>" ? "<>" : "=")} {(v.AsBool ? 1 : 0)}";
                case 'C' or 'V' when v.Kind == ValueKind.Character && op == "=" && rt.Options.Collate.Equals("MACHINE", StringComparison.OrdinalIgnoreCase):
                {
                    // "field = cText" (SET EXACT OFF compares up to the length of cText): every match starts with cText's
                    // non-blank part, so a prefix test is a superset whatever SET EXACT says.
                    var text = v.AsString.TrimEnd(' ');
                    if (text.Length == 0) return null;
                    return $"substr({col}, 1, {text.Length}) = {Param(text)}";
                }
                default:
                    return null;
            }
        }

        /// <summary>The index of the work area's field named by <paramref name="e"/>, or null.</summary>
        private int? Field(Expr e)
        {
            string? name = e switch
            {
                NameExpr n => n.Name,
                MemberExpr { Target: NameExpr a } m when a.Name.Equals(wa.Alias, StringComparison.OrdinalIgnoreCase) => m.Name,
                AliasFieldExpr af when af.Alias.Equals(wa.Alias, StringComparison.OrdinalIgnoreCase) => af.Field,
                _ => null,
            };
            if (name == null) return null;
            var i = wa.FieldIndex(name);
            return i >= 0 && wa.Table.Fields[i].Type is not ('M' or 'G' or 'W' or 'Q') ? i : null;
        }

        /// <summary>The value of an expression that cannot change while the command runs (literals, variables, pure calls on them).</summary>
        private Value? Constant(Expr e)
        {
            if (!IsConstant(e)) return null;
            try { return rt.Eval(e); }
            catch (VfpException) { return null; }
        }

        private bool IsConstant(Expr e) => e switch
        {
            LiteralExpr => true,
            MemVarExpr => true,
            NameExpr n => wa.FieldIndex(n.Name) < 0 && rt.FindVariable(n.Name) is { IsArray: false },
            UnaryExpr { Op: "-" } u => IsConstant(u.Operand),
            BinaryExpr { Op: "+" or "-" or "*" or "/" } b => IsConstant(b.Left) && IsConstant(b.Right),
            CallExpr c => PureFunctions.Contains(c.Name) && rt.FindVariable(c.Name) is not { IsArray: true } && c.Args.All(IsConstant),
            _ => false,
        };
    }
}
