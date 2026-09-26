using JoePro.Core;
using Microsoft.Data.Sqlite;

namespace JoePro.Data;

public enum StoreKind { Database, FreeTable, Cursors }

/// <summary>
/// One SQLite file holding Joe Pro tables: a database (.jpdb), a free table (.jpt), or the
/// in-memory store that holds a data session's cursors. Each data session opens its own
/// connection, so sessions are isolated like VFP data sessions.
/// </summary>
public sealed class Store : IDisposable
{
    public const int FormatVersion = 1;
    public const string DatabaseExtension = ".jpdb";
    public const string FreeTableExtension = ".jpt";

    private readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);
    private int _savepointDepth;

    public string Path { get; }
    public string Name { get; }
    public StoreKind Kind { get; }
    internal SqliteConnection Connection { get; }
    public int TransactionLevel => _savepointDepth;

    private Store(string path, StoreKind kind, SqliteConnection connection)
    {
        Path = path;
        Kind = kind;
        Name = kind == StoreKind.Cursors ? "(cursors)" : System.IO.Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        Connection = connection;
    }

    public static Store Create(string path, StoreKind kind)
    {
        if (File.Exists(path)) throw new VfpException(ErrorCodes.FileInUse, $"File '{path}' already exists.", path);
        var store = OpenInternal(path, kind, create: true);
        store.Exec($"""
            CREATE TABLE _jp_meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE _jp_tables(name TEXT PRIMARY KEY COLLATE NOCASE, sqlname TEXT NOT NULL, comment TEXT,
                rule_expr TEXT, rule_text TEXT, insert_trigger TEXT, update_trigger TEXT, delete_trigger TEXT);
            CREATE TABLE _jp_fields(tbl TEXT NOT NULL COLLATE NOCASE, ord INTEGER NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL,
                width INTEGER, decimals INTEGER, nullable INTEGER, isbinary INTEGER, autoinc_next INTEGER, autoinc_step INTEGER,
                default_expr TEXT, rule_expr TEXT, rule_text TEXT, caption TEXT, PRIMARY KEY(tbl, ord));
            CREATE TABLE _jp_tags(tbl TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL COLLATE NOCASE, expr TEXT NOT NULL,
                for_expr TEXT, descending INTEGER, kind TEXT, collation TEXT, keycol TEXT NOT NULL, PRIMARY KEY(tbl, name));
            CREATE TABLE _jp_relations(parent TEXT, parent_tag TEXT, child TEXT, child_tag TEXT,
                ri_update TEXT, ri_delete TEXT, ri_insert TEXT);
            INSERT INTO _jp_meta VALUES ('format', '{FormatVersion}'), ('kind', '{kind}');
            """);
        return store;
    }

    public static Store Open(string path)
    {
        if (!File.Exists(path)) throw VfpException.FileNotFound(path);
        var store = OpenInternal(path, StoreKind.Database, create: false);
        var kind = store.ScalarString("SELECT value FROM _jp_meta WHERE key='kind'");
        if (kind == null)
        {
            store.Dispose();
            throw new VfpException(ErrorCodes.FileAccessDenied, $"'{path}' is not a Joe Pro database or table.", path);
        }
        var s = new Store(path, Enum.Parse<StoreKind>(kind), store.Connection);
        return s;
    }

    public static Store CreateInMemory()
    {
        var store = Create(":memory:", StoreKind.Cursors);
        return store;
    }

    private static Store OpenInternal(string path, StoreKind kind, bool create)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        var s = new Store(path, kind, conn);
        if (path != ":memory:") s.Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        s.Exec("PRAGMA busy_timeout=5000;");
        return s;
    }

    // ---- SQL helpers -------------------------------------------------------------------

    internal SqliteCommand Command(string sql, params (string, object?)[] args)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    internal void Exec(string sql, params (string, object?)[] args)
    {
        BumpVersion();
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    // ---- Prepared statements and change tracking ----------------------------------------

    private readonly Dictionary<string, SqliteCommand> _prepared = new();
    private SqliteCommand? _dataVersionCmd;
    private long _lastDataVersion;
    private long _dataVersionCheckedAt = long.MinValue;
    private const long DataVersionIntervalMs = 250;

    // One write counter per file, shared by every Store (data session) in this process that opens it,
    // so cached rows are invalidated exactly by writes from any session.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, StrongBox> FileVersions = new(StringComparer.Ordinal);
    private StrongBox? _version;
    private StrongBox Version => _version ??= Kind == StoreKind.Cursors || Path == ":memory:"
        ? new StrongBox()
        : FileVersions.GetOrAdd(System.IO.Path.GetFullPath(Path), _ => new StrongBox());

    private sealed class StrongBox { public long Value; }

    /// <summary>Incremented by every write to this file from this process (and by rollbacks), so cached rows can be checked for staleness.</summary>
    internal long WriteVersion
    {
        get => Interlocked.Read(ref Version.Value);
        private set => Interlocked.Exchange(ref Version.Value, value);
    }

    private void BumpVersion() => Interlocked.Increment(ref Version.Value);

    /// <summary>
    /// A cached, prepared command for a hot statement (row reads, index scans, inserts). The caller must not
    /// dispose it, and must finish with any reader before the same statement is used again.
    /// </summary>
    internal SqliteCommand Prepared(string sql, params (string Name, object? Value)[] args)
    {
        if (!_prepared.TryGetValue(sql, out var cmd))
        {
            cmd = Connection.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
            _prepared[sql] = cmd;
            return cmd;
        }
        foreach (var (n, v) in args) cmd.Parameters[n].Value = v ?? DBNull.Value;
        return cmd;
    }

    internal object? ScalarPrepared(string sql, params (string Name, object? Value)[] args)
    {
        BumpVersion();
        return Prepared(sql, args).ExecuteScalar();
    }

    internal int ExecPrepared(string sql, params (string Name, object? Value)[] args)
    {
        BumpVersion();
        return Prepared(sql, args).ExecuteNonQuery();
    }

    /// <summary>
    /// Changes when another process commits to this file. Checked at most every 250 ms (like SET REFRESH);
    /// writes from this process are tracked exactly by <see cref="WriteVersion"/>.
    /// </summary>
    internal long DataVersion()
    {
        if (Kind == StoreKind.Cursors || Path == ":memory:") return 0;
        var now = Environment.TickCount64;
        if (now - _dataVersionCheckedAt < DataVersionIntervalMs) return _lastDataVersion;
        _dataVersionCheckedAt = now;
        _dataVersionCmd ??= Connection.CreateCommand();
        _dataVersionCmd.CommandText = "PRAGMA data_version";
        return _lastDataVersion = (long)_dataVersionCmd.ExecuteScalar()!;
    }

    internal string? ScalarString(string sql, params (string, object?)[] args)
    {
        try
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException) { return null; }
    }

    internal long ScalarLong(string sql, params (string, object?)[] args)
    {
        using var cmd = Command(sql, args);
        var r = cmd.ExecuteScalar();
        return r is null or DBNull ? 0 : Convert.ToInt64(r);
    }

    // ---- Tables -----------------------------------------------------------------------

    public IReadOnlyList<string> TableNames()
    {
        using var cmd = Command("SELECT name FROM _jp_tables ORDER BY name");
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public bool HasTable(string name) =>
        ScalarLong("SELECT COUNT(*) FROM _jp_tables WHERE name=$n", ("$n", name)) > 0;

    public Table CreateTable(TableSchema schema, IExpressionHost? host = null)
    {
        if (HasTable(schema.Name)) throw new VfpException(ErrorCodes.FileInUse, $"Table {schema.Name} already exists.", schema.Name);
        var sqlName = "t_" + schema.Name.ToLowerInvariant();
        var cols = string.Join(", ", schema.Fields.Select(f => $"\"c_{f.Name.ToLowerInvariant()}\" {ValueCodec.SqlType(f)}"));
        InTransaction(() =>
        {
            Exec($"CREATE TABLE \"{sqlName}\" (_recno INTEGER PRIMARY KEY, _deleted INTEGER NOT NULL DEFAULT 0, _rowver INTEGER NOT NULL DEFAULT 0, {cols})");
            Exec("INSERT INTO _jp_tables(name, sqlname, comment, rule_expr, rule_text, insert_trigger, update_trigger, delete_trigger) VALUES ($n,$s,$c,$re,$rt,$it,$ut,$dt)",
                ("$n", schema.Name), ("$s", sqlName), ("$c", schema.Comment), ("$re", schema.RuleExpr), ("$rt", schema.RuleText),
                ("$it", schema.InsertTrigger), ("$ut", schema.UpdateTrigger), ("$dt", schema.DeleteTrigger));
            for (int i = 0; i < schema.Fields.Count; i++)
            {
                var f = schema.Fields[i];
                Exec("""
                    INSERT INTO _jp_fields(tbl, ord, name, type, width, decimals, nullable, isbinary, autoinc_next, autoinc_step, default_expr, rule_expr, rule_text, caption)
                    VALUES ($t,$o,$n,$ty,$w,$d,$nu,$b,$an,$as,$de,$re,$rt,$ca)
                    """,
                    ("$t", schema.Name), ("$o", i), ("$n", f.Name), ("$ty", f.Type.ToString()), ("$w", f.Width), ("$d", f.Decimals),
                    ("$nu", f.Nullable ? 1 : 0), ("$b", f.Binary ? 1 : 0), ("$an", f.AutoIncNext), ("$as", f.AutoIncStep),
                    ("$de", f.DefaultExpr), ("$re", f.RuleExpr), ("$rt", f.RuleText), ("$ca", f.Caption));
            }
        });
        var table = OpenTable(schema.Name, host);
        foreach (var tag in schema.Tags) table.CreateTag(tag);
        return table;
    }

    public Table OpenTable(string name, IExpressionHost? host = null)
    {
        if (_tables.TryGetValue(name, out var t))
        {
            if (host != null) t.ExpressionHost ??= host;
            return t;
        }
        using var cmd = Command("SELECT name, sqlname, comment, rule_expr, rule_text, insert_trigger, update_trigger, delete_trigger FROM _jp_tables WHERE name=$n", ("$n", name));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw VfpException.FileNotFound(name);
        var tableName = r.GetString(0);
        var sqlName = r.GetString(1);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var comment = S(2); var ruleExpr = S(3); var ruleText = S(4); var it = S(5); var ut = S(6); var dt = S(7);
        r.Close();

        var fields = new List<FieldDef>();
        using (var fc = Command("SELECT name, type, width, decimals, nullable, isbinary, autoinc_next, autoinc_step, default_expr, rule_expr, rule_text, caption FROM _jp_fields WHERE tbl=$t ORDER BY ord", ("$t", tableName)))
        using (var fr = fc.ExecuteReader())
        {
            while (fr.Read())
            {
                string? FS(int i) => fr.IsDBNull(i) ? null : fr.GetString(i);
                fields.Add(new FieldDef(fr.GetString(0), fr.GetString(1)[0], fr.GetInt32(2), fr.GetInt32(3))
                {
                    Nullable = fr.GetInt32(4) != 0,
                    Binary = fr.GetInt32(5) != 0,
                    AutoIncNext = fr.IsDBNull(6) ? null : fr.GetInt64(6),
                    AutoIncStep = fr.IsDBNull(7) ? 1 : fr.GetInt32(7),
                    DefaultExpr = FS(8), RuleExpr = FS(9), RuleText = FS(10), Caption = FS(11),
                });
            }
        }
        var schema = new TableSchema(tableName, fields)
        {
            Comment = comment, RuleExpr = ruleExpr, RuleText = ruleText, InsertTrigger = it, UpdateTrigger = ut, DeleteTrigger = dt,
        };
        using (var tc = Command("SELECT name, expr, for_expr, descending, kind, collation, keycol FROM _jp_tags WHERE tbl=$t ORDER BY rowid", ("$t", tableName)))
        using (var tr = tc.ExecuteReader())
        {
            while (tr.Read())
            {
                schema.Tags.Add(new TagDef(tr.GetString(0), tr.GetString(1), tr.IsDBNull(2) ? null : tr.GetString(2), tr.GetInt32(3) != 0,
                    Enum.Parse<TagKind>(tr.GetString(4)), tr.GetString(5)) { KeyColumn = tr.GetString(6) });
            }
        }
        t = new Table(this, schema, sqlName) { ExpressionHost = host };
        _tables[tableName] = t;
        return t;
    }

    public void DropTable(string name)
    {
        var t = OpenTable(name);
        InTransaction(() =>
        {
            Exec($"DROP TABLE \"{t.SqlName}\"");
            Exec("DELETE FROM _jp_tables WHERE name=$n", ("$n", t.Name));
            Exec("DELETE FROM _jp_fields WHERE tbl=$n", ("$n", t.Name));
            Exec("DELETE FROM _jp_tags WHERE tbl=$n", ("$n", t.Name));
            Exec("DELETE FROM _jp_relations WHERE parent=$n OR child=$n", ("$n", t.Name));
        });
        _tables.Remove(t.Name);
    }

    // ---- Database-level metadata ------------------------------------------------------

    public string? GetMeta(string key) => ScalarString("SELECT value FROM _jp_meta WHERE key=$k", ("$k", key));

    public void SetMeta(string key, string? value) =>
        Exec("INSERT INTO _jp_meta(key, value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));

    /// <summary>Stored procedures (one code blob per database, like the DBC).</summary>
    public string StoredProcedures
    {
        get => GetMeta("stored_procedures") ?? "";
        set => SetMeta("stored_procedures", value);
    }

    // ---- Named objects (views, connections) ------------------------------------------

    private bool _objectsReady;

    private void EnsureObjectTable()
    {
        if (_objectsReady) return;
        Exec("CREATE TABLE IF NOT EXISTS _jp_objects(kind TEXT NOT NULL, name TEXT NOT NULL COLLATE NOCASE, definition TEXT NOT NULL, PRIMARY KEY(kind, name))");
        _objectsReady = true;
    }

    public void SaveObject(string kind, string name, string definition)
    {
        EnsureObjectTable();
        Exec("INSERT INTO _jp_objects(kind, name, definition) VALUES ($k,$n,$d) ON CONFLICT(kind, name) DO UPDATE SET definition=excluded.definition",
            ("$k", kind), ("$n", name), ("$d", definition));
    }

    public string? GetObject(string kind, string name)
    {
        EnsureObjectTable();
        return ScalarString("SELECT definition FROM _jp_objects WHERE kind=$k AND name=$n", ("$k", kind), ("$n", name));
    }

    public bool HasObject(string kind, string name) => GetObject(kind, name) != null;

    public IReadOnlyList<string> ObjectNames(string kind)
    {
        EnsureObjectTable();
        using var cmd = Command("SELECT name FROM _jp_objects WHERE kind=$k ORDER BY name", ("$k", kind));
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public bool DeleteObject(string kind, string name)
    {
        EnsureObjectTable();
        return ScalarLong("SELECT COUNT(*) FROM _jp_objects WHERE kind=$k AND name=$n", ("$k", kind), ("$n", name)) > 0
            && ExecCount("DELETE FROM _jp_objects WHERE kind=$k AND name=$n", ("$k", kind), ("$n", name)) > 0;
    }

    private int ExecCount(string sql, params (string, object?)[] args)
    {
        BumpVersion();
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public void AddRelation(RelationDef r) =>
        Exec("INSERT INTO _jp_relations VALUES ($p,$pt,$c,$ct,$u,$d,$i)",
            ("$p", r.ParentTable), ("$pt", r.ParentTag), ("$c", r.ChildTable), ("$ct", r.ChildTag), ("$u", r.RiUpdate), ("$d", r.RiDelete), ("$i", r.RiInsert));

    public IReadOnlyList<RelationDef> Relations()
    {
        using var cmd = Command("SELECT parent, parent_tag, child, child_tag, ri_update, ri_delete, ri_insert FROM _jp_relations");
        using var r = cmd.ExecuteReader();
        var list = new List<RelationDef>();
        while (r.Read()) list.Add(new RelationDef(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    }

    // ---- Transactions -----------------------------------------------------------------

    /// <summary>Starts a (nested) transaction. VFP allows five levels; each maps to a SQLite savepoint.</summary>
    public void BeginTransaction()
    {
        _savepointDepth++;
        ExecPrepared($"SAVEPOINT jp{_savepointDepth}");
    }

    public void Commit()
    {
        if (_savepointDepth == 0) throw new VfpException(ErrorCodes.NoTransaction, "Command cannot be issued outside a transaction.");
        ExecPrepared($"RELEASE jp{_savepointDepth}");
        _savepointDepth--;
    }

    public void Rollback()
    {
        if (_savepointDepth == 0) return;
        Exec($"ROLLBACK TO jp{_savepointDepth}; RELEASE jp{_savepointDepth}");
        _savepointDepth--;
        foreach (var t in _tables.Values) t.InvalidateCaches();
    }

    /// <summary>
    /// Runs a multi-row command (REPLACE ALL, UPDATE-SQL, APPEND FROM…) with a single commit instead of one
    /// per row. Rows written before an error are still committed, as in VFP.
    /// </summary>
    public void Batch(Action action)
    {
        BeginTransaction();
        var depth = _savepointDepth;
        try { action(); }
        finally { if (_savepointDepth == depth) Commit(); }
    }

    /// <summary>Runs <paramref name="action"/> atomically (as a nested savepoint when a transaction is open).</summary>
    internal void InTransaction(Action action)
    {
        BeginTransaction();
        try
        {
            action();
            Commit();
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    public void Dispose()
    {
        while (_savepointDepth > 0) Rollback();
        foreach (var cmd in _prepared.Values) cmd.Dispose();
        _prepared.Clear();
        _dataVersionCmd?.Dispose();
        Connection.Dispose();
    }
}
