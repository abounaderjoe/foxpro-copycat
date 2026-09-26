using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Text;
using JoePro.Core;
using JoePro.Data;
using Microsoft.Data.Sqlite;

namespace JoePro.Runtime;

/// <summary>
/// An ADO.NET provider that SQL pass-through can open. Connection strings pick a provider with
/// "Provider=name;…"; ODBC-style strings (Driver=…, DSN=…) use the ODBC provider, as in VFP.
/// </summary>
public sealed record RemoteProvider(string Name, Func<string, DbConnection> Create, bool NamedParameters)
{
    /// <summary>The full declared type of a base column (with length and precision), when the provider's type names omit it.</summary>
    public Func<DbConnection, string, string, string?>? DeclaredType { get; init; }

    /// <summary>Lists tables and views as (catalog, schema, name, type); null uses DbConnection.GetSchema("Tables").</summary>
    public Func<DbConnection, IEnumerable<(string? Catalog, string? Schema, string Name, string Type)>>? Tables { get; init; }
}

public static class RemoteProviders
{
    private static readonly Dictionary<string, RemoteProvider> Registered = new(StringComparer.OrdinalIgnoreCase);

    static RemoteProviders()
    {
        Register(new RemoteProvider("sqlite", cs => new SqliteConnection(cs), NamedParameters: true)
        {
            Tables = conn =>
            {
                var list = new List<(string?, string?, string, string)>();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name, type FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add((null, null, r.GetString(0), r.GetString(1).ToUpperInvariant()));
                return list;
            },
            DeclaredType = (conn, table, column) =>
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT type FROM pragma_table_info($t) WHERE name = $c COLLATE NOCASE";
                cmd.Parameters.Add(new SqliteParameter("$t", table));
                cmd.Parameters.Add(new SqliteParameter("$c", column));
                return cmd.ExecuteScalar() as string;
            },
        });
        Register(new RemoteProvider("odbc", cs => new OdbcConnection(cs), NamedParameters: false));
    }

    /// <summary>Adds a provider (SQL Server, PostgreSQL, …) that connection strings can name with Provider=.</summary>
    public static void Register(RemoteProvider provider)
    {
        lock (Registered) Registered[provider.Name] = provider;
    }

    public static (RemoteProvider Provider, string ConnectionString) Resolve(string connectionString)
    {
        var builder = new DbConnectionStringBuilder();
        try { builder.ConnectionString = connectionString; }
        catch (ArgumentException ex) { throw new VfpException(1526, "Connectivity error: " + ex.Message); }
        if (builder.TryGetValue("Provider", out var p) && p is string name)
        {
            lock (Registered)
                if (Registered.TryGetValue(name, out var provider))
                {
                    builder.Remove("Provider");
                    return (provider, builder.ConnectionString);
                }
            throw new VfpException(1526, $"Connectivity error: provider '{name}' is not registered.");
        }
        lock (Registered) return (Registered["odbc"], connectionString);
    }
}

/// <summary>One SQL pass-through connection handle (SQLCONNECT / SQLSTRINGCONNECT).</summary>
public sealed class RemoteConnection : IDisposable
{
    public RemoteConnection(int handle, RemoteProvider provider, DbConnection connection, string connectString)
    {
        Handle = handle;
        Provider = provider;
        Connection = connection;
        ConnectString = connectString;
    }

    public int Handle { get; }
    public RemoteProvider Provider { get; }
    public DbConnection Connection { get; }
    public string ConnectString { get; }
    public DbTransaction? Transaction { get; set; }
    public Dictionary<string, Value> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SQLPREPARE: the statement and cursor name for the next SQLEXEC(nHandle).</summary>
    public (string Sql, string? Cursor)? Prepared { get; set; }
    /// <summary>Result sets not yet delivered when BatchMode is off (SQLMORERESULTS).</summary>
    public Queue<ResultSet> Pending { get; } = new();
    public string PendingCursor { get; set; } = "SQLRESULT";
    public int PendingIndex { get; set; }

    public bool ManualTransactions => Properties.TryGetValue("Transactions", out var t) && t.Kind == ValueKind.Number && (int)t.AsNumber == 2;

