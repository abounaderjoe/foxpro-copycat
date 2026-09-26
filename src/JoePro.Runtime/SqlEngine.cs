using System.Text;
using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime;

internal sealed class SqlSourceBinding
{
    public required string Alias { get; init; }
    public required List<FieldDef> Fields { get; init; }
    public Dictionary<string, int> Index { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Value[]> Rows { get; init; } = new();

    public void BuildIndex()
    {
        for (int i = 0; i < Fields.Count; i++) Index.TryAdd(Fields[i].Name, i);
    }
}

/// <summary>Name resolution while SELECT-SQL evaluates expressions over a joined row.</summary>
internal sealed class SqlRowContext
{
    public SqlRowContext(List<SqlSourceBinding> sources, SqlRowContext? outer)
    {
        Sources = sources;
        Outer = outer;
        Current = new Value[]?[sources.Count];
    }

    public List<SqlSourceBinding> Sources { get; }
    public SqlRowContext? Outer { get; }
    public Value[]?[] Current { get; set; }
    public List<Value[]?[]>? Aggregates { get; set; }
    public Dictionary<string, Value>? Columns { get; set; }

    public bool TryResolve(string name, out Value value)
    {
        if (Columns != null && Columns.TryGetValue(name, out value)) return true;
        for (int s = 0; s < Sources.Count; s++)
        {
            if (Sources[s].Index.TryGetValue(name, out var i))
            {
                value = Current[s] is { } row ? row[i] : Value.Null;
                return true;
            }
        }
        if (Outer != null) return Outer.TryResolve(name, out value);
        value = default;
        return false;
    }

    public bool TryResolve(string alias, string field, out Value value)
    {
        for (int s = 0; s < Sources.Count; s++)
        {
            if (!Sources[s].Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Sources[s].Index.TryGetValue(field, out var i)) throw VfpException.FieldNotFound(alias + "." + field);
            value = Current[s] is { } row ? row[i] : Value.Null;
            return true;
        }
        if (Outer != null) return Outer.TryResolve(alias, field, out value);
        value = default;
        return false;
    }

    public Value EvalAggregate(Interpreter rt, CallExpr c)
    {
        var name = c.Name.ToUpperInvariant();
        var rows = Aggregates!;
        var saved = Current;
        var savedAgg = Aggregates;
        var acc = new Builtins.AggregateAccumulator { Distinct = name.EndsWith("_DISTINCT") };
        Aggregates = null; // nested aggregates are not allowed; evaluate arguments per row
        try
        {
            foreach (var r in rows)
            {
                Current = r;
                if (name.StartsWith("COUNT") && (c.Args.Count == 0 || c.Args[0] is LiteralExpr { Value.Kind: ValueKind.Character } l && l.Value.AsString == "*"))
                {
                    acc.Add(Value.Number(1), rt);
                    continue;
                }
                acc.Add(rt.Eval(c.Args[0]), rt);
            }
        }
        finally
        {
            Current = saved;
            Aggregates = savedAgg;
        }
        return acc.Result(name.Replace("_DISTINCT", ""), rt.Options);
    }
}

internal sealed class SqlResult
{
    public List<FieldDef> Fields { get; } = new();
    public List<Value[]> Rows { get; } = new();
}

/// <summary>
/// Executes FoxPro SELECT-SQL, INSERT, UPDATE and DELETE. This first engine evaluates queries in
/// memory with FoxPro expression semantics; pushdown of joins and filters to SQLite is planned (ADR 0003).
/// </summary>
internal static class SqlEngine
{
    public static SqlResult Execute(Interpreter rt, SqlSelect q, bool materialize)
    {
        var result = ExecuteCore(rt, q);
        rt.SetPublic("_TALLY", Value.Number(result.Rows.Count, 0));
        if (materialize) Deliver(rt, q, result);
        return result;
    }

