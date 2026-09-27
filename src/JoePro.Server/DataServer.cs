using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using JoePro.Data;
using JoePro.Data.Remote;
using Microsoft.Data.Sqlite;

namespace JoePro.Server;

/// <summary>
/// The Joe Pro Data Server: owns the database files and serves them to clients over TCP (TLS with a certificate).
/// Each client session authenticates, opens databases it has permission for, and runs the engine's statements on its
/// own SQLite connection per database (WAL: one writer at a time, many readers). Locks are server-side leases;
/// commits push change notifications to the other clients; admins can back up, check and list sessions and locks.
/// </summary>
public sealed class DataServer : IAsyncDisposable
{
    private readonly ServerConfig _config;
    private readonly string _baseDir;
    private readonly ConcurrentDictionary<long, ClientSession> _sessions = new();
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private X509Certificate2? _certificate;
    private Timer? _sweeper;
    private long _nextSession;

    public DataServer(ServerConfig config, string baseDir)
    {
        _config = config;
        _baseDir = baseDir;
    }

    public LeaseLockManager Locks { get; } = new();
    public int Port { get; private set; }
    public IReadOnlyCollection<ClientSession> Sessions => _sessions.Values.ToList();
    public event Action<string>? Log;

    internal void Write(string message) => Log?.Invoke($"{DateTime.Now:HH:mm:ss} {message}");

    public string DatabasePath(string name) =>
        _config.Databases.TryGetValue(name, out var p) ? Path.GetFullPath(Path.Combine(_baseDir, p)) : throw new KeyNotFoundException(name);