    public void Dispose()
    {
        try { Transaction?.Rollback(); } catch (DbException) { }
        Connection.Dispose();
    }
}

public sealed record ResultSet(List<FieldDef> Fields, List<Value[]> Rows, int RecordsAffected);

/// <summary>SQL pass-through: SQLEXEC and friends running native SQL on an ADO.NET connection.</summary>
public sealed class SqlPassThrough
{
    private readonly Interpreter _rt;
    private readonly Dictionary<int, RemoteConnection> _connections = new();
    private int _nextHandle;

    public SqlPassThrough(Interpreter rt) => _rt = rt;

    /// <summary>Defaults applied to new connections (SQLSETPROP(0, …)).</summary>
    public Dictionary<string, Value> Defaults { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Asynchronous"] = Value.False,
        ["BatchMode"] = Value.True,
        ["ConnectTimeOut"] = Value.Number(15),
        ["DispLogin"] = Value.Number(3),
        ["DispWarnings"] = Value.False,
        ["DisconnectRollback"] = Value.False,
        ["IdleTimeout"] = Value.Number(0),
        ["PacketSize"] = Value.Number(4096),
        ["QueryTimeOut"] = Value.Number(0),
        ["Transactions"] = Value.Number(1),
        ["WaitTime"] = Value.Number(100),
    };

    public IEnumerable<int> Handles => _connections.Keys.OrderBy(h => h);

    public RemoteConnection Get(int handle) =>
        _connections.TryGetValue(handle, out var c) ? c : throw new VfpException(1466, "Connection handle is invalid.");

    public int Connect(string connectString)
    {
        var (provider, cs) = RemoteProviders.Resolve(connectString);
        if (provider.Name == "sqlite") cs = ResolveSqlitePath(cs);
        var conn = provider.Create(cs);
        try { conn.Open(); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or DllNotFoundException or ArgumentException)
        {
            conn.Dispose();
            throw Connectivity(ex);
        }
        var rc = new RemoteConnection(++_nextHandle, provider, conn, connectString);
        foreach (var (k, v) in Defaults) rc.Properties[k] = v;
        rc.Properties["ConnectString"] = Value.String(connectString);
        _connections[rc.Handle] = rc;
        return rc.Handle;
    }

    private string ResolveSqlitePath(string cs)
    {
        var b = new SqliteConnectionStringBuilder(cs);
        if (!string.IsNullOrEmpty(b.DataSource) && b.DataSource != ":memory:" && !b.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathRooted(b.DataSource))
            b.DataSource = Path.Combine(_rt.Options.Default_, b.DataSource);
        return b.ConnectionString;
    }

    public int Disconnect(int handle)
    {
        if (handle == 0)
        {
            foreach (var c in _connections.Values) c.Dispose();
            _connections.Clear();
            return 1;
        }
        var rc = Get(handle);
        if (rc.Transaction != null && rc.Properties.TryGetValue("DisconnectRollback", out var dr) && !dr.AsBool)
        {
            rc.Transaction.Commit();
            rc.Transaction = null;
        }
        rc.Dispose();
        _connections.Remove(handle);
        return 1;
    }

    public void DisconnectAll() => Disconnect(0);

