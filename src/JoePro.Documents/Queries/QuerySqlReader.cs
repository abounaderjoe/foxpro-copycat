using System.Text;

namespace JoePro.Documents.Queries;

/// <summary>
/// Reads SELECT-SQL into a <see cref="QueryDocument"/>: splits the statement into clauses, lists and conditions
/// and keeps each piece's text as written, so a query built in the designer reads back identically.
/// </summary>
internal static class QuerySqlReader
{
    private enum K { Word, Number, String, Op, Open, Close, Comma }

    private sealed record Tok(K Kind, string Text, int Start, int End, int Depth)
    {
        public bool Is(string word) => Kind == K.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "DISTINCT", "ALL", "TOP", "PERCENT", "FROM", "WHERE", "GROUP", "BY", "HAVING", "ORDER", "UNION", "INTO", "TO", "AS",
        "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "JOIN", "ON", "AND", "OR", "NOT", "ASC", "DESC", "CURSOR", "TABLE", "DBF",
        "ARRAY", "READWRITE", "NOFILTER", "SCREEN", "PRINTER", "FILE", "NOCONSOLE", "PLAIN", "NOWAIT", "PREFERENCE", "WITH",
    };

    private sealed class NotRepresentable(string reason) : Exception(reason);

    // ---- Text ----------------------------------------------------------------------------------------

