using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime;

public sealed partial class Interpreter
{
    /// <summary>Set while SELECT-SQL evaluates expressions: resolves names against the current joined row.</summary>
    internal SqlRowContext? SqlContext { get; set; }

    internal StringCompareMode CompareMode(bool identical = false) =>
        identical ? StringCompareMode.Identical
        : SqlContext != null ? (Options.Ansi ? StringCompareMode.Padded : StringCompareMode.RightLength)
        : (Options.Exact ? StringCompareMode.Padded : StringCompareMode.RightLength);

    // ================================================================================
    // Evaluation
    // ================================================================================

    internal Value Eval(Expr e)
    {
        switch (e)
        {
            case LiteralExpr l:
                return l.Value;
            case NameExpr n:
                return ResolveName(n.Name);
            case MemVarExpr m:
                return GetVariable(m.Name);
            case AliasFieldExpr af:
                return FieldOfAlias(af.Alias, af.Field);
            case MemberExpr m:
                return EvalMember(m);
            case CallExpr c:
                return EvalCall(c);
            case IndexExpr ix:
                return EvalIndex(ix);
            case MethodCallExpr mc:
                return CallMethod(mc);
            case UnaryExpr u:
                return EvalUnary(u);
            case BinaryExpr b:
                return EvalBinary(b);
            case SpecialObjectExpr s:
                return Value.Object(ResolveSpecial(s.Which));
            case MacroExpr m:
            {
                var text = GetVariable(m.VarName);
                if (text.Kind != ValueKind.Character) throw VfpException.TypeMismatch();
                return Eval(Parser.ParseExpression(text.AsString));
            }
            case ByRefExpr br:
                return GetVariable(br.Name);
            case EmptyArgExpr:
                return Value.False;
            case InListExpr il:
                return SqlEngine.EvalInList(this, il);
            case BetweenExpr bt:
            {
                var v = Eval(bt.Value);
                var lo = Eval(bt.Low);
                var hi = Eval(bt.High);
                if (v.IsNull || lo.IsNull || hi.IsNull) return Value.Null;
                var mode = CompareMode();
                var r = VfpCompare.Compare(v, lo, mode) >= 0 && VfpCompare.Compare(v, hi, mode) <= 0;
                return Value.Logical(r != bt.Not);
            }
            case LikeExpr lk:
            {
                var v = Eval(lk.Value);
                var p = Eval(lk.Pattern);
                if (v.IsNull || p.IsNull) return Value.Null;
                var r = Builtins.Library.SqlLike(p.AsString.TrimEnd(), v.AsString.TrimEnd());
                return Value.Logical(r != lk.Not);
            }
            case IsNullExpr isn:
                return Value.Logical(Eval(isn.Value).IsNull != isn.Not);
            case ViewParamExpr vp:
                return Eval(vp.Inner);
            case ExistsExpr ex:
                return Value.Logical(SqlEngine.Execute(this, ex.Query, materialize: false).Rows.Count > 0);
            case SubqueryExpr sq:
            {
                var r = SqlEngine.Execute(this, sq.Query, materialize: false);
                return r.Rows.Count > 0 ? r.Rows[0][0] : Value.Null;
            }
            default:
                throw VfpException.NotSupported(e.GetType().Name);
        }
    }

    /// <summary>Unqualified name: SQL row, index/filter record, current work area field, then memory variable.</summary>
    private Value ResolveName(string name)
    {
        if (SqlContext != null && SqlContext.TryResolve(name, out var sv)) return sv;
        if (RecordContext is { } rec)
        {
            var idx = rec.Table.Schema.FieldIndex(name);
            if (idx >= 0) return rec.Get(idx);
        }
        var wa = Session.Current;
        if (wa.InUse)
        {
            var idx = wa.FieldIndex(name);
            if (idx >= 0) return wa.Get(idx);
        }
        var v = FindVariable(name);
        if (v != null) return v.IsArray ? v.Array![1] : v.Value;
        throw VfpException.VariableNotFound(name);
    }

    internal WorkArea ResolveWorkArea(Value v)
    {
        if (v.Kind == ValueKind.Number) return Session.Area((int)v.AsNumber == 0 ? Session.CurrentAreaNumber : (int)v.AsNumber);
        if (v.Kind == ValueKind.Character)
        {
            var s = v.AsString.Trim();
            if (s.Length == 1 && char.IsLetter(s[0]) && Session.FindAlias(s) == null && char.ToUpperInvariant(s[0]) <= 'J')
                return Session.Area(char.ToUpperInvariant(s[0]) - 'A' + 1); // work areas A–J
            return Session.ResolveAlias(s);
        }
        throw VfpException.InvalidArgument();
    }