    /// <summary>
    /// SQLEXEC: runs a statement, binding ?name and ?(expression) parameters, and creates one cursor per
    /// result set (SQLRESULT, SQLRESULT1, …). Returns the number of cursors created (1 when none).
    /// </summary>
    public int Exec(int handle, string? sql, string? cursorName, Action<List<(string Alias, int Count)>>? countInfo)
    {
        var rc = Get(handle);
        if (sql == null)
        {
            if (rc.Prepared is not { } prepared) throw new VfpException(1526, "Connectivity error: no statement has been prepared.");
            sql = prepared.Sql;
            cursorName ??= prepared.Cursor;
        }
        cursorName = string.IsNullOrWhiteSpace(cursorName) ? "SQLRESULT" : cursorName.Trim();
        rc.Pending.Clear();

        var sets = new List<ResultSet>();
        try
        {
            using var cmd = rc.Connection.CreateCommand();
            cmd.CommandText = BindParameters(rc, sql, cmd);
            var timeout = rc.Properties.TryGetValue("QueryTimeOut", out var qt) ? (int)qt.AsNumber : 0;
            if (timeout > 0) cmd.CommandTimeout = timeout;
            if (rc.ManualTransactions && rc.Transaction == null) rc.Transaction = rc.Connection.BeginTransaction();
            cmd.Transaction = rc.Transaction;
            using var reader = cmd.ExecuteReader();
            do
            {
                if (reader.FieldCount > 0) sets.Add(Read(reader, -1, DeclaredTypes(rc)));
            } while (reader.NextResult());
            if (sets.Count == 0) sets.Add(new ResultSet(new(), new(), reader.RecordsAffected));
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            throw Connectivity(ex);
        }

        var counts = new List<(string, int)>();
        var batch = !rc.Properties.TryGetValue("BatchMode", out var bm) || bm.AsBool;
        int created = 0;
        for (int i = 0; i < sets.Count; i++)
        {
            var set = sets[i];
            if (set.Fields.Count == 0) { counts.Add(("", set.RecordsAffected)); continue; }
            if (!batch && created > 0) { rc.Pending.Enqueue(set); continue; }
            var alias = created == 0 ? cursorName : cursorName + created;
            Materialize(alias, set);
            counts.Add((alias.ToUpperInvariant(), set.Rows.Count));
            created++;
        }
        if (!batch) { rc.PendingCursor = cursorName; rc.PendingIndex = created; }
        countInfo?.Invoke(counts);
        if (created == 0 && sets.Count == 1) _rt.SetVariable("_TALLY", Value.Number(Math.Max(0, sets[0].RecordsAffected)));
        return Math.Max(1, created);
    }

    /// <summary>SQLMORERESULTS: the next result set when BatchMode is off; 2 when there are no more.</summary>
    public int MoreResults(int handle)
    {
        var rc = Get(handle);
        if (rc.Pending.Count == 0) return 2;
        var alias = rc.PendingIndex == 0 ? rc.PendingCursor : rc.PendingCursor + rc.PendingIndex;
        Materialize(alias, rc.Pending.Dequeue());
        rc.PendingIndex++;
        return 1;
    }

    public int Commit(int handle)
    {
        var rc = Get(handle);
        try { rc.Transaction?.Commit(); }
        catch (DbException ex) { throw Connectivity(ex); }
        finally { rc.Transaction = null; }
        return 1;
    }

    public int Rollback(int handle)
    {
        var rc = Get(handle);
        try { rc.Transaction?.Rollback(); }
        catch (DbException ex) { throw Connectivity(ex); }
        finally { rc.Transaction = null; }
        return 1;
    }

    /// <summary>SQLTABLES: a cursor of TABLE_CAT, TABLE_SCHEM, TABLE_NAME, TABLE_TYPE, REMARKS.</summary>
    public int Tables(int handle, string? types, string? cursorName)
    {
        var rc = Get(handle);
        var wanted = (types ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('\'', '"').ToUpperInvariant()).ToHashSet();
        IEnumerable<(string? Catalog, string? Schema, string Name, string Type)> rows;
        try
        {
            if (rc.Provider.Tables != null) rows = rc.Provider.Tables(rc.Connection).ToList();
            else
            {
                var dt = rc.Connection.GetSchema("Tables");
                string? Col(DataRow r, string name) => dt.Columns.Contains(name) && r[name] is string s ? s : null;
                rows = dt.Rows.Cast<DataRow>().Select(r => (Col(r, "TABLE_CAT"), Col(r, "TABLE_SCHEM"), Col(r, "TABLE_NAME") ?? "", Col(r, "TABLE_TYPE") ?? "TABLE")).ToList();
            }
        }
        catch (Exception ex) when (ex is DbException or NotSupportedException or ArgumentException) { throw Connectivity(ex); }
        var fields = new List<FieldDef>
        {
            new("TABLE_CAT", 'C', 128) { Nullable = true }, new("TABLE_SCHEM", 'C', 128) { Nullable = true },
            new("TABLE_NAME", 'C', 128), new("TABLE_TYPE", 'C', 32), new("REMARKS", 'C', 254) { Nullable = true },
        };
        var data = rows.Where(r => wanted.Count == 0 || wanted.Contains(r.Type.ToUpperInvariant()))
            .Select(r => new[] { Str(r.Catalog), Str(r.Schema), Value.String(r.Name), Value.String(r.Type), Value.Null }).ToList();
        Materialize(string.IsNullOrWhiteSpace(cursorName) ? "SQLRESULT" : cursorName, new ResultSet(fields, data, 0));
        return 1;
    }