    private static SqlResult ExecuteCore(Interpreter rt, SqlSelect q, string? recnoAlias = null)
    {
        var sources = new List<SqlSourceBinding>();
        foreach (var src in q.From) sources.Add(LoadSource(rt, src, recnoAlias));
        foreach (var j in q.Joins) sources.Add(LoadSource(rt, j.Source, recnoAlias));

        var outer = rt.SqlContext;
        var ctx = new SqlRowContext(sources, outer);
        var saved = rt.SqlContext;
        rt.SqlContext = ctx;
        try
        {
            // 1. FROM and JOINs. WHERE conjuncts that touch a single source filter it first (inner joins only);
            //    equality conjuncts become hash joins. Every row is still checked against the full condition.
            var whereParts = q.Where != null ? Conjuncts(q.Where) : [];
            var allInner = q.Joins.All(j => j.Kind is "INNER" or "CROSS");
            if (allInner && whereParts.Count > 0)
            {
                for (int s = 0; s < sources.Count; s++)
                {
                    var local = whereParts.Where(w => Refs(w, sources) is { Count: 1 } r && r.Contains(s)).ToList();
                    if (local.Count == 0) continue;
                    var probe = new Value[]?[sources.Count];
                    sources[s].Rows.RemoveAll(row =>
                    {
                        probe[s] = row;
                        ctx.Current = probe;
                        return !local.All(w => IsTrue(rt.Eval(w)));
                    });
                }
            }
            List<Value[]?[]> tuples;
            int si;
            if (q.From.Count == 0)
            {
                tuples = [new Value[]?[sources.Count]];
                si = 0;
            }
            else
            {
                tuples = sources[0].Rows.Select(r => { var t = new Value[]?[sources.Count]; t[0] = r; return t; }).ToList();
                si = 1;
                for (; si < q.From.Count; si++)
                {
                    // Comma-separated source: an inner join on the WHERE conjuncts that link it to earlier sources.
                    var linking = allInner ? whereParts.Where(w => Refs(w, sources) is { } r && r.Contains(si) && r.All(x => x <= si) && r.Count > 1).ToList() : [];
                    tuples = Join(rt, ctx, tuples, si, sources[si], new SqlJoin("INNER", q.From[si], AndAll(linking)));
                }
            }
            foreach (var j in q.Joins)
            {
                tuples = Join(rt, ctx, tuples, si, sources[si], j);
                si++;
            }

            // 2. WHERE
            if (q.Where != null)
            {
                tuples = tuples.Where(t =>
                {
                    ctx.Current = t;
                    return IsTrue(rt.Eval(q.Where));
                }).ToList();
            }

            // 3. Columns
            var columns = ExpandColumns(q, sources);
            var hasAgg = columns.Any(c => HasAggregate(c.Expr)) || (q.Having != null && HasAggregate(q.Having));
            var result = new SqlResult();
            var repTuples = new List<Value[]?[]>();

            if (q.GroupBy.Count > 0 || hasAgg)
            {
                var groupExprs = q.GroupBy.Select(g => ResolveColumnRef(g, columns) ?? g).ToList();
                var groups = new List<List<Value[]?[]>>();
                var index = new Dictionary<string, int>();
                foreach (var t in tuples)
                {
                    ctx.Current = t;
                    var key = RowKey(groupExprs.Select(rt.Eval));
                    if (!index.TryGetValue(key, out var gi))
                    {
                        gi = groups.Count;
                        index[key] = gi;
                        groups.Add(new List<Value[]?[]>());
                    }
                    groups[gi].Add(t);
                }
                if (groups.Count == 0 && q.GroupBy.Count == 0) groups.Add(new List<Value[]?[]>());
                foreach (var g in groups)
                {
                    ctx.Aggregates = g;
                    ctx.Current = g.Count > 0 ? g[0] : new Value[]?[sources.Count];
                    var values = columns.Select(c => rt.Eval(c.Expr)).ToArray();
                    if (q.Having != null)
                    {
                        ctx.Columns = columns.Select((c, i) => (c.Name, values[i])).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(x => x.Key, x => x.First().Item2, StringComparer.OrdinalIgnoreCase);
                        var keep = IsTrue(rt.Eval(q.Having));
                        ctx.Columns = null;
                        if (!keep) continue;
                    }
                    result.Rows.Add(values);
                    repTuples.Add(ctx.Current);
                }
                ctx.Aggregates = null;
            }
            else
            {
                foreach (var t in tuples)
                {
                    ctx.Current = t;
                    result.Rows.Add(columns.Select(c => rt.Eval(c.Expr)).ToArray());
                    repTuples.Add(t);
                }
                if (q.Having != null)
                {
                    var keep = new List<int>();
                    for (int i = 0; i < result.Rows.Count; i++)
                    {
                        ctx.Current = repTuples[i];
                        ctx.Columns = columns.Select((c, k) => (c.Name, result.Rows[i][k])).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(x => x.Key, x => x.First().Item2, StringComparer.OrdinalIgnoreCase);
                        if (IsTrue(rt.Eval(q.Having))) keep.Add(i);
                    }
                    ctx.Columns = null;
                    var rows = keep.Select(i => result.Rows[i]).ToList();
                    var reps = keep.Select(i => repTuples[i]).ToList();
                    result.Rows.Clear(); result.Rows.AddRange(rows);
                    repTuples = reps;
                }
            }

            result.Fields.AddRange(InferFields(columns, result.Rows));

            // 4. DISTINCT
            if (q.Distinct) Distinct(result, repTuples);

            // 5. UNION
            foreach (var (uq, all) in q.Unions)
            {
                var other = ExecuteCore(rt, uq);
                if (other.Fields.Count != result.Fields.Count) throw new VfpException(1851, "SQL: UNION clause has incompatible column counts.");
                result.Rows.AddRange(other.Rows);
                repTuples.AddRange(other.Rows.Select(_ => (Value[]?[])new Value[]?[sources.Count]));
                for (int i = 0; i < result.Fields.Count; i++)
                    if (result.Fields[i].Type is 'C' or 'V' && other.Fields[i].Width > result.Fields[i].Width)
                        result.Fields[i] = result.Fields[i] with { Width = other.Fields[i].Width };
                if (!all) Distinct(result, repTuples);
            }

            // 6. ORDER BY
            if (q.OrderBy.Count > 0) Order(rt, ctx, q, columns, result, repTuples);

            // 7. TOP
            if (q.Top != null)
            {
                var n = rt.Eval(q.Top).AsNumber;
                var take = q.TopPercent ? (int)Math.Ceiling(result.Rows.Count * n / 100.0) : (int)n;
                if (take < result.Rows.Count) result.Rows.RemoveRange(take, result.Rows.Count - take);
            }
            return result;
        }
        finally
        {
            rt.SqlContext = saved;
        }
    }

    private static bool IsTrue(Value v) => v.Kind == ValueKind.Logical && v.AsBool;

    // ---- Sources -----------------------------------------------------------------------------

    /// <summary>The hidden column holding record numbers for UPDATE/DELETE … FROM targets.</summary>
    private const string RecnoColumn = "__RECNO";

    private static string SourceAlias(Interpreter rt, SqlSource src)
    {
        if (src.Alias != null) return src.Alias;
        var name = rt.NameValue(src.Table!);
        var bang = name.IndexOf('!');
        return Path.GetFileNameWithoutExtension(bang >= 0 ? name[(bang + 1)..] : name);
    }