    public void Start()
    {
        if (_config.Certificate is { Length: > 0 } cert)
            _certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(_baseDir, cert), _config.CertificatePassword);
        _listener = new TcpListener(IPAddress.Parse(_config.Listen), _config.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
        var every = TimeSpan.FromSeconds(Math.Max(1, _config.LeaseSeconds / 3));
        _sweeper = new Timer(_ => SweepLeases(), null, every, every);
        Write($"Joe Pro Data Server listening on {_config.Listen}:{Port}{(_certificate != null ? " (TLS)" : "")}, {_config.Databases.Count} database(s).");
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) when (_cts.IsCancellationRequested) { return; }
            catch (SocketException) { continue; }
            tcp.NoDelay = true;
            _ = Task.Run(() => Serve(tcp));
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        Stream stream = tcp.GetStream();
        var id = Interlocked.Increment(ref _nextSession);
        var endpoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        ClientSession? session = null;
        try
        {
            if (_certificate != null)
            {
                var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(_certificate, false, false);
                stream = ssl;
            }
            session = new ClientSession(this, id, stream, endpoint);
            _sessions[id] = session;
            await session.Run(_cts.Token);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or InvalidDataException or ObjectDisposedException) { }
        finally
        {
            _sessions.TryRemove(id, out _);
            session?.Dispose();
            var released = Locks.ReleaseSession(id);
            if (session?.User != null) Write($"{session.User}@{endpoint} disconnected{(released > 0 ? $"; {released} lock(s) released" : "")}.");
            tcp.Dispose();
        }
    }

    /// <summary>Ends sessions that stopped renewing their lease (a crashed or hung client): their locks are released.</summary>
    private void SweepLeases()
    {
        var limit = DateTime.UtcNow - TimeSpan.FromSeconds(_config.LeaseSeconds);
        foreach (var s in _sessions.Values.Where(s => s.LastSeen < limit).ToList())
        {
            Write($"Lease expired for {s.User ?? "?"}@{s.Endpoint}; closing the session.");
            Locks.ReleaseSession(s.Id);
            s.Abort();
        }
    }

    internal ServerConfig Config => _config;

    internal long Version(string db) => _versions.GetValueOrDefault(db);

    /// <summary>A commit changed a database: other sessions using it are told.</summary>
    internal void Committed(string db, long fromSession)
    {
        var v = _versions.AddOrUpdate(db, 1, (_, x) => x + 1);
        var e = new JsonObject { ["event"] = "changed", ["db"] = db, ["version"] = v };
        foreach (var s in _sessions.Values)
            if (s.Id != fromSession && s.Uses(db)) s.Push(e);
    }

    // ---- Administration --------------------------------------------------------------------------

    /// <summary>An online backup of a database (consistent while clients keep working).</summary>
    public void Backup(string db, string destination)
    {
        var dest = Path.GetFullPath(Path.Combine(_baseDir, destination));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(dest)) File.Delete(dest);
        BackupFile(DatabasePath(db), dest);
        Write($"Backed up {db} to {dest}.");
    }

    public static void BackupFile(string source, string destination)
    {
        using var from = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var to = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        from.Open();
        to.Open();
        from.BackupDatabase(to);
    }

    /// <summary>Checks a database: SQLite's integrity check plus the Joe Pro catalog (tables, fields and index columns exist).</summary>
    public List<string> Check(string db) => CheckFile(DatabasePath(db));

    public static List<string> CheckFile(string path)
    {
        var problems = new List<string>();
        using var link = SqliteStoreConnection.Open(path, create: false, readOnly: true);
        foreach (var r in link.Query("PRAGMA integrity_check", []))
            if (r[0] as string is { } msg && msg != "ok") problems.Add("SQLite: " + msg);
        var sqlTables = link.Query("SELECT name FROM sqlite_master WHERE type='table'", []).Select(r => (string)r[0]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in new[] { "_jp_meta", "_jp_tables", "_jp_fields", "_jp_tags", "_jp_relations" })
            if (!sqlTables.Contains(t)) problems.Add($"The catalog table {t} is missing.");
        if (problems.Count > 0) return problems;
        foreach (var t in link.Query("SELECT name, sqlname FROM _jp_tables", []))
        {
            var (name, sql) = ((string)t[0]!, (string)t[1]!);
            if (!sqlTables.Contains(sql)) { problems.Add($"Table {name}: its data table {sql} is missing."); continue; }
            var columns = link.Query($"SELECT name FROM pragma_table_info('{sql.Replace("'", "''")}')", []).Select(r => (string)r[0]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in link.Query("SELECT name FROM _jp_fields WHERE tbl=$t", [("$t", name)]))
                if (!columns.Contains("c_" + ((string)f[0]!).ToLowerInvariant())) problems.Add($"Table {name}: field {f[0]} has no column.");
            foreach (var k in link.Query("SELECT name, keycol FROM _jp_tags WHERE tbl=$t", [("$t", name)]))
                if (!columns.Contains((string)k[1]!)) problems.Add($"Table {name}: index {k[0]} has no key column.");
        }
        foreach (var r in link.Query("SELECT parent, child FROM _jp_relations", []))
            foreach (var t in new[] { (string)r[0]!, (string)r[1]! })
                if (link.Query("SELECT 1 FROM _jp_tables WHERE name=$n", [("$n", t)]).Count == 0) problems.Add($"A relation names table {t}, which does not exist.");
        return problems;
    }

    public JsonObject Status()
    {
        var sessions = new JsonArray();
        foreach (var s in _sessions.Values.OrderBy(s => s.Id))
            sessions.Add(new JsonObject
            {
                ["session"] = s.Id, ["user"] = s.User, ["endpoint"] = s.Endpoint, ["since"] = s.Connected.ToString("O"),
                ["lastSeen"] = s.LastSeen.ToString("O"), ["databases"] = new JsonArray(s.Databases.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
                ["requests"] = s.Requests,
            });
        var locks = new JsonArray();
        foreach (var (db, table, recNo, session, owner) in Locks.Snapshot())
            locks.Add(new JsonObject { ["db"] = db, ["table"] = table, ["recno"] = recNo, ["session"] = session, ["owner"] = owner });
        return new JsonObject { ["sessions"] = sessions, ["locks"] = locks, ["databases"] = new JsonArray(_config.Databases.Keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()) };
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _sweeper?.Dispose();
        _listener?.Stop();
        foreach (var s in _sessions.Values) s.Abort();
        await Task.Delay(50);
        _certificate?.Dispose();
    }
}