    internal WorkArea AreaOf(Expr? inClause) => inClause == null ? Session.Current : ResolveWorkArea(EvalAlias(inClause));

    internal Value EvalAlias(Expr e) => e switch
    {
        LiteralExpr l => l.Value,
        MacroExpr m => GetVariable(m.VarName),
        NameExpr n => Value.String(n.Name),
        _ => Eval(e),
    };

    private Value FieldOfAlias(string alias, string field)
    {
        if (SqlContext != null && SqlContext.TryResolve(alias, field, out var sv)) return sv;
        if (alias.Equals("M", StringComparison.OrdinalIgnoreCase)) return GetVariable(field);
        var wa = Session.ResolveAlias(alias);
        var i = wa.FieldIndex(field);
        if (i < 0) throw VfpException.FieldNotFound(field);
        if (RecordContext is { } rec && rec.Table == wa.Table) return rec.Get(i);
        return wa.Get(i);
    }

    private Value EvalMember(MemberExpr m)
    {
        if (m.Target is NameExpr n)
        {
            if (SqlContext != null && SqlContext.TryResolve(n.Name, m.Name, out var sv)) return sv;
            var v = FindVariable(n.Name);
            if (v != null && !v.IsArray && v.Value.Kind == ValueKind.Object)
                return v.Value.AsObject is ClrObjectProxy px ? px.Get(m.Name) : GetPropertyChecked((VfpObject)v.Value.AsObject, m.Name);
            if (Session.FindAlias(n.Name) != null) return FieldOfAlias(n.Name, m.Name);
            if (n.Name.Equals("M", StringComparison.OrdinalIgnoreCase)) return GetVariable(m.Name);
            if (v == null) throw VfpException.AliasNotFound(n.Name);
        }
        var target = Eval(m.Target);
        if (target.Kind != ValueKind.Object) throw new VfpException(1924, $"{ExprPrinter.Print(m.Target).ToUpperInvariant()} is not an object.");
        if (target.AsObject is ClrObjectProxy proxy) return proxy.Get(m.Name);
        return GetPropertyChecked((VfpObject)target.AsObject, m.Name);
    }

    private Value EvalCall(CallExpr c)
    {
        // An array variable takes precedence over a function with the same name.
        if (FindVariable(c.Name) is { IsArray: true } av && !(SqlContext != null && Builtins.Library.IsAggregate(c.Name)))
            return av.Array![ArrayIndex(av.Array, c.Args)];
        if (SqlContext?.Aggregates != null && Builtins.Library.IsAggregate(c.Name))
            return SqlContext.EvalAggregate(this, c);
        if (Builtins.Library.TryGet(c.Name, out var fn))
            return fn(new Builtins.CallContext(this, c.Args, c.Name));
        var result = TryCallUserFunction(c.Name, EvalArgs(c.Args, byRefVariables: false));
        if (result != null) return result.Value;
        throw new VfpException(1, $"File '{c.Name.ToLowerInvariant()}.prg' does not exist.", c.Name);
    }

    internal int ArrayIndex(VfpArray a, List<Expr> args)
    {
        if (args.Count == 0) return 1;
        var i = (int)Eval(args[0]).AsNumber;
        if (args.Count == 1) return i;
        var j = (int)Eval(args[1]).AsNumber;
        return a.Index(i, j);
    }

    private Value EvalIndex(IndexExpr ix)
    {
        var arr = ResolveArray(ix.Target) ?? throw VfpException.InvalidSubscript();
        return arr[ArrayIndex(arr, ix.Args)];
    }

    internal VfpArray? ResolveArray(Expr target)
    {
        switch (target)
        {
            case NameExpr n:
                return FindVariable(n.Name)?.Array;
            case MemVarExpr mv:
                return FindVariable(mv.Name)?.Array;
            case MemberExpr m:
            {
                var o = Eval(m.Target);
                if (o.Kind != ValueKind.Object) return null;
                return ((VfpObject)o.AsObject).FindProperty(m.Name)?.Array;
            }
            default:
                return null;
        }
    }

    private Value EvalUnary(UnaryExpr u)
    {
        var v = Eval(u.Operand);
        if (u.Op == "NOT")
        {
            if (v.IsNull) return Value.Null;
            if (v.Kind != ValueKind.Logical) throw VfpException.OperatorTypeMismatch();
            return Value.Logical(!v.AsBool);
        }
        if (v.IsNull) return Value.Null;
        return v.Kind switch
        {
            ValueKind.Number => Value.Number(-v.AsNumber, v.Decimals),
            ValueKind.Currency => Value.Currency(-v.AsCurrency),
            _ => throw VfpException.OperatorTypeMismatch(),
        };
    }