    private static SqlSourceBinding LoadSource(Interpreter rt, SqlSource src, string? recnoAlias = null)
    {
        if (src.Derived != null)
        {
            var r = ExecuteCore(rt, src.Derived);
            var b = new SqlSourceBinding { Alias = src.Alias!, Fields = r.Fields, Rows = r.Rows };
            b.BuildIndex();
            return b;
        }
        var name = rt.NameValue(src.Table!);
        var bang = name.IndexOf('!');
        var aliasName = src.Alias ?? Path.GetFileNameWithoutExtension(bang >= 0 ? name[(bang + 1)..] : name);
        var table = SourceArea(rt, name).Table;
        var withRecno = recnoAlias != null && aliasName.Equals(recnoAlias, StringComparison.OrdinalIgnoreCase);
        var binding = new SqlSourceBinding { Alias = aliasName, Fields = withRecno ? [.. table.Fields, new FieldDef(RecnoColumn, 'I')] : table.Fields.ToList() };
        foreach (var row in table.Scan(null, forward: true, skipDeleted: rt.Options.Deleted))
            binding.Rows.Add(withRecno ? [.. row.Values, Value.Number(row.RecNo)] : row.Values);
        binding.BuildIndex();
        return binding;
    }

    /// <summary>The work area holding a FROM table: an open alias, else the table (or view) opened in a free area.</summary>
    private static WorkArea SourceArea(Interpreter rt, string name)
    {
        var bang = name.IndexOf('!');
        var session = rt.Session;
        var wa = session.FindAlias(Path.GetFileNameWithoutExtension(bang >= 0 ? name[(bang + 1)..] : name));
        if (wa != null) return wa;
        var area = session.FreeArea(except: session.CurrentAreaNumber); // the current area may be about to receive the result
        var current = session.CurrentAreaNumber;
        try
        {
            if (Path.GetExtension(name).Equals(".dbf", StringComparison.OrdinalIgnoreCase))
            {
                rt.ExecuteCommand($"USE \"{name}\" IN {area}");
                return session.Area(area);
            }
            if (session.FindView(name) is { } v) return Views.Open(rt, v.Database, v.View, area, null, noData: false);
            return session.Use(name, area, again: true);
        }
        finally { session.Select(current); }
    }

    /// <summary>
    /// For views: the output column names of a query and the base table and field behind each one
    /// (null for computed columns). Table names are given as written in the FROM clause.
    /// </summary>
    public static List<(string Name, string? Table, string? Field)> ColumnSources(Interpreter rt, SqlSelect q)
    {
        var bindings = new List<SqlSourceBinding>();
        var tables = new List<string?>();
        foreach (var src in q.From.Concat(q.Joins.Select(j => j.Source)))
        {
            if (src.Derived != null)
            {
                bindings.Add(new SqlSourceBinding { Alias = src.Alias!, Fields = ExecuteCore(rt, src.Derived).Fields });
                tables.Add(null);
                continue;
            }
            var name = rt.NameValue(src.Table!);
            var bang = name.IndexOf('!');
            var bare = Path.GetFileNameWithoutExtension(bang >= 0 ? name[(bang + 1)..] : name);
            var table = SourceArea(rt, name).Table;
            var b = new SqlSourceBinding { Alias = src.Alias ?? bare, Fields = table.Fields.ToList() };
            b.BuildIndex();
            bindings.Add(b);
            tables.Add(Path.HasExtension(name) && bang < 0 ? name : (bang >= 0 ? name[..bang] + "!" + bare : bare));
        }
        var result = new List<(string, string?, string?)>();
        foreach (var col in ExpandColumns(q, bindings))
        {
            var i = col.Source == null ? -1 : bindings.FindIndex(b => b.Fields.Any(f => ReferenceEquals(f, col.Source)));
            result.Add((col.Name, i >= 0 ? tables[i] : null, i >= 0 ? col.Source!.Name : null));
        }
        return result;
    }

    private static List<Value[]?[]> Join(Interpreter rt, SqlRowContext ctx, List<Value[]?[]> left, int rightIndex, SqlSourceBinding right, SqlJoin j)
    {
        var result = new List<Value[]?[]>();
        var rightMatched = new bool[right.Rows.Count];
        var hash = j.On != null ? BuildHash(rt, ctx, j.On, rightIndex, right) : null;
        foreach (var t in left)
        {
            bool matched = false;
            IEnumerable<int> candidates;
            if (hash != null)
            {
                ctx.Current = t;
                var key = HashKey(rt.Eval(hash.Value.LeftExpr));
                candidates = key != null && hash.Value.Table.TryGetValue(key, out var list) ? list : [];
            }
            else candidates = Enumerable.Range(0, right.Rows.Count);
            foreach (var r in candidates)
            {
                var n = (Value[]?[])t.Clone();
                n[rightIndex] = right.Rows[r];
                ctx.Current = n;
                if (j.On == null || IsTrue(rt.Eval(j.On)))
                {
                    result.Add(n);
                    matched = true;
                    rightMatched[r] = true;
                }
            }
            if (!matched && j.Kind is "LEFT" or "FULL")
            {
                var n = (Value[]?[])t.Clone();
                n[rightIndex] = null;
                result.Add(n);
            }
        }
        if (j.Kind is "RIGHT" or "FULL")
        {
            for (int r = 0; r < right.Rows.Count; r++)
            {
                if (rightMatched[r]) continue;
                var n = new Value[]?[ctx.Sources.Count];
                n[rightIndex] = right.Rows[r];
                result.Add(n);
            }
        }
        return result;
    }