    /// <summary>The statements of a program text: comments dropped, ; continuation lines joined.</summary>
    public static List<string> Statements(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw);
            var trimmed = line.Trim();
            if (current.Length == 0 && (trimmed.Length == 0 || trimmed.StartsWith('*') || trimmed.StartsWith("NOTE ", StringComparison.OrdinalIgnoreCase))) continue;
            if (trimmed.EndsWith(';'))
            {
                current.Append(trimmed[..^1].TrimEnd()).Append(' ');
                continue;
            }
            current.Append(trimmed);
            if (current.ToString().Trim().Length > 0) result.Add(current.ToString().Trim());
            current.Clear();
        }
        if (current.ToString().Trim().Length > 0) result.Add(current.ToString().Trim());
        return result;
    }

    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '&' && i + 1 < line.Length && line[i + 1] == '&') return line[..i];
        }
        return line;
    }

    /// <summary>One line, single spaces outside strings, no trailing ; or continuation.</summary>
    private static string Normalize(string sql)
    {
        var joined = string.Join(" ", Statements(sql));
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (var c in joined)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim().TrimEnd(';').Trim();
    }

    private static List<Tok> Lex(string s)
    {
        var list = new List<Tok>();
        int depth = 0;
        int i = 0;
        void Add(K kind, int start, int end) => list.Add(new Tok(kind, s[start..end], start, end, depth));
        while (i < s.Length)
        {
            var c = s[i];
            if (c == ' ') { i++; continue; }
            var start = i;
            var prev = list.Count > 0 ? list[^1].Kind : K.Op;
            if (c is '"' or '\'' || (c == '[' && prev is not (K.Word or K.Close or K.Number)))
            {
                var close = c == '[' ? ']' : c;
                i++;
                while (i < s.Length && s[i] != close) i++;
                i = Math.Min(i + 1, s.Length);
                Add(K.String, start, i);
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]) && prev is not (K.Word or K.Close)))
            {
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
                Add(K.Number, start, i);
            }
            else if (char.IsLetter(c) || c == '_' || c > 127)
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] > 127)) i++;
                Add(K.Word, start, i);
            }
            else if (c == '.' && i + 2 < s.Length && char.IsLetter(s[i + 1]) && s.IndexOf('.', i + 1) is var dot and > 0 && dot - i <= 6
                     && s[(i + 1)..dot].All(char.IsLetter) && s[(i + 1)..dot].ToUpperInvariant() is "AND" or "OR" or "NOT" or "T" or "F" or "NULL" or "Y" or "N")
            {
                i = dot + 1;
                list.Add(new Tok(K.Word, s[start..i].Trim('.').ToUpperInvariant() is "AND" or "OR" or "NOT" ? s[(start + 1)..(i - 1)] : s[start..i], start, i, depth));
            }
            else if (c == '(') { Add(K.Open, i, i + 1); depth++; i++; }
            else if (c == ')') { depth = Math.Max(0, depth - 1); i++; list.Add(new Tok(K.Close, ")", start, i, depth)); }
            else if (c == ',') { Add(K.Comma, i, i + 1); i++; }
            else
            {
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                i += two is "==" or "<>" or "!=" or "<=" or ">=" or "**" ? 2 : 1;
                Add(K.Op, start, i);
            }
        }
        return list;
    }

    // ---- Reading ------------------------------------------------------------------------------------

    public static bool HasInto(string sql)
    {
        var toks = Lex(Normalize(sql));
        for (int i = 0; i < toks.Count; i++)
            if (toks[i].Depth == 0 && (toks[i].Is("INTO") || (toks[i].Is("TO") && i + 1 < toks.Count && (toks[i + 1].Is("SCREEN") || toks[i + 1].Is("PRINTER") || toks[i + 1].Is("FILE")))))
                return true;
        return false;
    }

    public static QueryDocument Read(string sql)
    {
        try { return ReadCore(Normalize(sql)); }
        catch (NotRepresentable ex) { return new QueryDocument { Sql = sql.Trim(), SqlOnlyReason = ex.Message }; }
    }

    private static QueryDocument ReadCore(string text)
    {
        var t = Lex(text);
        string Slice(int a, int b) => a >= b ? "" : text[t[a].Start..t[b - 1].End].Trim();
        if (t.Count == 0 || t[0].Kind != K.Word || t[0].Text.Length < 4 || !"SELECT".StartsWith(t[0].Text.ToUpperInvariant()))
            throw new NotRepresentable("not a SELECT statement");
        var doc = new QueryDocument();
        int i = 1;
        if (i < t.Count && t[i].Is("DISTINCT")) { doc.Distinct = true; i++; }
        else if (i < t.Count && t[i].Is("ALL")) i++;
        if (i < t.Count && t[i].Is("TOP"))
        {
            if (i + 1 >= t.Count || t[i + 1].Kind != K.Number || !int.TryParse(t[i + 1].Text, out var top)) throw new NotRepresentable("TOP needs a whole number");
            doc.Top = top;
            i += 2;
            if (i < t.Count && t[i].Is("PERCENT")) { doc.Percent = true; i++; }
        }

        // Top-level clause starts.
        int Find(string word, int from, string? next = null)
        {
            for (int k = from; k < t.Count; k++)
                if (t[k].Depth == 0 && t[k].Is(word) && (next == null || (k + 1 < t.Count && t[k + 1].Is(next)))) return k;
            return -1;
        }
        if (Find("UNION", 0) >= 0) throw new NotRepresentable("UNION");
        var from = Find("FROM", i);
        if (from < 0) throw new NotRepresentable("no FROM clause");
        var where = Find("WHERE", from);
        var group = Find("GROUP", from, "BY");
        var having = Find("HAVING", from);
        var order = Find("ORDER", from, "BY");
        var into = Find("INTO", from);
        var to = Find("TO", from);
        var starts = new[] { where, group, having, order, into, to }.Where(x => x >= 0).ToList();
        int End(int start) => starts.Where(x => x > start).DefaultIfEmpty(t.Count).Min();
        // The clauses must come in order.
        var seq = new[] { where, group, having, order, into >= 0 ? into : to }.Where(x => x >= 0).ToList();
        if (!seq.SequenceEqual(seq.OrderBy(x => x))) throw new NotRepresentable("clauses in an unusual order");

        // Output fields.
        foreach (var (a, b) in SplitCommas(t, i, from))
        {
            if (b - a == 1 && t[a].Kind == K.Op && t[a].Text == "*") continue;
            if (b - a >= 3 && t[b - 2].Is("AS") && t[b - 1].Kind == K.Word) doc.Fields.Add(new QueryField(Slice(a, b - 2), t[b - 1].Text));
            else doc.Fields.Add(new QueryField(Slice(a, b)));
        }
        if (doc.Fields.Count == 0 && End(from) == from) throw new NotRepresentable("no fields");

        ReadFrom(doc, t, from + 1, End(from), Slice);
        if (where >= 0) doc.Filters.AddRange(Conditions(t, where + 1, End(where), Slice));
        if (group >= 0) foreach (var (a, b) in SplitCommas(t, group + 2, End(group))) doc.GroupBy.Add(Slice(a, b));
        if (having >= 0) doc.Having.AddRange(Conditions(t, having + 1, End(having), Slice));
        if (order >= 0)
            foreach (var (a, b) in SplitCommas(t, order + 2, End(order)))
            {
                if (t[b - 1].Is("DESC")) doc.OrderBy.Add(new QueryOrder(Slice(a, b - 1), true));
                else if (t[b - 1].Is("ASC")) doc.OrderBy.Add(new QueryOrder(Slice(a, b - 1)));
                else doc.OrderBy.Add(new QueryOrder(Slice(a, b)));
            }
        if (into >= 0)
        {
            var e = End(into);
            if (into + 2 >= e) throw new NotRepresentable("INTO needs a destination");
            var kind = t[into + 1];
            var nameEnd = e;
            if (kind.Is("CURSOR"))
            {
                doc.Destination = QueryDestination.Cursor;
                if (t[e - 1].Is("READWRITE")) { doc.ReadWrite = true; nameEnd--; }
                else if (t[e - 1].Is("NOFILTER")) { doc.NoFilter = true; nameEnd--; }
            }
            else if (kind.Is("TABLE") || kind.Is("DBF")) doc.Destination = QueryDestination.Table;
            else if (kind.Is("ARRAY")) doc.Destination = QueryDestination.Array;
            else throw new NotRepresentable($"INTO {kind.Text}");
            doc.IntoName = Slice(into + 2, nameEnd);
            if (doc.IntoName.Length == 0 || doc.IntoName.Contains(' ')) throw new NotRepresentable("INTO options");
        }
        else if (to >= 0)
        {
            if (to + 1 < t.Count && t[to + 1].Is("SCREEN") && End(to) == to + 2) doc.Destination = QueryDestination.Screen;
            else throw new NotRepresentable("TO PRINTER, TO FILE or output options");
        }
        return doc;
    }

    private static IEnumerable<(int, int)> SplitCommas(List<Tok> t, int a, int b)
    {
        if (a >= b) yield break;
        var depth = t[a].Depth;
        var start = a;
        for (int k = a; k < b; k++)
        {
            if (t[k].Kind == K.Comma && t[k].Depth == depth)
            {
                if (k == start) throw new NotRepresentable("an empty list item");
                yield return (start, k);
                start = k + 1;
            }
        }
        if (start >= b) throw new NotRepresentable("an empty list item");
        yield return (start, b);
    }

    private static bool IsJoinWord(Tok x) => x.Is("INNER") || x.Is("LEFT") || x.Is("RIGHT") || x.Is("FULL") || x.Is("CROSS") || x.Is("JOIN");

    private static void ReadFrom(QueryDocument doc, List<Tok> t, int a, int b, Func<int, int, string> slice)
    {
        if (a >= b) throw new NotRepresentable("no tables");
        var pos = a;
        QueryTable TableRef()
        {
            if (pos >= b) throw new NotRepresentable("a missing table name");
            if (t[pos].Kind == K.Open) throw new NotRepresentable("a derived table (subquery in FROM)");
            var start = pos;
            pos++;
            while (pos < b && t[pos].Start == t[pos - 1].End && t[pos].Kind is not (K.Comma or K.Open)) pos++;
            var table = new QueryTable { Name = slice(start, pos) };
            if (pos < b && t[pos].Is("AS")) { pos++; table.Alias = pos < b && t[pos].Kind == K.Word ? t[pos++].Text : throw new NotRepresentable("AS needs an alias"); }
            else if (pos < b && t[pos].Kind == K.Word && !Keywords.Contains(t[pos].Text)) table.Alias = t[pos++].Text;
            return table;
        }
        doc.Tables.Add(TableRef());
        var open = new Stack<QueryTable>();
        while (pos < b)
        {
            var x = t[pos];
            if (x.Kind == K.Comma) { pos++; doc.Tables.Add(TableRef()); continue; }
            if (IsJoinWord(x))
            {
                JoinKind? kind = JoinKind.Inner;
                if (x.Is("LEFT")) kind = JoinKind.Left;
                else if (x.Is("RIGHT")) kind = JoinKind.Right;
                else if (x.Is("FULL")) kind = JoinKind.Full;
                else if (x.Is("CROSS")) kind = null;
                if (!x.Is("JOIN")) pos++;
                if (pos < b && t[pos].Is("OUTER")) pos++;
                if (pos >= b || !t[pos].Is("JOIN")) throw new NotRepresentable("a join without JOIN");
                pos++;
                var table = TableRef();
                table.Join = kind;
                doc.Tables.Add(table);
                if (kind != null) open.Push(table);
                continue;
            }
            if (x.Is("ON"))
            {
                pos++;
                var start = pos;
                while (pos < b && !(t[pos].Depth == x.Depth && (t[pos].Is("ON") || IsJoinWord(t[pos]) || t[pos].Kind == K.Comma))) pos++;
                if (open.Count == 0) throw new NotRepresentable("ON without a join");
                // VFP's nested style (a JOIN b JOIN c ON c… ON b…) closes the innermost join first.
                open.Pop().On = slice(start, pos);
                continue;
            }
            throw new NotRepresentable($"'{x.Text}' in FROM");
        }
        if (open.Count > 0) throw new NotRepresentable("a join without ON");
    }

    private static List<QueryCondition> Conditions(List<Tok> t, int a, int b, Func<int, int, string> slice)
    {
        if (a >= b) throw new NotRepresentable("an empty condition");
        var depth = t[a].Depth;
        var parts = new List<(int A, int B, bool Or)>();
        var start = a;
        var between = false;
        for (int k = a; k < b; k++)
        {
            if (t[k].Depth != depth) continue;
            if (t[k].Is("BETWEEN")) { between = true; continue; }
            var isAnd = t[k].Is("AND");
            var isOr = t[k].Is("OR");
            if (!isAnd && !isOr) continue;
            if (isAnd && between) { between = false; continue; }
            if (k == start) throw new NotRepresentable("an empty condition");
            parts.Add((start, k, isOr));
            start = k + 1;
            between = false;
        }
        if (start >= b) throw new NotRepresentable("an empty condition");
        parts.Add((start, b, false));
        return parts.Select(p => Condition(t, p.A, p.B, slice) with { Or = p.Or }).ToList();
    }

    private static QueryCondition Condition(List<Tok> t, int a, int b, Func<int, int, string> slice)
    {
        var not = false;
        if (t[a].Is("NOT") && b - a > 1) { not = true; a++; }
        var depth = t[a].Depth;
        for (int k = a + 1; k < b; k++)
        {
            var x = t[k];
            if (x.Depth != depth) continue;
            string? op = null;
            int opEnd = k + 1, leftEnd = k;
            if (x.Kind == K.Op && x.Text is "=" or "==" or "<>" or "!=" or "#" or "<" or ">" or "<=" or ">=")
                op = x.Text is "!=" or "#" ? "<>" : x.Text;
            else if (x.Is("LIKE") || x.Is("BETWEEN") || x.Is("IN")) op = x.Text.ToUpperInvariant();
            else if (x.Is("NOT") && k + 1 < b && (t[k + 1].Is("LIKE") || t[k + 1].Is("BETWEEN") || t[k + 1].Is("IN")))
            {
                not = !not;
                op = t[k + 1].Text.ToUpperInvariant();
                opEnd = k + 2;
            }
            else if (x.Is("IS"))
            {
                var n = k + 1;
                if (n < b && t[n].Is("NOT")) { not = !not; n++; }
                if (n < b && t[n].Is("NULL") && n + 1 == b) return new QueryCondition(slice(a, leftEnd), "IS NULL", "", not);
                continue;
            }
            if (op == null) continue;
            var left = slice(a, leftEnd);
            var right = slice(opEnd, b);
            if (left.Length == 0 || right.Length == 0) break;
            return new QueryCondition(left, op, right, not);
        }
        return new QueryCondition(slice(a, b), "", "", not);
    }
}
