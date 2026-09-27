using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using JoePro.Core;

namespace JoePro.Data.Remote;

/// <summary>
/// A Data Server location: joepro://[user[:password]@]host[:port]/database[?tls=1][&amp;trust=1]. Without a password
/// in the URL, JOEPRO_PASSWORD is used; without a user, JOEPRO_USER (or the operating system user).
/// </summary>
public sealed record DataServerAddress(string Host, int Port, string Database, string User, string? Password, bool Tls, bool TrustAnyCertificate)
{
    public const string Scheme = "joepro://";

    public static bool IsServerPath(string? s) => s != null && s.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    public static DataServerAddress Parse(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Scheme.Equals("joepro", StringComparison.OrdinalIgnoreCase) || u.Host.Length == 0)
            throw new VfpException(ErrorCodes.InvalidArgument, $"'{url}' is not a Data Server address (joepro://host[:port]/database).");
        var db = Uri.UnescapeDataString(u.AbsolutePath.Trim('/'));
        if (db.Length == 0) throw new VfpException(ErrorCodes.InvalidArgument, $"'{url}' names no database.");
        string? user = null, password = null;
        if (u.UserInfo.Length > 0)
        {
            var parts = u.UserInfo.Split(':', 2);
            user = Uri.UnescapeDataString(parts[0]);
            if (parts.Length == 2) password = Uri.UnescapeDataString(parts[1]);
        }
        var query = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : "1");
        return new DataServerAddress(u.Host, u.Port > 0 ? u.Port : Wire.DefaultPort, db,
            user ?? Environment.GetEnvironmentVariable("JOEPRO_USER") ?? Environment.UserName,
            password ?? Environment.GetEnvironmentVariable("JOEPRO_PASSWORD"),
            query.TryGetValue("tls", out var tls) && tls != "0", query.TryGetValue("trust", out var trust) && trust != "0");
    }

    /// <summary>The address as a path (without the password), as the database's Path shows it.</summary>
    public string Display => $"{Scheme}{Host}:{Port}/{Database}";
}

/// <summary>An error reported by the Data Server (authentication, permission, protocol).</summary>
public sealed class DataServerException(string message, string kind) : VfpException(1526, message)
{
    public string Kind { get; } = kind;
}

/// <summary>
/// One connection to a Data Server, shared by the databases a process opens there as the same user. Requests are
/// answered in order by id; change notifications arrive as events; a heartbeat keeps the session's lock leases alive.
/// </summary>
public sealed class DataServerClient : IDisposable
{
    private static readonly ConcurrentDictionary<string, DataServerClient> Pool = new(StringComparer.OrdinalIgnoreCase);

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _heartbeat;
    private long _nextId;
    private int _users;
    private string? _poolKey;

    private DataServerClient(TcpClient tcp, Stream stream, TimeSpan heartbeat)
    {
        _tcp = tcp;
        _stream = stream;
        _ = Task.Run(ReadLoop);
        _heartbeat = new Timer(_ => { try { Send("ping", new JsonObject(), TimeSpan.FromSeconds(10)); } catch (Exception) { } }, null, heartbeat, heartbeat);
    }

    public bool Connected => !_cts.IsCancellationRequested;
    public long SessionId { get; private set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Raised on the reader thread for server events ({"event": "changed", "db": …, "version": …}).</summary>
    public event Action<JsonObject>? Event;

    /// <summary>A pooled, authenticated connection for this address's server and user.</summary>
    public static DataServerClient Get(DataServerAddress a)
    {
        var key = $"{a.Host}:{a.Port}|{a.User}|{a.Tls}";
        lock (Pool)
        {
            if (Pool.TryGetValue(key, out var existing) && existing.Connected)
            {
                existing._users++;
                return existing;
            }
            var client = Connect(a);
            client._poolKey = key;
            client._users = 1;
            Pool[key] = client;
            return client;
        }
    }

    public static DataServerClient Connect(DataServerAddress a)
    {
        var tcp = new TcpClient { NoDelay = true };
        try { tcp.Connect(a.Host, a.Port); }
        catch (SocketException ex) { tcp.Dispose(); throw new VfpException(1526, $"Connectivity error: cannot reach the Data Server at {a.Host}:{a.Port} ({ex.Message})."); }
        Stream stream = tcp.GetStream();
        if (a.Tls)
        {
            var ssl = new SslStream(stream, false, a.TrustAnyCertificate ? (_, _, _, _) => true : null);
            ssl.AuthenticateAsClient(a.Host);
            stream = ssl;
        }
        var client = new DataServerClient(tcp, stream, TimeSpan.FromSeconds(10));
        try
        {
            var hello = client.Send("hello", new JsonObject { ["user"] = a.User, ["password"] = a.Password ?? "", ["client"] = "joepro" });
            client.SessionId = (long)hello["session"]!;
            if (hello["lease"] is JsonValue lease)
            {
                var every = TimeSpan.FromSeconds(Math.Max(1, lease.GetValue<double>() / 3));
                client._heartbeat.Change(every, every);
            }
        }
        catch
        {
            client.Close();
            throw;
        }
        return client;
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var m = await Wire.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                if (m == null) break;
                if (m["event"] != null)
                {
                    try { Event?.Invoke(m); } catch (Exception) { }
                    continue;
                }
                if (m["id"] is JsonValue id && _pending.TryRemove(id.GetValue<long>(), out var tcs)) tcs.TrySetResult(m);
            }
        }
        catch (Exception) when (!_cts.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
        finally
        {
            _cts.Cancel();
            foreach (var p in _pending.Values) p.TrySetException(new VfpException(1526, "Connectivity error: the connection to the Data Server was lost."));
            _pending.Clear();
        }
    }