    /// <summary>Builds a hash table on the right source for an equality conjunct "leftExpr = rightExpr" if one is safe to hash.</summary>
    private static (Expr LeftExpr, Dictionary<string, List<int>> Table)? BuildHash(Interpreter rt, SqlRowContext ctx, Expr on, int rightIndex, SqlSourceBinding right)
    {
        foreach (var c in Conjuncts(on))
        {
            if (c is not BinaryExpr { Op: "=" or "==" } eq) continue;
            var lr = Refs(eq.Left, ctx.Sources);
            var rr = Refs(eq.Right, ctx.Sources);
            if (lr == null || rr == null) continue;
            Expr leftSide, rightSide;
            if (rr.Count == 1 && rr.Contains(rightIndex) && lr.All(x => x < rightIndex) && lr.Count > 0) (leftSide, rightSide) = (eq.Left, eq.Right);
            else if (lr.Count == 1 && lr.Contains(rightIndex) && rr.All(x => x < rightIndex) && rr.Count > 0) (leftSide, rightSide) = (eq.Right, eq.Left);
            else continue;
            // Character keys hash safely only when comparison is exact: SET ANSI ON, "==", or two fixed-width fields of equal width.
            if (!rt.Options.Ansi && eq.Op == "=" && !SameWidthFields(leftSide, rightSide, ctx.Sources, out var nonChar) && !nonChar) continue;
            var table = new Dictionary<string, List<int>>();
            var probe = new Value[]?[ctx.Sources.Count];
            for (int i = 0; i < right.Rows.Count; i++)
            {
                probe[rightIndex] = right.Rows[i];
                ctx.Current = probe;
                var key = HashKey(rt.Eval(rightSide));
                if (key == null) continue;
                if (!table.TryGetValue(key, out var list)) table[key] = list = new List<int>();
                list.Add(i);
            }
            return (leftSide, table);
        }
        return null;
    }

    private static bool SameWidthFields(Expr a, Expr b, List<SqlSourceBinding> sources, out bool nonCharacter)
    {
        var fa = FieldOf(a, sources);
        var fb = FieldOf(b, sources);
        nonCharacter = fa != null && fb != null && fa.Type is not ('C' or 'V' or 'M') && fb.Type is not ('C' or 'V' or 'M');
        return fa != null && fb != null && fa.Type == 'C' && fb.Type == 'C' && fa.Width == fb.Width;
    }

    private static FieldDef? FieldOf(Expr e, List<SqlSourceBinding> sources) => e switch
    {
        NameExpr n => sources.Select(s => s.Index.TryGetValue(n.Name, out var i) ? s.Fields[i] : null).FirstOrDefault(f => f != null),
        MemberExpr { Target: NameExpr a } m => sources.FirstOrDefault(s => s.Alias.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) is { } sb && sb.Index.TryGetValue(m.Name, out var mi) ? sb.Fields[mi] : null,
        _ => null,
    };

    private static string? HashKey(Value v) => v.Kind switch
    {
        ValueKind.Null => null,
        ValueKind.Character => "C" + v.AsString.TrimEnd(' '),
        ValueKind.Number or ValueKind.Currency => "N" + v.AsNumber.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        ValueKind.Date => "D" + v.JulianDay,
        ValueKind.DateTime => "T" + v.JulianMs,
        ValueKind.Logical => v.AsBool ? "LT" : "LF",
        ValueKind.Binary => "B" + Convert.ToBase64String(v.AsBinary),
        _ => null,
    };

    private static List<Expr> Conjuncts(Expr e) =>
        e is BinaryExpr { Op: "AND" } b ? [.. Conjuncts(b.Left), .. Conjuncts(b.Right)] : [e];

    private static Expr? AndAll(List<Expr> parts) =>
        parts.Count == 0 ? null : parts.Skip(1).Aggregate(parts[0], (a, b) => new BinaryExpr("AND", a, b));

    /// <summary>The sources an expression reads, or null if that cannot be determined (subqueries, macros).</summary>
    private static HashSet<int>? Refs(Expr e, List<SqlSourceBinding> sources)
    {
        var set = new HashSet<int>();
        bool ok = true;
        void Walk(Expr x)
        {
            switch (x)
            {
                case NameExpr n:
                    for (int i = 0; i < sources.Count; i++) if (sources[i].Index.ContainsKey(n.Name)) { set.Add(i); break; }
                    break;
                case MemberExpr { Target: NameExpr a } m:
                {
                    var i = sources.FindIndex(s => s.Alias.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) set.Add(i); else ok = false;
                    break;
                }
                case AliasFieldExpr af:
                {
                    var i = sources.FindIndex(s => s.Alias.Equals(af.Alias, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) set.Add(i); else ok = false;
                    break;
                }
                case LiteralExpr or MemVarExpr or ViewParamExpr:
                    break;
                case BinaryExpr b: Walk(b.Left); Walk(b.Right); break;
                case UnaryExpr u: Walk(u.Operand); break;
                case CallExpr c:
                    if (JoePro.Runtime.Builtins.Library.IsAggregate(c.Name)) { ok = false; break; }
                    foreach (var arg in c.Args) Walk(arg);
                    break;
                case BetweenExpr bt: Walk(bt.Value); Walk(bt.Low); Walk(bt.High); break;
                case InListExpr il when il.Items.All(i => i is not SubqueryExpr): Walk(il.Value); foreach (var i in il.Items) Walk(i); break;
                case LikeExpr lk: Walk(lk.Value); Walk(lk.Pattern); break;
                case IsNullExpr isn: Walk(isn.Value); break;
                case EmptyArgExpr: break;
                default: ok = false; break;
            }
        }
        Walk(e);
        return ok ? set : null;
    }

    // ---- Columns -------------------------------------------------------------------------------

    private sealed record OutColumn(Expr Expr, string Name, FieldDef? Source);

    private static List<OutColumn> ExpandColumns(SqlSelect q, List<SqlSourceBinding> sources)
    {
        var cols = new List<OutColumn>();
        int expN = 0;
        foreach (var c in q.Columns)
        {
            if (c.Star)
            {
                foreach (var s in sources)
                {
                    if (c.StarAlias != null && !s.Alias.Equals(c.StarAlias, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var f in s.Fields)
                        cols.Add(new OutColumn(new MemberExpr(new NameExpr(s.Alias), f.Name), f.Name, f));
                }
                continue;
            }
            FieldDef? src = null;
            string name;
            switch (c.Expr)
            {
                case NameExpr n:
                    src = sources.Select(s => s.Index.TryGetValue(n.Name, out var i) ? s.Fields[i] : null).FirstOrDefault(f => f != null);
                    name = src?.Name ?? n.Name;
                    break;
                case MemberExpr { Target: NameExpr a } m:
                    src = sources.FirstOrDefault(s => s.Alias.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) is { } sb && sb.Index.TryGetValue(m.Name, out var mi) ? sb.Fields[mi] : null;
                    name = m.Name;
                    break;
                case CallExpr call when Builtins.Library.IsAggregate(call.Name):
                {
                    var fn = call.Name.ToUpperInvariant().Replace("_DISTINCT", "");
                    var arg = call.Args.Count == 0 || call.Args[0] is LiteralExpr { Value.Kind: ValueKind.Character } ? null : call.Args[0];
                    var argName = arg switch { NameExpr an => an.Name, MemberExpr am => am.Name, _ => null };
                    name = fn switch
                    {
                        "COUNT" or "CNT" when arg == null => "CNT",
                        "COUNT" or "CNT" => "CNT_" + (argName ?? "EXP"),
                        "AVG" or "AVERAGE" => "AVG_" + (argName ?? "EXP"),
                        _ => fn + "_" + (argName ?? "EXP"),
                    };
                    if (argName == null && arg != null) name = $"EXP_{++expN}";
                    break;
                }
                default:
                    name = $"EXP_{++expN}";
                    break;
            }
            if (c.Alias != null) name = c.Alias;
            cols.Add(new OutColumn(c.Expr, name.ToUpperInvariant(), c.Alias == null ? src : src));
        }
        // Duplicate names get _A, _B, … suffixes, as in VFP.
        var dupes = cols.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dupes.Count > 0)
        {
            var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cols.Count; i++)
            {
                if (!dupes.Contains(cols[i].Name)) continue;
                counters.TryGetValue(cols[i].Name, out var k);
                counters[cols[i].Name] = k + 1;
                cols[i] = cols[i] with { Name = cols[i].Name + "_" + (char)('A' + k) };
            }
        }
        return cols;
    }

