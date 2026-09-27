using Microsoft.Data.Sqlite;

namespace JoePro.Data;

/// <summary>A storage error from the database engine (local SQLite or the Data Server).</summary>
public class StoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A write refused by a UNIQUE constraint (a candidate or primary key).</summary>
public sealed class StoreConstraintException(string message, Exception? inner = null) : StoreException(message, inner);

/// <summary>
/// What a <see cref="Store"/> needs from its database: statements with named parameters ($name), scalar results and
/// materialized rows (null for SQL NULL), and a counter that changes when another connection commits. The embedded
/// engine opens SQLite directly; in server mode the same calls go to the Joe Pro Data Server.
/// </summary>
public interface IStoreConnection : IDisposable
{
    /// <summary>Runs a statement (or several, separated by ;) and returns the number of rows changed.</summary>
    int Execute(string sql, (string Name, object? Value)[] args, bool prepared = false);
    object? Scalar(string sql, (string Name, object? Value)[] args, bool prepared = false);
    List<object?[]> Query(string sql, (string Name, object? Value)[] args, bool prepared = false);
    /// <summary>
    /// Streams rows to <paramref name="onRow"/> in one reused buffer (values may be DBNull; copy what you keep).
    /// Returns the number of rows.
    /// </summary>
    int QueryEach(string sql, (string Name, object? Value)[] args, bool prepared, Action<object?[]> onRow)
    {
        var rows = Query(sql, args, prepared);
        foreach (var r in rows) onRow(r);
        return rows.Count;
    }
    /// <summary>Changes when another connection commits to the database.</summary>
    long DataVersion();
    bool IsRemote { get; }
}

/// <summary>The embedded engine: one SQLite connection, with cached prepared statements for hot paths.</summary>
public sealed class SqliteStoreConnection : IStoreConnection
{
    private readonly Dictionary<string, SqliteCommand> _prepared = new();
    private SqliteCommand? _dataVersion;

    private SqliteStoreConnection(SqliteConnection connection) => Connection = connection;

    public SqliteConnection Connection { get; }
    public bool IsRemote => false;

    public static SqliteStoreConnection Open(string path, bool create)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        var conn = new SqliteConnection(cs);
        try { conn.Open(); }
        catch (SqliteException ex) { conn.Dispose(); throw new StoreException(ex.Message, ex); }
        var link = new SqliteStoreConnection(conn);
        if (path != ":memory:") link.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;", []);
        link.Execute("PRAGMA busy_timeout=5000;", []);
        return link;
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] args, bool prepared, out bool owned)
    {
        if (prepared && _prepared.TryGetValue(sql, out var cached))
        {
            foreach (var (n, v) in args) cached.Parameters[n].Value = v ?? DBNull.Value;
            owned = false;
            return cached;
        }
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        if (prepared) _prepared[sql] = cmd;
        owned = !prepared;
        return cmd;
    }

    private static Exception Wrap(SqliteException ex) =>
        ex.SqliteErrorCode == 19 ? new StoreConstraintException(ex.Message, ex) : new StoreException(ex.Message, ex);

    public int Execute(string sql, (string Name, object? Value)[] args, bool prepared = false)
    {
        var cmd = Command(sql, args, prepared, out var owned);
        try { return cmd.ExecuteNonQuery(); }
        catch (SqliteException ex) { throw Wrap(ex); }
        finally { if (owned) cmd.Dispose(); }
    }

    public object? Scalar(string sql, (string Name, object? Value)[] args, bool prepared = false)
    {
        var cmd = Command(sql, args, prepared, out var owned);
        try { return cmd.ExecuteScalar() is var v && v is not DBNull ? v : null; }
        catch (SqliteException ex) { throw Wrap(ex); }
        finally { if (owned) cmd.Dispose(); }
    }

    public List<object?[]> Query(string sql, (string Name, object? Value)[] args, bool prepared = false)
    {
        var cmd = Command(sql, args, prepared, out var owned);
        try
        {
            using var r = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            while (r.Read())
            {
                var row = new object?[r.FieldCount];
                for (int i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (SqliteException ex) { throw Wrap(ex); }
        finally { if (owned) cmd.Dispose(); }
    }

    public int QueryEach(string sql, (string Name, object? Value)[] args, bool prepared, Action<object?[]> onRow)
    {
        var cmd = Command(sql, args, prepared, out var owned);
        try
        {
            using var r = cmd.ExecuteReader();
            var buffer = new object?[r.FieldCount];
            int n = 0;
            while (r.Read())
            {
                r.GetValues(buffer!);
                onRow(buffer);
                n++;
            }
            return n;
        }
        catch (SqliteException ex) { throw Wrap(ex); }
        finally { if (owned) cmd.Dispose(); }
    }

    public long DataVersion()
    {
        _dataVersion ??= Connection.CreateCommand();
        _dataVersion.CommandText = "PRAGMA data_version";
        return (long)_dataVersion.ExecuteScalar()!;
    }

    public void Dispose()
    {
        foreach (var c in _prepared.Values) c.Dispose();
        _prepared.Clear();
        _dataVersion?.Dispose();
        Connection.Dispose();
    }
}