/// <summary>One connected client: its user, its open database handles (a SQLite connection each), its lease.</summary>
public sealed class ClientSession : IDisposable
{
    // Statements that could reach outside the served database files.
    private static readonly Regex Forbidden = new(@"\b(ATTACH|DETACH)\b|\bVACUUM\s+INTO\b|\bload_extension\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly DataServer _server;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<long, (string Db, SqliteStoreConnection Link)> _handles = new();
    private readonly CancellationTokenSource _abort = new();
    private ServerUser? _user;
    private long _nextHandle;

    internal ClientSession(DataServer server, long id, Stream stream, string endpoint)
    {
        _server = server;
        Id = id;
        _stream = stream;
        Endpoint = endpoint;
    }

    public long Id { get; }
    public string Endpoint { get; }
    public string? User { get; private set; }
    public DateTime Connected { get; } = DateTime.UtcNow;
    public DateTime LastSeen { get; private set; } = DateTime.UtcNow;
    public long Requests { get; private set; }
    public IReadOnlyList<string> Databases { get { lock (_handles) return _handles.Values.Select(h => h.Db).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); } }

    internal bool Uses(string db) { lock (_handles) return _handles.Values.Any(h => h.Db.Equals(db, StringComparison.OrdinalIgnoreCase)); }

    internal void Push(JsonObject e)
    {
        try { _ = Wire.WriteAsync(_stream, (JsonObject)e.DeepClone(), _writeGate, _abort.Token); }
        catch (Exception) { }
    }

    internal void Abort()
    {
        _abort.Cancel();
        try { _stream.Dispose(); } catch (IOException) { }
    }

