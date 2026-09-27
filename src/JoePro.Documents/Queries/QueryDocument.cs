using System.Text;
using JoePro.Documents.Yaml;

namespace JoePro.Documents.Queries;

public enum JoinKind { Inner, Left, Right, Full }

/// <summary>Where a query's results go (the Query Designer's Query Destination).</summary>
public enum QueryDestination { Browse, Cursor, Table, Array, Screen }

/// <summary>A table (or view) in a query, with its join to the tables before it and its place in the diagram.</summary>
public sealed class QueryTable
{
    /// <summary>The table as the query names it: customer, sales!customer or a path.</summary>
    public string Name { get; set; } = "";
    public string? Alias { get; set; }
    /// <summary>How it joins the tables before it; null for the first table or a plain FROM list (a, b).</summary>
    public JoinKind? Join { get; set; }
    public string? On { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>The name the query's expressions use for the table: its alias, else its bare table name.</summary>
    public string Ref => Alias ?? Bare(Name);

    public static string Bare(string name)
    {
        var n = name.Trim('"', '\'');
        var bang = n.LastIndexOf('!');
        if (bang >= 0) n = n[(bang + 1)..];
        n = n.Replace('\\', '/');
        if (n.Contains('/')) n = n[(n.LastIndexOf('/') + 1)..];
        var dot = n.LastIndexOf('.');
        return dot > 0 ? n[..dot] : n;
    }
}

/// <summary>An output column: an expression and its optional AS name.</summary>
public sealed record QueryField(string Expression, string? Alias = null)
{
    public string Text => Alias != null ? $"{Expression} AS {Alias}" : Expression;
}

/// <summary>
/// A filter row (the Filter and Having tabs): left [NOT] op right, and whether the next row is joined with OR
/// (else AND). An empty <see cref="Op"/> is a condition written as one expression (EMPTY(x), (a OR b)).
/// </summary>
public sealed record QueryCondition(string Left, string Op = "", string Right = "", bool Not = false, bool Or = false)
{
    public static readonly string[] Operators = ["=", "==", "<>", "<", ">", "<=", ">=", "LIKE", "BETWEEN", "IN", "IS NULL"];

    public string Text => Op switch
    {
        "" => (Not ? "NOT " : "") + Left,
        "IS NULL" => $"{Left} IS {(Not ? "NOT " : "")}NULL",
        _ => $"{(Not ? "NOT " : "")}{Left} {Op} {Right}",
    };
}

public sealed record QueryOrder(string Expression, bool Descending = false)
{
    public string Text => Descending ? Expression + " DESC" : Expression;
}

/// <summary>
/// A query (.jpquery) as the Query Designer shows it: tables and joins, output fields, filters, grouping, order,
/// DISTINCT/TOP and the destination. It writes its SELECT-SQL and reads one back; a SELECT the designer cannot show
/// (UNION, a derived table, …) is kept as SQL (<see cref="Sql"/>) and edited as text.
/// </summary>
public sealed class QueryDocument
{
    public List<QueryTable> Tables { get; } = new();
    public List<QueryField> Fields { get; } = new();
    public List<QueryCondition> Filters { get; } = new();
    public List<string> GroupBy { get; } = new();
    public List<QueryCondition> Having { get; } = new();
    public List<QueryOrder> OrderBy { get; } = new();
    public bool Distinct { get; set; }
    public int? Top { get; set; }
    public bool Percent { get; set; }
    public QueryDestination Destination { get; set; }
    public string? IntoName { get; set; }
    public bool ReadWrite { get; set; }
    public bool NoFilter { get; set; }
    /// <summary>SQL-only mode: the SELECT as written, when the designer cannot represent it.</summary>
    public string? Sql { get; set; }
    /// <summary>Why the query is SQL-only.</summary>
    public string? SqlOnlyReason { get; set; }
    public string? Comment { get; set; }

    public bool IsSqlOnly => Sql != null;