    /// <summary>SQLCOLUMNS: FOXPRO format gives FIELD_NAME, FIELD_TYPE, FIELD_LEN, FIELD_DEC; NATIVE gives the server's type names.</summary>
    public int Columns(int handle, string table, bool native, string? cursorName)
    {
        var rc = Get(handle);
        ResultSet probe;
        var nativeTypes = new List<string>();
        var names = new List<string>();
        try
        {
            using var cmd = rc.Connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} WHERE 1 = 0";
            cmd.Transaction = rc.Transaction;
            using var reader = cmd.ExecuteReader();
            probe = Read(reader, -1, DeclaredTypes(rc));
            for (int i = 0; i < reader.FieldCount; i++) { nativeTypes.Add(reader.GetDataTypeName(i)); names.Add(reader.GetName(i)); }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { throw Connectivity(ex); }
        List<FieldDef> fields;
        List<Value[]> rows;
        if (native)
        {
            fields = [new("COLUMN_NAME", 'C', 128), new("TYPE_NAME", 'C', 128), new("PRECISION", 'I'), new("SCALE", 'I')];
            rows = probe.Fields.Select((f, i) => new[] { Value.String(names[i]), Value.String(nativeTypes[i]), Value.Number(f.Width), Value.Number(f.Decimals) }).ToList();
        }
        else
        {
            fields = [new("FIELD_NAME", 'C', 128), new("FIELD_TYPE", 'C', 1), new("FIELD_LEN", 'I'), new("FIELD_DEC", 'I')];
            rows = probe.Fields.Select((f, i) => (Def: f.Normalize(), Name: names[i]))
                .Select(x => new[] { Value.String(x.Name), Value.String(x.Def.Type.ToString()), Value.Number(x.Def.Width), Value.Number(x.Def.Decimals) }).ToList();
        }
        Materialize(string.IsNullOrWhiteSpace(cursorName) ? "SQLRESULT" : cursorName, new ResultSet(fields, rows, 0));
        return 1;
    }

    private static Value Str(string? s) => s == null ? Value.Null : Value.String(s);

    internal static Func<string, string, string?>? DeclaredTypes(RemoteConnection rc)
    {
        if (rc.Provider.DeclaredType is not { } lookup) return null;
        var cache = new Dictionary<(string, string), string?>();
        return (t, c) =>
        {
            if (!cache.TryGetValue((t, c), out var v))
            {
                try { v = lookup(rc.Connection, t, c); } catch (DbException) { v = null; }
                cache[(t, c)] = v;
            }
            return v;
        };
    }