    internal async Task Run(CancellationToken serverToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken, _abort.Token);
        while (!linked.IsCancellationRequested)
        {
            var m = await Wire.ReadAsync(_stream, linked.Token);
            if (m == null) return;
            LastSeen = DateTime.UtcNow;
            Requests++;
            var reply = new JsonObject { ["id"] = m["id"]?.DeepClone() };
            try
            {
                var result = Handle((string?)m["op"] ?? "", m, out var extra);
                reply["ok"] = true;
                if (result != null) reply["result"] = result;
                if (extra != null) foreach (var (k, v) in extra.ToList()) { extra.Remove(k); reply[k] = v; }
            }
            catch (StoreConstraintException ex) { reply["ok"] = false; reply["error"] = ex.Message; reply["kind"] = "constraint"; }
            catch (StoreException ex) { reply["ok"] = false; reply["error"] = ex.Message; reply["kind"] = "store"; }
            catch (DeniedException ex) { reply["ok"] = false; reply["error"] = ex.Message; reply["kind"] = ex.Kind; }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException or IOException or InvalidCastException)
            { reply["ok"] = false; reply["error"] = ex.Message; reply["kind"] = "error"; }
            await Wire.WriteAsync(_stream, reply, _writeGate, linked.Token);
            if (_user == null && reply["ok"]?.GetValue<bool>() != true) return;   // failed hello: hang up
        }
    }

    private sealed class DeniedException(string message, string kind) : Exception(message) { public string Kind { get; } = kind; }

    private (string Db, SqliteStoreConnection Link) HandleOf(JsonObject m)
    {
        var h = (long?)m["h"] ?? throw new ArgumentException("A handle is required.");
        lock (_handles) return _handles.TryGetValue(h, out var x) ? x : throw new KeyNotFoundException($"Handle {h} is not open.");
    }

    private JsonNode? Handle(string op, JsonObject m, out JsonObject? extra)
    {
        extra = null;
        if (op != "hello" && _user == null) throw new DeniedException("Sign in first.", "auth");
        switch (op)
        {
            case "hello":
            {
                var name = (string?)m["user"] ?? "";
                _user = _server.Config.Authenticate(name, (string?)m["password"] ?? "") ?? throw new DeniedException("The user name or password is not valid.", "auth");
                User = name;
                _server.Write($"{name}@{Endpoint} signed in (session {Id}).");
                extra = new JsonObject { ["session"] = Id, ["lease"] = _server.Config.LeaseSeconds };
                return null;
            }
            case "ping":
                return null;
            case "open":
            {
                var db = (string?)m["db"] ?? throw new ArgumentException("A database name is required.");
                if (!_server.Config.Databases.ContainsKey(db)) throw new DeniedException($"The server has no database {db}.", "notfound");
                var permission = _server.Config.PermissionOf(_user!, db);
                if (permission == Permission.None) throw new DeniedException($"{User} has no permission for database {db}.", "permission");
                var link = SqliteStoreConnection.Open(_server.DatabasePath(db), create: false, readOnly: permission == Permission.Read);
                SQLitePCL.raw.sqlite3_limit(link.Connection.Handle, SQLitePCL.raw.SQLITE_LIMIT_ATTACHED, 0);
                var h = Interlocked.Increment(ref _nextHandle);
                lock (_handles) _handles[h] = (db, link);
                extra = new JsonObject { ["h"] = h, ["name"] = db.ToUpperInvariant(), ["version"] = _server.Version(db), ["permission"] = permission.ToString().ToLowerInvariant() };
                return null;
            }
            case "close":
            {
                var h = (long?)m["h"] ?? 0;
                (string Db, SqliteStoreConnection Link) x;
                lock (_handles) { if (!_handles.Remove(h, out x)) return null; }
                x.Link.Dispose();
                return null;
            }
            case "exec" or "scalar" or "query" or "dataversion":
            {
                var (db, link) = HandleOf(m);
                if (op == "dataversion") return JsonValue.Create(_server.Version(db));
                var sql = (string?)m["sql"] ?? "";
                if (Forbidden.IsMatch(sql)) throw new DeniedException("The statement is not allowed on the Data Server.", "permission");
                var args = Wire.Args(m["args"]);
                var prepared = m["p"]?.GetValue<bool>() == true;
                var before = SQLitePCL.raw.sqlite3_total_changes(link.Connection.Handle);
                JsonNode? result = op switch
                {
                    "exec" => JsonValue.Create((long)link.Execute(sql, args, prepared)),
                    "scalar" => Wire.ToJson(link.Scalar(sql, args, prepared)),
                    _ => Wire.Rows(link.Query(sql, args, prepared)),
                };
                // A commit (autocommit mode after changes) tells the other clients.
                if (op != "query" && SQLitePCL.raw.sqlite3_total_changes(link.Connection.Handle) != before && SQLitePCL.raw.sqlite3_get_autocommit(link.Connection.Handle) != 0)
                    _server.Committed(db, Id);
                else if (op != "query" && sql.TrimStart().StartsWith("RELEASE", StringComparison.OrdinalIgnoreCase) && SQLitePCL.raw.sqlite3_get_autocommit(link.Connection.Handle) != 0)
                    _server.Committed(db, Id);
                return result;
            }
            case "lock":
            {
                var (db, _) = HandleOf(m);
                return JsonValue.Create(_server.Locks.TryLock(db, (string)m["table"]!, (int)(long)m["recno"]!, Id, (string?)m["owner"] ?? ""));
            }
            case "unlock":
            {
                var (db, _) = HandleOf(m);
                _server.Locks.Unlock(db, (string)m["table"]!, m["recno"] is JsonValue r ? (int)r.GetValue<long>() : null, Id, (string?)m["owner"] ?? "");
                return null;
            }
            case "islocked":
            {
                var (db, _) = HandleOf(m);
                return JsonValue.Create(_server.Locks.IsLocked(db, (string)m["table"]!, (int)(long)m["recno"]!));
            }
            case "status":
                RequireAdmin(null);
                return _server.Status();
            case "backup":
            {
                var db = (string)m["db"]!;
                RequireAdmin(db);
                _server.Backup(db, (string)m["to"]!);
                return null;
            }
            case "check":
            {
                var db = (string)m["db"]!;
                RequireAdmin(db);
                return new JsonArray(_server.Check(db).Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
            }
            default:
                throw new ArgumentException($"Unknown request {op}.");
        }
    }

    private void RequireAdmin(string? db)
    {
        if (_user!.Admin || (db != null && _server.Config.PermissionOf(_user, db) == Permission.Admin)) return;
        throw new DeniedException($"{User} is not an administrator.", "permission");
    }

    public void Dispose()
    {
        lock (_handles)
        {
            foreach (var (_, link) in _handles.Values) link.Dispose();   // open transactions roll back
            _handles.Clear();
        }
        _abort.Cancel();
    }
}