    public static QueryDocument Load(string path) => QuerySerializer.Parse(File.ReadAllText(path));
    public void Save(string path) => File.WriteAllText(path, QuerySerializer.Write(this), new UTF8Encoding(false));

    public QueryTable? FindTable(string nameOrAlias) =>
        Tables.FirstOrDefault(t => t.Ref.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase))
        ?? Tables.FirstOrDefault(t => QueryTable.Bare(t.Name).Equals(QueryTable.Bare(nameOrAlias), StringComparison.OrdinalIgnoreCase));

    private static string Conditions(List<QueryCondition> list)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(list[i - 1].Or ? " OR " : " AND ");
            sb.Append(list[i].Text);
        }
        return sb.ToString();
    }

    /// <summary>The SELECT-SQL command, one clause per line joined with FoxPro's ; continuation.</summary>
    public string ToSql(bool withDestination = true)
    {
        if (Sql != null) return Sql;
        var lines = new List<string>();
        var head = "SELECT " + (Distinct ? "DISTINCT " : "") + (Top is { } top ? $"TOP {top}{(Percent ? " PERCENT" : "")} " : "")
                   + (Fields.Count == 0 ? "*" : string.Join(", ", Fields.Select(f => f.Text)));
        lines.Add(head);
        for (int i = 0; i < Tables.Count; i++)
        {
            var t = Tables[i];
            var name = t.Alias != null ? $"{t.Name} {t.Alias}" : t.Name;
            if (i == 0) lines.Add("FROM " + name);
            else if (t.Join is { } kind)
                lines.Add($"{JoinWords(kind)} {name}{(string.IsNullOrWhiteSpace(t.On) ? "" : " ON " + t.On)}");
            else lines[^1] += ", " + name;
        }
        if (Filters.Count > 0) lines.Add("WHERE " + Conditions(Filters));
        if (GroupBy.Count > 0) lines.Add("GROUP BY " + string.Join(", ", GroupBy));
        if (Having.Count > 0) lines.Add("HAVING " + Conditions(Having));
        if (OrderBy.Count > 0) lines.Add("ORDER BY " + string.Join(", ", OrderBy.Select(o => o.Text)));
        if (withDestination && DestinationClause() is { } into) lines.Add(into);
        return string.Join(" ;\n   ", lines);
    }

    public static string JoinWords(JoinKind k) => k switch
    {
        JoinKind.Left => "LEFT OUTER JOIN",
        JoinKind.Right => "RIGHT OUTER JOIN",
        JoinKind.Full => "FULL OUTER JOIN",
        _ => "INNER JOIN",
    };

    public string? DestinationClause() => Destination switch
    {
        QueryDestination.Cursor => $"INTO CURSOR {IntoName ?? "query"}{(ReadWrite ? " READWRITE" : NoFilter ? " NOFILTER" : "")}",
        QueryDestination.Table => $"INTO TABLE {IntoName ?? "query"}",
        QueryDestination.Array => $"INTO ARRAY {IntoName ?? "aquery"}",
        QueryDestination.Screen => "TO SCREEN",
        _ => null,
    };

    /// <summary>
    /// The code DO query.jpquery runs: the SELECT, and for the default Browse destination a cursor named Query that is
    /// then browsed, as VFP does.
    /// </summary>
    public string RunCode()
    {
        if (Destination != QueryDestination.Browse || Sql != null && QuerySqlReader.HasInto(Sql)) return ToSql();
        return ToSql(withDestination: false) + " ;\n   INTO CURSOR query\nBROWSE NOWAIT";
    }

    /// <summary>
    /// Reads a SELECT into the designer's model. A statement the designer cannot show becomes a SQL-only query
    /// (the reason is in <see cref="SqlOnlyReason"/>), so nothing is lost.
    /// </summary>
    public static QueryDocument FromSql(string sql) => QuerySqlReader.Read(sql);
}

/// <summary>Reads .jpquery files (a strict YAML subset) and writes them.</summary>
public static class QuerySerializer
{
    public const string Header = "Joe Pro query v1";