    private static Expr? ResolveColumnRef(Expr e, List<OutColumn> columns)
    {
        if (e is LiteralExpr { Value.Kind: ValueKind.Number } l)
        {
            var i = (int)l.Value.AsNumber;
            if (i < 1 || i > columns.Count) throw new VfpException(1807, "SQL: GROUP BY/ORDER BY position is invalid.");
            return columns[i - 1].Expr;
        }
        if (e is NameExpr n)
        {
            var c = columns.FirstOrDefault(c => c.Name.Equals(n.Name, StringComparison.OrdinalIgnoreCase) && c.Expr is not NameExpr);
            if (c != null) return c.Expr;
        }
        return null;
    }

    private static bool HasAggregate(Expr e) => e switch
    {
        CallExpr c => Builtins.Library.IsAggregate(c.Name) || c.Args.Any(HasAggregate),
        BinaryExpr b => HasAggregate(b.Left) || HasAggregate(b.Right),
        UnaryExpr u => HasAggregate(u.Operand),
        MethodCallExpr m => m.Args.Any(HasAggregate),
        BetweenExpr bt => HasAggregate(bt.Value) || HasAggregate(bt.Low) || HasAggregate(bt.High),
        InListExpr il => HasAggregate(il.Value) || il.Items.Any(HasAggregate),
        _ => false,
    };

    private static List<FieldDef> InferFields(List<OutColumn> columns, List<Value[]> rows)
    {
        var list = new List<FieldDef>();
        for (int i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            if (c.Source != null)
            {
                list.Add(c.Source with { Name = c.Name, AutoIncNext = null, DefaultExpr = null, RuleExpr = null, RuleText = null, Nullable = c.Source.Nullable || rows.Any(r => r[i].IsNull) });
                continue;
            }
            var values = rows.Select(r => r[i]).ToList();
            var sample = values.FirstOrDefault(v => !v.IsNull);
            bool nullable = values.Any(v => v.IsNull);
            FieldDef f = sample.Kind switch
            {
                ValueKind.Character when values.Where(v => !v.IsNull).Max(v => v.AsString.Length) > 254 => new FieldDef(c.Name, 'M'),
                ValueKind.Character => new FieldDef(c.Name, 'C', Math.Max(1, values.Where(v => !v.IsNull).Max(v => v.AsString.Length))),
                ValueKind.Number => NumericField(c.Name, values),
                ValueKind.Currency => new FieldDef(c.Name, 'Y'),
                ValueKind.Date => new FieldDef(c.Name, 'D'),
                ValueKind.DateTime => new FieldDef(c.Name, 'T'),
                ValueKind.Logical => new FieldDef(c.Name, 'L'),
                ValueKind.Binary => new FieldDef(c.Name, 'W'),
                _ => new FieldDef(c.Name, 'C', 1),
            };
            list.Add(f with { Nullable = nullable });
        }
        return list;
    }

    private static FieldDef NumericField(string name, List<Value> values)
    {
        var nums = values.Where(v => v.Kind == ValueKind.Number).ToList();
        var dec = Math.Min(nums.Max(v => v.Decimals), 18);
        var width = nums.Max(v => Formatter.FormatNumber(v.AsNumber, dec, SetOptions.Default).Length) + 1;
        width = Math.Clamp(Math.Max(width, dec > 0 ? dec + 3 : 10), 1, 20);
        return new FieldDef(name, 'N', width, Math.Min(dec, Math.Max(0, width - 2)));
    }