    /// <summary>Sends a request and waits for its answer. Errors come back as exceptions (StoreException for SQL errors).</summary>
    public JsonObject Send(string op, JsonObject payload, TimeSpan? timeout = null)
    {
        if (_cts.IsCancellationRequested) throw new VfpException(1526, "Connectivity error: the connection to the Data Server was lost.");
        var id = Interlocked.Increment(ref _nextId);
        payload["id"] = id;
        payload["op"] = op;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            Wire.WriteAsync(_stream, payload, _writeGate, _cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            _cts.Cancel();
            throw new VfpException(1526, "Connectivity error: the connection to the Data Server was lost.");
        }
        if (!tcs.Task.Wait(timeout ?? Timeout))
        {
            _pending.TryRemove(id, out _);
            throw new VfpException(1526, $"Connectivity error: the Data Server did not answer {op} in time.");
        }
        var r = tcs.Task.GetAwaiter().GetResult();
        if (r["ok"]?.GetValue<bool>() == true) return r;
        var message = (string?)r["error"] ?? "The Data Server refused the request.";
        var kind = (string?)r["kind"] ?? "error";
        throw kind switch
        {
            "constraint" => new StoreConstraintException(message),
            "store" => new StoreException(message),
            _ => new DataServerException(message, kind),
        };
    }

    /// <summary>Gives a pooled connection back; the last user closes it.</summary>
    public void Release()
    {
        lock (Pool)
        {
            if (--_users > 0) return;
            if (_poolKey != null) Pool.TryRemove(new KeyValuePair<string, DataServerClient>(_poolKey, this));
        }
        Close();
    }

    public void Close()
    {
        _heartbeat.Dispose();
        _cts.Cancel();
        try { _stream.Dispose(); } catch (IOException) { }
        _tcp.Dispose();
    }

    public void Dispose() => Release();
}

/// <summary>A database on a Data Server, as the engine's connection: statements run on the server's connection for this handle.</summary>
public sealed class RemoteStoreConnection : IStoreConnection
{
    private readonly DataServerClient _client;
    private long _version;
    private bool _closed;

    private RemoteStoreConnection(DataServerClient client, long handle, string database)
    {
        _client = client;
        Handle = handle;
        Database = database;
        _client.Event += OnEvent;
    }

    public long Handle { get; }
    public string Database { get; }
    public bool IsRemote => true;
    public DataServerClient Client => _client;

    public static RemoteStoreConnection Open(DataServerAddress a)
    {
        var client = DataServerClient.Get(a);
        try
        {
            var r = client.Send("open", new JsonObject { ["db"] = a.Database });
            var conn = new RemoteStoreConnection(client, (long)r["h"]!, (string)r["name"]!);
            conn._version = (long?)r["version"] ?? 0;
            return conn;
        }
        catch
        {
            client.Release();
            throw;
        }
    }

    private void OnEvent(JsonObject e)
    {
        if ((string?)e["event"] == "changed" && string.Equals((string?)e["db"], Database, StringComparison.OrdinalIgnoreCase) && e["version"] is JsonValue v)
        {
            var nv = v.GetValue<long>();
            long cur;
            while ((cur = Interlocked.Read(ref _version)) < nv && Interlocked.CompareExchange(ref _version, nv, cur) != cur) { }
            Changed?.Invoke();
        }
    }

    /// <summary>Raised (on a background thread) when another client commits to this database.</summary>
    public event Action? Changed;

    private JsonObject Payload(string sql, (string Name, object? Value)[] args, bool prepared) =>
        new() { ["h"] = Handle, ["sql"] = sql, ["args"] = Wire.Args(args), ["p"] = prepared };

    public int Execute(string sql, (string Name, object? Value)[] args, bool prepared = false) =>
        (int)(long)_client.Send("exec", Payload(sql, args, prepared))["result"]!;

    public object? Scalar(string sql, (string Name, object? Value)[] args, bool prepared = false) =>
        Wire.FromJson(_client.Send("scalar", Payload(sql, args, prepared))["result"]);

    public List<object?[]> Query(string sql, (string Name, object? Value)[] args, bool prepared = false) =>
        Wire.Rows(_client.Send("query", Payload(sql, args, prepared))["result"]);

    public long DataVersion() => Interlocked.Read(ref _version);

    // ---- Locks (leases held by this client's session on the server) ----------------------------------

    public bool TryLock(string table, int recNo, string owner) =>
        _client.Send("lock", new JsonObject { ["h"] = Handle, ["table"] = table, ["recno"] = recNo, ["owner"] = owner })["result"]!.GetValue<bool>();

    public void Unlock(string table, int? recNo, string owner) =>
        _client.Send("unlock", new JsonObject { ["h"] = Handle, ["table"] = table, ["recno"] = recNo, ["owner"] = owner });

    public bool IsLocked(string table, int recNo) =>
        _client.Send("islocked", new JsonObject { ["h"] = Handle, ["table"] = table, ["recno"] = recNo })["result"]!.GetValue<bool>();

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _client.Event -= OnEvent;
        try { if (_client.Connected) _client.Send("close", new JsonObject { ["h"] = Handle }, TimeSpan.FromSeconds(5)); }
        catch (VfpException) { }
        _client.Release();
    }
}