    private static string Word<T>(T v) where T : Enum { var s = v.ToString(); return char.ToLowerInvariant(s[0]) + s[1..]; }

    private static YamlSeq Conditions(List<QueryCondition> list)
    {
        var seq = new YamlSeq();
        foreach (var c in list)
        {
            var m = new YamlMap();
            m.Add("left", new YamlScalar(c.Left, quoted: true));
            if (c.Op.Length > 0) m.Add("op", new YamlScalar(c.Op, quoted: true));
            if (c.Right.Length > 0) m.Add("right", new YamlScalar(c.Right, quoted: true));
            m.Set("not", c.Not).Set("or", c.Or);
            seq.Add(m);
        }
        return seq;
    }

    public static string Write(QueryDocument doc)
    {
        var root = new YamlMap();
        root.Set("comment", doc.Comment);
        if (doc.Sql != null)
        {
            root.Set("sql", doc.Sql.TrimEnd() + "\n");
            root.Set("sqlOnlyReason", doc.SqlOnlyReason);
        }
        var tables = new YamlSeq();
        foreach (var t in doc.Tables)
        {
            var m = new YamlMap();
            m.Add("name", new YamlScalar(t.Name, quoted: true));
            m.Set("alias", t.Alias);
            if (t.Join is { } j) m.SetWord("join", Word(j));
            if (t.On != null) m.Add("on", new YamlScalar(t.On, quoted: true));
            m.Set("x", t.X, 0).Set("y", t.Y, 0);
            tables.Add(m);
        }
        root.SetNode("tables", tables.Items.Count > 0 ? tables : null);
        var fields = new YamlSeq();
        foreach (var f in doc.Fields)
        {
            var m = new YamlMap();
            m.Add("expr", new YamlScalar(f.Expression, quoted: true));
            m.Set("as", f.Alias);
            fields.Add(m);
        }
        root.SetNode("fields", fields.Items.Count > 0 ? fields : null);
        root.SetNode("filter", doc.Filters.Count > 0 ? Conditions(doc.Filters) : null);
        if (doc.GroupBy.Count > 0)
        {
            var g = new YamlSeq();
            foreach (var e in doc.GroupBy) g.Add(new YamlScalar(e, quoted: true));
            root.SetNode("groupBy", g);
        }
        root.SetNode("having", doc.Having.Count > 0 ? Conditions(doc.Having) : null);
        if (doc.OrderBy.Count > 0)
        {
            var o = new YamlSeq();
            foreach (var e in doc.OrderBy)
            {
                var m = new YamlMap();
                m.Add("expr", new YamlScalar(e.Expression, quoted: true));
                m.Set("desc", e.Descending);
                o.Add(m);
            }
            root.SetNode("orderBy", o);
        }
        root.Set("distinct", doc.Distinct);
        if (doc.Top is { } top) root.Set("top", top);
        root.Set("percent", doc.Percent);
        root.SetWord("destination", Word(doc.Destination), "browse");
        root.Set("into", doc.IntoName);
        root.Set("readWrite", doc.ReadWrite);
        root.Set("noFilter", doc.NoFilter);
        return YamlText.Write(root, Header);
    }