    private Value EvalBinary(BinaryExpr b)
    {
        if (b.Op is "AND" or "OR")
        {
            var l = Eval(b.Left);
            if (l.Kind is not (ValueKind.Logical or ValueKind.Null)) throw VfpException.OperatorTypeMismatch();
            if (b.Op == "AND" && l.Kind == ValueKind.Logical && !l.AsBool) return Value.False;
            if (b.Op == "OR" && l.Kind == ValueKind.Logical && l.AsBool) return Value.True;
            var r = Eval(b.Right);
            if (r.Kind is not (ValueKind.Logical or ValueKind.Null)) throw VfpException.OperatorTypeMismatch();
            if (b.Op == "AND")
            {
                if (r.Kind == ValueKind.Logical && !r.AsBool) return Value.False;
                return l.IsNull || r.IsNull ? Value.Null : Value.True;
            }
            if (r.Kind == ValueKind.Logical && r.AsBool) return Value.True;
            return l.IsNull || r.IsNull ? Value.Null : Value.False;
        }
        return Operators.Binary(b.Op, Eval(b.Left), Eval(b.Right), this);
    }

    // ================================================================================
    // Assignment
    // ================================================================================

    internal void Assign(Expr target, Value value)
    {
        switch (target)
        {
            case NameExpr n:
                        SetVariable(n.Name, value);
                break;
            case MemVarExpr mv:
                SetVariable(mv.Name, value);
                break;
            case CallExpr c:
            {
                var v = FindVariable(c.Name);
                if (v?.Array == null) throw new VfpException(ErrorCodes.VariableNotFound, $"Variable '{c.Name.ToUpperInvariant()}' is not found.", c.Name);
                v.Array[ArrayIndex(v.Array, c.Args)] = value;
                break;
            }
            case IndexExpr ix:
            {
                var arr = ResolveArray(ix.Target) ?? throw VfpException.InvalidSubscript();
                arr[ArrayIndex(arr, ix.Args)] = value;
                break;
            }
            case MethodCallExpr mc:
            {
                // obj.aProp(n) = value
                var o = Eval(mc.Target);
                if (o.Kind != ValueKind.Object) throw VfpException.TypeMismatch();
                var arr = ((VfpObject)o.AsObject).FindProperty(mc.Name)?.Array ?? throw VfpException.InvalidSubscript();
                arr[ArrayIndex(arr, mc.Args)] = value;
                break;
            }
            case MemberExpr m:
            {
                if (m.Target is NameExpr n && n.Name.Equals("M", StringComparison.OrdinalIgnoreCase) && FindVariable("M") == null)
                {
                    SetVariable(m.Name, value);
                    break;
                }
                var o = Eval(m.Target);
                if (o.Kind != ValueKind.Object) throw new VfpException(1924, $"{ExprPrinter.Print(m.Target).ToUpperInvariant()} is not an object.");
                if (o.AsObject is ClrObjectProxy proxy) { proxy.Set(m.Name, value); break; }
                CheckAccess((VfpObject)o.AsObject, m.Name);
                SetProperty((VfpObject)o.AsObject, m.Name, value);
                break;
            }
            case MacroExpr mac:
                Assign(Parser.ParseExpression(GetVariable(mac.VarName).AsString), value);
                break;
            default:
                throw VfpException.Syntax("Invalid assignment target.");
        }
    }
}