    /// <summary>Replaces ?name and ?(expression) outside string literals with provider parameters.</summary>
    internal string BindParameters(RemoteConnection rc, string sql, DbCommand cmd, bool missingAsNull = false)
    {
        var sb = new StringBuilder(sql.Length);
        int n = 0;
        for (int i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (ch is '\'' or '"' or '[')
            {
                var close = ch == '[' ? ']' : ch;
                int j = i + 1;
                while (j < sql.Length)
                {
                    if (sql[j] == close)
                    {
                        if (close != ']' && j + 1 < sql.Length && sql[j + 1] == close) { j += 2; continue; }
                        break;
                    }
                    j++;
                }
                sb.Append(sql, i, Math.Min(j + 1, sql.Length) - i);
                i = j;
                continue;
            }
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var eol = sql.IndexOf('\n', i);
                if (eol < 0) eol = sql.Length - 1;
                sb.Append(sql, i, eol - i + 1);
                i = eol;
                continue;
            }
            if (ch == '?' && i + 1 < sql.Length && (sql[i + 1] == '(' || char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                string expr;
                int end;
                if (sql[i + 1] == '(')
                {
                    int depth = 0;
                    end = i + 1;
                    for (; end < sql.Length; end++)
                    {
                        if (sql[end] == '(') depth++;
                        else if (sql[end] == ')' && --depth == 0) break;
                    }
                    expr = sql[(i + 2)..Math.Min(end, sql.Length)];
                }
                else
                {
                    end = i + 1;
                    while (end + 1 < sql.Length && (char.IsLetterOrDigit(sql[end + 1]) || sql[end + 1] is '_' or '.')) end++;
                    if (sql[end] == '.') end--;
                    expr = sql[(i + 1)..(end + 1)];
                }
                Value value;
                try { value = _rt.Evaluate(expr); }
                catch (VfpException) when (missingAsNull) { value = Value.Null; } // CREATE SQL VIEW: parameters need not exist yet
                var p = cmd.CreateParameter();
                p.ParameterName = "@p" + ++n;
                p.Value = ParameterValue(value);
                cmd.Parameters.Add(p);
                sb.Append(rc.Provider.NamedParameters ? p.ParameterName : "?");
                i = end;
                continue;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    internal static object ParameterValue(Value v) => v.Kind switch
    {
        ValueKind.Null => DBNull.Value,
        ValueKind.Character => v.AsString,
        ValueKind.Logical => v.AsBool,
        ValueKind.Number => v.Decimals == 0 && Math.Abs(v.AsNumber) < long.MaxValue && v.AsNumber == Math.Floor(v.AsNumber) ? (long)v.AsNumber : v.AsNumber,
        ValueKind.Currency => v.AsCurrency,
        ValueKind.Date => v.IsEmpty ? DBNull.Value : Julian.ToDate(v.JulianDay).ToDateTime(TimeOnly.MinValue),
        ValueKind.DateTime => v.IsEmpty ? DBNull.Value : Julian.ToDateTime(v.JulianMs),
        ValueKind.Binary => v.AsBinary,
        _ => ClrObjectProxy.ToObject(v, null) ?? DBNull.Value,
    };

    /// <summary>Reads the current result set and chooses FoxPro field types for its columns.</summary>
    internal static ResultSet Read(DbDataReader reader, int maxRecords = -1, Func<string, string, string?>? declaredType = null)
    {
        int n = reader.FieldCount;
        var rows = new List<Value[]>();
        // Providers such as SQLite type each value separately, so remember the CLR type and type name of the first non-null value.
        var clrTypes = new Type?[n];
        var typeNames = new string?[n];
        for (int i = 0; i < n; i++)
        {
            try { clrTypes[i] = reader.GetFieldType(i); } catch (Exception) { }
        }
        var observed = new Type?[n];
        var observedNames = new string?[n];
        while ((maxRecords < 0 || rows.Count < maxRecords) && reader.Read())
        {
            var row = new Value[n];
            for (int i = 0; i < n; i++)
            {
                if (reader.IsDBNull(i)) { row[i] = Value.Null; continue; }
                var raw = reader.GetValue(i);
                if (observed[i] == null)
                {
                    observed[i] = raw.GetType();
                    observedNames[i] = SafeTypeName(reader, i);
                }
                var v = ClrObjectProxy.ToValue(raw);
                row[i] = v.Kind == ValueKind.Object ? Value.String(raw.ToString() ?? "") : v;
            }
            rows.Add(row);
        }
        DataTable? schema = null;
        try { schema = reader.GetSchemaTable(); } catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { }
        if (rows.Count == 0)
            for (int i = 0; i < n; i++) typeNames[i] = SafeTypeName(reader, i);
        var fields = new List<FieldDef>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < n; i++)
        {
            var name = FieldName(reader.GetName(i), i, used);
            int size = -1, precision = -1, scale = -1;
            if (schema != null && i < schema.Rows.Count)
            {
                var sr = schema.Rows[i];
                size = Int(sr, "ColumnSize");
                precision = Int(sr, "NumericPrecision");
                scale = Int(sr, "NumericScale");
                // The full declared type (with length and precision) when the provider can look it up.
                if (declaredType != null && Str(sr, "BaseTableName") is { Length: > 0 } bt && Str(sr, "BaseColumnName") is { Length: > 0 } bc
                    && declaredType(bt, bc) is { Length: > 0 } full)
                    observedNames[i] = full;
            }
            var declared = observedNames[i] ?? typeNames[i] ?? "";
            var type = observed[i] ?? clrTypes[i] ?? typeof(string);
            fields.Add(MapField(name, type, declared, size, precision, scale, rows.Select(r => r[i])) with { Nullable = true });
        }
        // Coerce values to the chosen field types (SQLite columns can hold mixed types).
        for (int i = 0; i < n; i++)
        {
            var f = fields[i].Normalize();
            foreach (var row in rows)
                if (!row[i].IsNull) row[i] = CoerceTo(f, row[i]);
        }
        return new ResultSet(fields, rows, reader.RecordsAffected);
    }

    private static string SafeTypeName(DbDataReader r, int i)
    {
        try { return r.GetDataTypeName(i) ?? ""; } catch (Exception) { return ""; }
    }

    private static string? Str(DataRow r, string col) =>
        r.Table.Columns.Contains(col) && r[col] is string s ? s : null;

    private static int Int(DataRow r, string col) =>
        r.Table.Columns.Contains(col) && r[col] is not DBNull && r[col] != null ? Convert.ToInt32(r[col]) : -1;

    private static string FieldName(string raw, int i, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var ch in raw) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        var name = sb.Length == 0 ? $"EXP_{i + 1}" : sb.ToString();
        if (char.IsDigit(name[0])) name = "_" + name;
        if (name.Length > 128) name = name[..128];
        var baseName = name;
        for (int k = 2; !used.Add(name); k++) name = baseName + "_" + k;
        return name;
    }

    /// <summary>
    /// ODBC-to-FoxPro type mapping: character data of known length ≤ 254 becomes C, longer or unbounded
    /// text becomes M, integers I, exact numerics N, money Y, floats B, bit L, dates D, timestamps T,
    /// binary data W.
    /// </summary>
    internal static FieldDef MapField(string name, Type type, string declared, int size, int precision, int scale, IEnumerable<Value> values)
    {
        var decl = declared.ToUpperInvariant().Trim();
        var nonNull = values.Where(v => !v.IsNull).ToList();
        if (FromDeclaredType(name, decl, size, nonNull) is { } byName) return byName;
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(DateTimeOffset))
        {
            if (decl.Contains("DATETIME") || decl.Contains("TIMESTAMP")) return new FieldDef(name, 'T');
            if (decl == "DATE") return new FieldDef(name, 'D');
            if (size is > 0 and <= 254) return new FieldDef(name, 'C', size);
            if (size > 254) return new FieldDef(name, 'M');
            var declaredLen = DeclaredLength(decl);
            if (declaredLen is > 0 and <= 254) return new FieldDef(name, 'C', declaredLen.Value);
            var max = nonNull.Count == 0 ? 1 : nonNull.Max(v => v.Kind == ValueKind.Character ? v.AsString.Length : Formatter.ToDisplay(v, new SetOptions()).Length);
            return max <= 254 && declaredLen == null && !decl.Contains("TEXT") && !decl.Contains("CLOB") ? new FieldDef(name, 'C', Math.Max(1, max)) : new FieldDef(name, 'M');
        }
        if (type == typeof(bool)) return new FieldDef(name, 'L');
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(sbyte) || type == typeof(ushort)) return new FieldDef(name, 'I');
        if (type == typeof(long) || type == typeof(uint) || type == typeof(ulong))
        {
            if (decl.Contains("BOOL") || decl == "BIT") return new FieldDef(name, 'L');
            if (decl is "DATE") return new FieldDef(name, 'D');
            // SQLite reports every INTEGER column as Int64; keep 32-bit values as Integer.
            if (nonNull.All(v => v.Kind == ValueKind.Number && Math.Abs(v.AsNumber) <= int.MaxValue) && !decl.Contains("BIGINT"))
                return new FieldDef(name, 'I');
            return new FieldDef(name, 'N', 20, 0); // TODO(oracle): VFP 9 maps bigint to Character(20) by default.
        }
        if (type == typeof(decimal))
        {
            if (decl.Contains("MONEY")) return new FieldDef(name, 'Y');
            if (precision is > 0 and < 255 && scale >= 0)
            {
                var w = Math.Min(20, precision + (scale > 0 ? 2 : 1));
                return new FieldDef(name, 'N', w, Math.Min(scale, Math.Max(0, w - 2)));
            }
            return new FieldDef(name, 'N', 20, 4);
        }
        if (type == typeof(double) || type == typeof(float))
        {
            var (p, s) = DeclaredPrecision(decl);
            if (p is > 0 and <= 20 && (decl.StartsWith("NUMERIC") || decl.StartsWith("DECIMAL")))
                return new FieldDef(name, 'N', Math.Min(20, p + (s > 0 ? 2 : 1)), s);
            int dec = 0;
            foreach (var v in nonNull.Where(v => v.Kind == ValueKind.Number))
            {
                var text = v.AsNumber.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
                var dot = text.IndexOf('.');
                if (dot >= 0) dec = Math.Max(dec, Math.Min(9, text.Length - dot - 1));
            }
            return new FieldDef(name, 'B', 8, dec);
        }
        if (type == typeof(DateTime)) return decl == "DATE" ? new FieldDef(name, 'D') : new FieldDef(name, 'T');
        if (type == typeof(DateOnly)) return new FieldDef(name, 'D');
        if (type == typeof(byte[])) return new FieldDef(name, 'W');
        return new FieldDef(name, 'M');
    }

    /// <summary>Maps a server type name (SQLite declared types, ODBC type names) when it is decisive.</summary>
    private static FieldDef? FromDeclaredType(string name, string decl, int size, List<Value> nonNull)
    {
        if (decl.Length == 0) return null;
        var paren = decl.IndexOf('(');
        var baseType = (paren >= 0 ? decl[..paren] : decl).Trim();
        var (p, s) = DeclaredPrecision(decl);
        switch (baseType)
        {
            case "BIGINT" or "INT8":
                return new FieldDef(name, 'N', 20, 0); // TODO(oracle): VFP 9 maps bigint to Character(20) by default.
            case "INT" or "INTEGER" or "SMALLINT" or "TINYINT" or "MEDIUMINT" or "INT2" or "INT4" or "SERIAL":
                return new FieldDef(name, 'I');
            case "NUMERIC" or "DECIMAL" or "NUMBER" when p > 0:
            {
                var w = Math.Min(20, p + (s > 0 ? 2 : 1));
                return new FieldDef(name, 'N', w, Math.Min(s, Math.Max(0, w - 2)));
            }
            case "MONEY" or "SMALLMONEY":
                return new FieldDef(name, 'Y');
            case "REAL" or "FLOAT" or "DOUBLE" or "DOUBLE PRECISION" or "FLOAT4" or "FLOAT8":
                return null; // decimals come from the data (CLR path)
            case "DATE":
                return new FieldDef(name, 'D');
            case "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "TIMESTAMP" or "DATETIMEOFFSET":
                return new FieldDef(name, 'T');
            case "BIT" or "BOOL" or "BOOLEAN":
                return new FieldDef(name, 'L');
            case "TEXT" or "NTEXT" or "CLOB" or "LONGTEXT" or "MEDIUMTEXT":
                return new FieldDef(name, 'M');
            case "BLOB" or "VARBINARY" or "BINARY" or "IMAGE" or "BYTEA" or "LONGBLOB":
                return new FieldDef(name, 'W');
            case "UNIQUEIDENTIFIER" or "UUID":
                return new FieldDef(name, 'C', 36);
            case "CHAR" or "VARCHAR" or "NCHAR" or "NVARCHAR" or "CHARACTER" or "VARYING CHARACTER" or "NATIVE CHARACTER" or "VARCHAR2" or "NVARCHAR2":
            {
                var len = decl.Contains("MAX") ? int.MaxValue : p > 0 ? p : size;
                if (len > 254) return new FieldDef(name, 'M');
                if (len > 0) return new FieldDef(name, 'C', len);
                var max = nonNull.Count == 0 ? 1 : nonNull.Max(v => v.Kind == ValueKind.Character ? v.AsString.Length : 10);
                return max <= 254 ? new FieldDef(name, 'C', Math.Max(1, max)) : new FieldDef(name, 'M');
            }
            default:
                return null;
        }
    }

    private static int? DeclaredLength(string decl)
    {
        var open = decl.IndexOf('(');
        if (open < 0) return null;
        var close = decl.IndexOf(')', open);
        return close > open && int.TryParse(decl[(open + 1)..close].Split(',')[0].Trim(), out var n) ? n : null;
    }

    private static (int Precision, int Scale) DeclaredPrecision(string decl)
    {
        var open = decl.IndexOf('(');
        var close = open >= 0 ? decl.IndexOf(')', open) : -1;
        if (close < 0) return (-1, 0);
        var parts = decl[(open + 1)..close].Split(',');
        int.TryParse(parts[0].Trim(), out var p);
        int s = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var ss) ? ss : 0;
        return (p, s);
    }

    private static Value CoerceTo(FieldDef f, Value v)
    {
        try
        {
            switch (f.Type)
            {
                case 'C' or 'M' when v.Kind != ValueKind.Character:
                    return Value.String(Formatter.ToDisplay(v, new SetOptions()).Trim());
                case 'L' when v.Kind == ValueKind.Number:
                    return Value.Logical(v.AsNumber != 0);
                case 'T' when v.Kind == ValueKind.Character:
                    return DateTime.TryParse(v.AsString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt) ? Value.DateTimeOf(dt) : Value.Null;
                case 'D' when v.Kind == ValueKind.Character:
                    return DateTime.TryParse(v.AsString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? Value.DateOf(DateOnly.FromDateTime(d)) : Value.Null;
                case 'D' when v.Kind == ValueKind.DateTime:
                    return Value.DateOf(DateOnly.FromDateTime(Julian.ToDateTime(v.JulianMs)));
                case 'N' or 'B' or 'I' when v.Kind == ValueKind.Character:
                    return double.TryParse(v.AsString, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num) ? Value.Number(num, f.Decimals) : Value.Null;
                case 'N' or 'B' when v.Kind == ValueKind.Currency:
                    return Value.Number((double)v.AsCurrency, f.Decimals);
                case 'N' or 'B' when v.Kind == ValueKind.Number:
                    return Value.Number(v.AsNumber, f.Decimals);
                case 'Y' when v.Kind == ValueKind.Number:
                    return Value.Currency((decimal)v.AsNumber);
                default:
                    return v;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { return Value.Null; }
    }

    /// <summary>Creates (or replaces) a cursor holding a result set, and selects it.</summary>
    internal void Materialize(string alias, ResultSet set)
    {
        var session = _rt.Session;
        var schema = new TableSchema(alias, set.Fields);
        var existing = session.FindAlias(alias);
        var area = existing?.Number ?? (session.Current.InUse ? session.FreeArea() : session.CurrentAreaNumber);
        session.Select(area);
        var wa = session.CreateCursor(schema, area);
        var fields = schema.Fields;
        foreach (var r in set.Rows)
        {
            var row = new Value[fields.Count];
            for (int i = 0; i < row.Length; i++) row[i] = r[i].IsNull ? Value.Null : fields[i].Coerce(r[i]);
            wa.Table.Append(row);
        }
        wa.GoTop();
    }

    internal static VfpException Connectivity(Exception ex)
    {
        var e = new VfpException(1526, "Connectivity error: " + ex.Message);
        e.Data["SqlState"] = ex is DbException { SqlState: { } st } ? st : ex is OdbcException oe && oe.Errors.Count > 0 ? oe.Errors[0].SQLState : null;
        e.Data["NativeError"] = ex is DbException de ? de.ErrorCode : 0;
        e.Data["OdbcMessage"] = ex.Message;
        return e;
    }
}