    private static string RowKey(IEnumerable<Value> values)
    {
        var sb = new StringBuilder();
        foreach (var v in values)
        {
            sb.Append((char)('A' + (int)v.Kind)).Append(':');
            sb.Append(v.Kind switch
            {
                ValueKind.Character => v.AsString.TrimEnd(),
                ValueKind.Null => "",
                ValueKind.Binary => Convert.ToBase64String(v.AsBinary),
                ValueKind.Number => v.AsNumber.ToString("R"),
                ValueKind.Currency => v.AsCurrency.ToString(),
                ValueKind.Date => v.JulianDay.ToString(),
                ValueKind.DateTime => v.JulianMs.ToString(),
                ValueKind.Logical => v.AsBool ? "T" : "F",
                ValueKind.Object => ((VfpObject)v.AsObject).Id.ToString(),
                _ => "",
            }).Append('\u0001');
        }
        return sb.ToString();
    }

    private static void Distinct(SqlResult result, List<Value[]?[]> reps)
    {
        var seen = new HashSet<string>();
        var rows = new List<Value[]>();
        var keptReps = new List<Value[]?[]>();
        for (int i = 0; i < result.Rows.Count; i++)
        {
            if (!seen.Add(RowKey(result.Rows[i]))) continue;
            rows.Add(result.Rows[i]);
            keptReps.Add(i < reps.Count ? reps[i] : []);
        }
        result.Rows.Clear();
        result.Rows.AddRange(rows);
        reps.Clear();
        reps.AddRange(keptReps);
    }

    private static void Order(Interpreter rt, SqlRowContext ctx, SqlSelect q, List<OutColumn> columns, SqlResult result, List<Value[]?[]> reps)
    {
        var keys = new List<Func<int, Value>>();
        foreach (var o in q.OrderBy)
        {
            int col = -1;
            if (o.Position is { } p)
            {
                if (p < 1 || p > columns.Count) throw new VfpException(1807, "SQL: ORDER BY position is invalid.");
                col = p - 1;
            }
            else if (o.Expr is NameExpr n) col = columns.FindIndex(c => c.Name.Equals(n.Name, StringComparison.OrdinalIgnoreCase));
            else if (o.Expr is MemberExpr me) col = columns.FindIndex(c => c.Expr is MemberExpr cm && ExprPrinter.Print(cm).Equals(ExprPrinter.Print(me), StringComparison.OrdinalIgnoreCase));
            if (col < 0)
            {
                var printed = ExprPrinter.Print(o.Expr);
                col = columns.FindIndex(c => ExprPrinter.Print(c.Expr).Equals(printed, StringComparison.OrdinalIgnoreCase));
            }
            if (col >= 0)
            {
                var c = col;
                keys.Add(i => result.Rows[i][c]);
            }
            else
            {
                var expr = o.Expr;
                keys.Add(i =>
                {
                    ctx.Current = i < reps.Count ? reps[i] : new Value[]?[ctx.Sources.Count];
                    return rt.Eval(expr);
                });
            }
        }
        var indices = Enumerable.Range(0, result.Rows.Count).ToList();
        var cache = indices.Select(i => keys.Select(k => k(i)).ToArray()).ToList();
        indices.Sort((a, b) =>
        {
            for (int k = 0; k < keys.Count; k++)
            {
                var va = cache[a][k];
                var vb = cache[b][k];
                int c;
                if (va.IsNull || vb.IsNull) c = va.IsNull == vb.IsNull ? 0 : va.IsNull ? -1 : 1;
                else c = VfpCompare.Compare(va, vb, StringCompareMode.Padded);
                if (c != 0) return q.OrderBy[k].Desc ? -c : c;
            }
            return a.CompareTo(b);
        });
        var rows = indices.Select(i => result.Rows[i]).ToList();
        var newReps = indices.Select(i => i < reps.Count ? reps[i] : []).ToList();
        result.Rows.Clear();
        result.Rows.AddRange(rows);
        reps.Clear();
        reps.AddRange(newReps);
    }

    // ---- Delivery (INTO) ---------------------------------------------------------------------

    private static void Deliver(Interpreter rt, SqlSelect q, SqlResult result)
    {
        var session = rt.Session;
        switch (q.IntoKind)
        {
            case "ARRAY":
            {
                if (result.Rows.Count == 0) return; // VFP leaves the array untouched when no rows are selected
                var v = rt.FindVariable(q.IntoName!) ?? DeclareArray(rt, q.IntoName!);
                v.Array = new VfpArray(result.Rows.Count, result.Fields.Count);
                for (int r = 0; r < result.Rows.Count; r++)
                    for (int c = 0; c < result.Fields.Count; c++)
                        v.Array[r + 1, c + 1] = result.Rows[r][c];
                return;
            }
            case "SCREEN":
            {
                var widths = result.Fields.Select((f, i) => Math.Max(f.Name.Length, result.Rows.Count == 0 ? 0 : result.Rows.Max(r => Formatter.ToDisplay(r[i], rt.Options).Trim().Length))).ToList();
                rt.WriteLine(string.Join(" ", result.Fields.Select((f, i) => f.Name.PadRight(widths[i]))).TrimEnd());
                foreach (var r in result.Rows)
                    rt.WriteLine(string.Join(" ", r.Select((v, i) => Formatter.ToDisplay(v, rt.Options).Trim().PadRight(widths[i]))).TrimEnd());
                return;
            }
            case "TABLE":
            {
                var path = session.ResolvePath(q.IntoName!, Store.FreeTableExtension);
                var existing = session.FindAlias(Path.GetFileNameWithoutExtension(path));
                existing?.Close();
                if (File.Exists(path)) File.Delete(path);
                var schema = new TableSchema(Path.GetFileNameWithoutExtension(path), result.Fields.Select(f => f with { Nullable = true }));
                var t = session.CreateTable(schema, free: true, path);
                t.Store.Batch(() => { foreach (var r in result.Rows) t.Append(r); });
                var area = existing?.Number ?? (session.Current.InUse ? session.FreeArea() : session.CurrentAreaNumber);
                session.Use(path, area);
                session.Select(area);
                return;
            }
            default:
            {
                var name = q.IntoKind == "CURSOR" ? q.IntoName! : "Query";
                var fields = result.Fields.Count > 0 ? result.Fields : [new FieldDef("EXP_1", 'L')];
                var schema = new TableSchema(name, fields.Select(f => f with { Nullable = true }));
                var existing = session.FindAlias(name);
                var area = existing?.Number ?? (session.Current.InUse ? session.FreeArea() : session.CurrentAreaNumber);
                session.Select(area);
                var wa = session.CreateCursor(schema, area);
                foreach (var r in result.Rows) wa.Table.Append(r);
                wa.GoTop();
                wa.ReadOnly = q.IntoKind == "CURSOR" && !q.ReadWrite;
                return;
            }
        }
    }