    private static void ReadConditions(IEnumerable<YamlNode> nodes, List<QueryCondition> into)
    {
        foreach (var node in nodes)
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a condition should be a mapping.");
            m.CheckKeys("left", "op", "right", "not", "or");
            var op = (m.Str("op") ?? "").ToUpperInvariant();
            if (op.Length > 0 && !QueryCondition.Operators.Contains(op)) throw new FormatException($"Line {m.Line}: unknown operator {op}.");
            into.Add(new QueryCondition(m.Str("left") ?? throw new FormatException($"Line {m.Line}: a condition needs left."), op, m.Str("right", ""), m.Bool("not"), m.Bool("or")));
        }
    }

    public static QueryDocument Parse(string text)
    {
        var root = YamlText.Parse(text);
        root.CheckKeys("comment", "sql", "sqlOnlyReason", "tables", "fields", "filter", "groupBy", "having", "orderBy", "distinct", "top", "percent", "destination", "into", "readWrite", "noFilter");
        var doc = new QueryDocument
        {
            Comment = root.Str("comment"),
            Sql = root.Str("sql")?.TrimEnd(),
            SqlOnlyReason = root.Str("sqlOnlyReason"),
            Distinct = root.Bool("distinct"),
            Top = root["top"] != null ? root.Int("top", 0) : null,
            Percent = root.Bool("percent"),
            Destination = Enum.TryParse<QueryDestination>(root.Str("destination", "browse"), true, out var d) ? d : throw new FormatException($"Line {root["destination"]!.Line}: destination should be browse, cursor, table, array or screen."),
            IntoName = root.Str("into"),
            ReadWrite = root.Bool("readWrite"),
            NoFilter = root.Bool("noFilter"),
        };
        foreach (var node in root.Seq("tables"))
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a table should be a mapping.");
            m.CheckKeys("name", "alias", "join", "on", "x", "y");
            doc.Tables.Add(new QueryTable
            {
                Name = m.Str("name") ?? throw new FormatException($"Line {m.Line}: a table needs a name."),
                Alias = m.Str("alias"),
                Join = m.Str("join") is { } j ? Enum.TryParse<JoinKind>(j, true, out var k) ? k : throw new FormatException($"Line {m.Line}: join should be inner, left, right or full.") : null,
                On = m.Str("on"),
                X = m.Num("x", 0),
                Y = m.Num("y", 0),
            });
        }
        foreach (var node in root.Seq("fields"))
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a field should be a mapping.");
            m.CheckKeys("expr", "as");
            doc.Fields.Add(new QueryField(m.Str("expr") ?? throw new FormatException($"Line {m.Line}: a field needs expr."), m.Str("as")));
        }
        ReadConditions(root.Seq("filter"), doc.Filters);
        foreach (var node in root.Seq("groupBy")) doc.GroupBy.Add((node as YamlScalar)?.Value ?? throw new FormatException($"Line {node.Line}: groupBy lists expressions."));
        ReadConditions(root.Seq("having"), doc.Having);
        foreach (var node in root.Seq("orderBy"))
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: an order should be a mapping.");
            m.CheckKeys("expr", "desc");
            doc.OrderBy.Add(new QueryOrder(m.Str("expr") ?? throw new FormatException($"Line {m.Line}: an order needs expr."), m.Bool("desc")));
        }
        return doc;
    }
}

/// <summary>Converts a Query Designer file (.QPR, plain SELECT-SQL) to a .jpquery.</summary>
public static class LegacyQueryConverter
{
    public sealed record Result(QueryDocument Document, List<ConversionFinding> Findings);

    public static Result Convert(string qprPath) => ConvertText(File.ReadAllText(qprPath, Encoding.Latin1), Path.GetFileName(qprPath));

    public static Result ConvertText(string text, string file = "query.qpr")
    {
        var findings = new List<ConversionFinding>();
        var statements = QuerySqlReader.Statements(text);
        var select = statements.FirstOrDefault(s => s.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || s.TrimStart().StartsWith("SELE ", StringComparison.OrdinalIgnoreCase));
        if (select == null)
        {
            findings.Add(new(FindingStatus.NeedsReview, file, "The query file has no SELECT statement; it was kept as SQL."));
            return new(new QueryDocument { Sql = text.Trim(), SqlOnlyReason = "no SELECT statement" }, findings);
        }
        foreach (var other in statements.Where(s => !ReferenceEquals(s, select)))
            findings.Add(new(FindingStatus.NeedsReview, file, $"The statement after the query was not kept: {other.Split('\n')[0].Trim()}"));
        var doc = QueryDocument.FromSql(select);
        if (doc.IsSqlOnly) findings.Add(new(FindingStatus.NeedsReview, file, $"The query is kept as SQL ({doc.SqlOnlyReason}); the designer edits it as text."));
        return new(doc, findings);
    }
}