/// <summary>Arithmetic, concatenation and comparison with FoxPro semantics.</summary>
public static class Operators
{
    public static Value Binary(string op, Value l, Value r, Interpreter rt)
    {
        if (op is "=" or "==" or "<>" or "<" or ">" or "<=" or ">=")
        {
            if (l.IsNull || r.IsNull) return Value.Null;
            var mode = rt.CompareMode(op == "==");
            int c;
            if (op == "<>" && l.Kind == ValueKind.Character && r.Kind == ValueKind.Character)
                c = VfpCompare.CompareStrings(l.AsString, r.AsString, mode);
            else c = VfpCompare.Compare(l, r, mode);
            return Value.Logical(op switch
            {
                "=" or "==" => c == 0,
                "<>" => c != 0,
                "<" => c < 0,
                ">" => c > 0,
                "<=" => c <= 0,
                _ => c >= 0,
            });
        }
        if (op == "$")
        {
            if (l.IsNull || r.IsNull) return Value.Null;
            if (l.Kind != ValueKind.Character || r.Kind != ValueKind.Character) throw VfpException.OperatorTypeMismatch();
            return Value.Logical(r.AsString.Contains(l.AsString, StringComparison.Ordinal));
        }
        if (l.IsNull || r.IsNull)
        {
            if (l.Kind is ValueKind.Object || r.Kind is ValueKind.Object) throw VfpException.OperatorTypeMismatch();
            return Value.Null;
        }
        var set = rt.Options;
        switch (op)
        {
            case "+":
                if (l.Kind == ValueKind.Character && r.Kind == ValueKind.Character) return Value.String(l.AsString + r.AsString);
                if (l.Kind == ValueKind.Date && r.Kind == ValueKind.Number) return Value.FromJulian(l.JulianDay + (long)r.AsNumber);
                if (l.Kind == ValueKind.Number && r.Kind == ValueKind.Date) return Value.FromJulian(r.JulianDay + (long)l.AsNumber);
                if (l.Kind == ValueKind.DateTime && r.Kind == ValueKind.Number) return Value.DateTimeFromJulianMs(l.JulianMs + (long)Math.Round(r.AsNumber * 1000));
                if (l.Kind == ValueKind.Number && r.Kind == ValueKind.DateTime) return Value.DateTimeFromJulianMs(r.JulianMs + (long)Math.Round(l.AsNumber * 1000));
                if (l.Kind == ValueKind.Binary && r.Kind == ValueKind.Binary) return Value.Binary([.. l.AsBinary, .. r.AsBinary]);
                return Numeric(op, l, r, set);
            case "-":
                if (l.Kind == ValueKind.Character && r.Kind == ValueKind.Character)
                {
                    // "-" concatenates, moving the left operand's trailing blanks to the end.
                    var ls = l.AsString;
                    var trimmed = ls.TrimEnd(' ');
                    return Value.String(trimmed + r.AsString + new string(' ', ls.Length - trimmed.Length));
                }
                if (l.Kind == ValueKind.Date && r.Kind == ValueKind.Date) return Value.Number(l.JulianDay - r.JulianDay, 0);
                if (l.Kind == ValueKind.DateTime && r.Kind == ValueKind.DateTime) return Value.Number((l.JulianMs - r.JulianMs) / 1000.0, 0);
                if (l.Kind == ValueKind.Date && r.Kind == ValueKind.Number) return Value.FromJulian(l.JulianDay - (long)r.AsNumber);
                if (l.Kind == ValueKind.DateTime && r.Kind == ValueKind.Number) return Value.DateTimeFromJulianMs(l.JulianMs - (long)Math.Round(r.AsNumber * 1000));
                return Numeric(op, l, r, set);
            default:
                return Numeric(op, l, r, set);
        }
    }

    // TODO(oracle): confirm decimal-place propagation rules for * and / against VFP 9.
    private static Value Numeric(string op, Value l, Value r, SetOptions set)
    {
        if (l.Kind is not (ValueKind.Number or ValueKind.Currency) || r.Kind is not (ValueKind.Number or ValueKind.Currency))
            throw VfpException.OperatorTypeMismatch();
        if (l.Kind == ValueKind.Currency || r.Kind == ValueKind.Currency)
        {
            decimal a = l.AsCurrency, b = r.AsCurrency;
            switch (op)
            {
                case "+": return Value.Currency(a + b);
                case "-": return Value.Currency(a - b);
                case "*": return Value.Currency(Math.Round(a * b, 4, MidpointRounding.AwayFromZero));
                case "/":
                    if (b == 0) throw new VfpException(1307, "Division by zero.");
                    return Value.Currency(Math.Round(a / b, 4, MidpointRounding.AwayFromZero));
            }
        }
        double x = l.AsNumber, y = r.AsNumber;
        int dl = l.Decimals, dr = r.Decimals;
        switch (op)
        {
            case "+": return Value.Number(x + y, Math.Max(dl, dr));
            case "-": return Value.Number(x - y, Math.Max(dl, dr));
            case "*": return Value.Number(x * y, Math.Min(dl + dr, 18));
            case "/":
                if (y == 0) throw new VfpException(1307, "Division by zero.");
                return Value.Number(x / y, Math.Max(set.Decimals, Math.Max(dl, dr)));
            case "%":
                if (y == 0) throw new VfpException(1307, "Division by zero.");
                return Value.Number(Mod(x, y), Math.Max(dl, dr));
            case "^":
                return Value.Number(Math.Pow(x, y), Math.Max(set.Decimals, dl));
        }
        throw VfpException.OperatorTypeMismatch();
    }

    /// <summary>VFP MOD(): the result takes the sign of the divisor.</summary>
    public static double Mod(double x, double y)
    {
        var m = x % y;
        if (m != 0 && Math.Sign(m) != Math.Sign(y)) m += y;
        return m;
    }
}