    private static Variable DeclareArray(Interpreter rt, string name)
    {
        rt.SetVariable(name, Value.False);
        return rt.FindVariable(name)!;
    }

    public static Value EvalInList(Interpreter rt, InListExpr il)
    {
        var v = rt.Eval(il.Value);
        if (v.IsNull) return Value.Null;
        IEnumerable<Value> items = il.Items.Count == 1 && il.Items[0] is SubqueryExpr sq
            ? Execute(rt, sq.Query, materialize: false).Rows.Select(r => r[0])
            : il.Items.Select(rt.Eval);
        var mode = rt.CompareMode();
        foreach (var item in items)
        {
            if (item.IsNull) continue;
            if (VfpCompare.Compare(v, item, mode == StringCompareMode.RightLength ? StringCompareMode.Padded : mode) == 0)
                return Value.Logical(!il.Not);
        }
        return Value.Logical(il.Not);
    }

    // ---- INSERT / UPDATE / DELETE -----------------------------------------------------------------

    private static WorkArea TargetArea(Interpreter rt, Expr tableExpr)
    {
        var name = rt.NameValue(tableExpr);
        var alias = Path.GetFileNameWithoutExtension(name.Contains('!') ? name[(name.IndexOf('!') + 1)..] : name);
        var wa = rt.Session.FindAlias(alias);
        if (wa != null) return wa;
        var current = rt.Session.CurrentAreaNumber;
        wa = rt.Session.Use(name, rt.Session.FreeArea());
        rt.Session.Select(current);
        return wa;
    }

    public static void Insert(Interpreter rt, SqlInsertStmt s)
    {
        if (s.Select != null)
        {
            var target = TargetArea(rt, s.Table);
            var r = ExecuteCore(rt, s.Select);
            var cols = s.Columns ?? target.Table.Fields.Take(r.Fields.Count).Select(f => f.Name).ToList();
            target.Table.Store.Batch(() => { foreach (var row in r.Rows) InsertRow(rt, target, cols, row); });
            rt.SetPublic("_TALLY", Value.Number(r.Rows.Count, 0));
            return;
        }
        var wa = TargetArea(rt, s.Table);
        var fields = wa.Table.Fields;
        List<string> columns;
        Value[] values;
        if (s.Values != null)
        {
            columns = s.Columns ?? fields.Select(f => f.Name).Take(s.Values.Count).ToList();
            if (columns.Count != s.Values.Count) throw new VfpException(1230, "SQL: column count does not match value count.");
            values = s.Values.Select(rt.Eval).ToArray();
        }
        else if (s.FromMemvar != null)
        {
            columns = fields.Select(f => f.Name).Where(n => rt.FindVariable(n) is { IsArray: false }).ToList();
            values = columns.Select(rt.GetVariable).ToArray();
        }
        else if (s.FromName != null)
        {
            var o = (VfpObject)rt.GetVariable(s.FromName).AsObject;
            columns = fields.Select(f => f.Name).Where(n => o.FindProperty(n) != null).ToList();
            values = columns.Select(n => o.Get(n)).ToArray();
        }
        else
        {
            var arr = rt.FindVariable(s.FromArray!)?.Array ?? throw VfpException.VariableNotFound(s.FromArray!);
            columns = fields.Select(f => f.Name).Take(arr.TwoDimensional ? arr.Cols : arr.Length).ToList();
            values = columns.Select((_, i) => arr[i + 1]).ToArray();
        }
        InsertRow(rt, wa, columns, values);
        rt.SetPublic("_TALLY", Value.Number(1, 0));
    }

    private static void InsertRow(Interpreter rt, WorkArea wa, List<string> columns, Value[] values)
    {
        var fields = wa.Table.Fields;
        var vals = new Value[fields.Count];
        var given = new bool[fields.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            var i = wa.FieldIndex(columns[c]);
            if (i < 0) throw VfpException.FieldNotFound(columns[c]);
            vals[i] = values[c].IsNull ? (fields[i].Nullable || rt.Options.Null ? Value.Null : throw new VfpException(ErrorCodes.NullNotAllowed, $"Field {fields[i].Name} does not accept null values.")) : fields[i].Coerce(values[c]);
            given[i] = true;
        }
        for (int i = 0; i < fields.Count; i++)
        {
            if (given[i]) continue;
            vals[i] = string.IsNullOrWhiteSpace(fields[i].DefaultExpr) ? fields[i].BlankValue() : rt.EvalInArea(wa, fields[i].DefaultExpr!);
        }
        rt.AppendWithDefaults(wa, vals);
        foreach (var i in Enumerable.Range(0, fields.Count).Where(i => given[i] && !string.IsNullOrWhiteSpace(fields[i].RuleExpr)))
        {
            if (!Interpreter.Truthy(rt.EvalInArea(wa, fields[i].RuleExpr!)))
            {
                wa.Delete();
                throw new VfpException(ErrorCodes.FieldRuleViolated, fields[i].RuleText ?? $"Field {fields[i].Name} validation rule is violated.");
            }
        }
    }

    /// <summary>
    /// UPDATE/DELETE … FROM: joins the target with the other sources and returns, for each target record that
    /// matches, its record number and the values of <paramref name="columns"/>. The first match wins.
    /// </summary>
    private static (WorkArea Target, List<(int RecNo, Value[] Values)> Rows) TargetMatches(Interpreter rt, Expr targetExpr,
        List<SqlSource> from, List<SqlJoin> joins, Expr? where, List<Expr> columns)
    {
        var targetName = rt.NameValue(targetExpr);
        var bare = Path.GetFileNameWithoutExtension(targetName.Contains('!') ? targetName[(targetName.IndexOf('!') + 1)..] : targetName);
        // The target may be one of the FROM sources (by alias or table name); otherwise it is joined in first.
        var sources = new List<SqlSource>(from);
        var targetSource = sources.Concat(joins.Select(j => j.Source))
            .FirstOrDefault(src => src.Derived == null && (SourceAlias(rt, src).Equals(bare, StringComparison.OrdinalIgnoreCase)
                || (src.Alias == null && rt.NameValue(src.Table!).Equals(targetName, StringComparison.OrdinalIgnoreCase))));
        if (targetSource == null)
        {
            targetSource = new SqlSource(targetExpr, bare, null);
            sources.Insert(0, targetSource);
        }
        var alias = SourceAlias(rt, targetSource);
        var cols = new List<SqlColumn> { new(new MemberExpr(new NameExpr(alias), RecnoColumn), RecnoColumn, false, null) };
        cols.AddRange(columns.Select((c, i) => new SqlColumn(c, $"V{i}", false, null)));
        var q = new SqlSelect(false, null, false, cols, sources, joins, where, [], null, [], [], null, null, false, false);
        var result = ExecuteCore(rt, q, alias);
        var seen = new HashSet<int>();
        var rows = new List<(int, Value[])>();
        foreach (var r in result.Rows)
        {
            var recno = (int)r[0].AsNumber;
            if (seen.Add(recno)) rows.Add((recno, r[1..])); // TODO(oracle): which match VFP uses when several rows join
        }
        var wa = TargetArea(rt, targetSource.Table!);
        return (wa, rows);
    }

    public static void Update(Interpreter rt, SqlUpdateStmt s)
    {
        if (s.From != null)
        {
            var (target, matches) = TargetMatches(rt, s.Table, s.From, s.Joins ?? [], s.Where, s.Sets.Select(x => x.Value).ToList());
            var indexes = s.Sets.Select(x => target.FieldIndex(x.Column) is var i and >= 0 ? i : throw VfpException.FieldNotFound(x.Column)).ToList();
            var savedArea = rt.Session.CurrentAreaNumber;
            rt.Session.Select(target.Number);
            try
            {
                target.Table.Store.Batch(() =>
                {
                    foreach (var (recno, values) in matches)
                    {
                        target.Go(recno);
                        rt.ReplaceWithRules(target, indexes.Select((fi, k) => (fi, values[k])).ToList());
                    }
                });
            }
            finally { rt.Session.Select(savedArea); }
            rt.SetPublic("_TALLY", Value.Number(matches.Count, 0));
            return;
        }
        var wa = TargetArea(rt, s.Table);
        var saved = rt.Session.CurrentAreaNumber;
        rt.Session.Select(wa.Number);
        int count = 0;
        try
        {
            var targets = s.Sets.Select(x => (Index: wa.FieldIndex(x.Column), x.Value)).ToList();
            foreach (var (idx, _) in targets) if (idx < 0) throw VfpException.FieldNotFound("?");
            var recs = new List<int>();
            wa.GoTop();
            while (!wa.Eof)
            {
                if (s.Where == null || IsTrue(rt.Eval(s.Where))) recs.Add(wa.RecNo);
                wa.Skip();
            }
            wa.Table.Store.Batch(() =>
            {
                foreach (var r in recs)
                {
                    wa.Go(r);
                    var assignments = targets.Select(t => (t.Index, rt.Eval(t.Value))).ToList();
                    rt.ReplaceWithRules(wa, assignments);
                    count++;
                }
            });
        }
        finally
        {
            rt.Session.Select(saved);
        }
        rt.SetPublic("_TALLY", Value.Number(count, 0));
    }

    public static void Delete(Interpreter rt, SqlDeleteStmt s)
    {
        if (s.From != null)
        {
            var (target, matches) = TargetMatches(rt, s.Table, s.From, s.Joins ?? [], s.Where, []);
            var savedArea = rt.Session.CurrentAreaNumber;
            rt.Session.Select(target.Number);
            int deleted = 0;
            try
            {
                target.Table.Store.Batch(() =>
                {
                    foreach (var (recno, _) in matches)
                    {
                        target.Go(recno);
                        if (!target.Deleted) { target.Delete(); deleted++; }
                    }
                });
            }
            finally { rt.Session.Select(savedArea); }
            rt.SetPublic("_TALLY", Value.Number(deleted, 0));
            return;
        }
        var wa = TargetArea(rt, s.Table);
        var saved = rt.Session.CurrentAreaNumber;
        rt.Session.Select(wa.Number);
        int count = 0;
        try
        {
            var recs = new List<int>();
            wa.GoTop();
            while (!wa.Eof)
            {
                if (!wa.Deleted && (s.Where == null || IsTrue(rt.Eval(s.Where)))) recs.Add(wa.RecNo);
                wa.Skip();
            }
            wa.Table.Store.Batch(() =>
            {
                foreach (var r in recs)
                {
                    wa.Go(r);
                    wa.Delete();
                    count++;
                }
            });
        }
        finally
        {
            rt.Session.Select(saved);
        }
        rt.SetPublic("_TALLY", Value.Number(count, 0));
    }
}
