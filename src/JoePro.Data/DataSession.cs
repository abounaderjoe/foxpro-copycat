using JoePro.Core;

namespace JoePro.Data;

/// <summary>
/// A data session: an isolated set of work areas, open databases and SET options.
/// Forms with DataSession = 2 get their own session, like in VFP.
/// </summary>
public sealed class DataSession : IDisposable
{
    public const int MaxWorkAreas = 32767;
    private static int _nextId;

    private readonly Dictionary<int, WorkArea> _areas = new();
    private readonly Dictionary<string, Store> _stores = new(StringComparer.OrdinalIgnoreCase);
    private Store? _cursorStore;
    private int _cursorSeq;

    public DataSession(SetOptions? options = null, IExpressionHost? host = null)
    {
        Uid = Interlocked.Increment(ref _nextId);
        Id = Uid;
        Options = options ?? new SetOptions();
        ExpressionHost = host;
    }

    /// <summary>The data session number programs see (SET DATASESSION, DataSessionId); the interpreter numbers its sessions from 1.</summary>
    public int Id { get; set; }
    /// <summary>Unique across the process (lock ownership).</summary>
    public int Uid { get; }
    public SetOptions Options { get; }
    public IExpressionHost? ExpressionHost { get; set; }
    public LockManager Locks { get; } = LockManager.Process;
    public int CurrentAreaNumber { get; private set; } = 1;
    public WorkArea Current => Area(CurrentAreaNumber);
    /// <summary>The current database (SET DATABASE TO), or null.</summary>
    public Store? CurrentDatabase { get; private set; }
    public IEnumerable<Store> OpenDatabases => _stores.Values.Where(s => s.Kind == StoreKind.Database);
    public int TransactionLevel { get; private set; }

    public WorkArea Area(int n)
    {
        if (n < 1 || n > MaxWorkAreas) throw VfpException.InvalidArgument();
        if (!_areas.TryGetValue(n, out var wa)) _areas[n] = wa = new WorkArea(this, n);
        return wa;
    }

    public IEnumerable<WorkArea> OpenWorkAreas() => _areas.Values.Where(a => a.InUse).OrderBy(a => a.Number);

    public WorkArea? FindAlias(string alias) =>
        _areas.Values.FirstOrDefault(a => a.InUse && string.Equals(a.Alias, alias, StringComparison.OrdinalIgnoreCase));

    public WorkArea ResolveAlias(string alias) => FindAlias(alias) ?? throw VfpException.AliasNotFound(alias);

    /// <summary>SELECT n | alias. SELECT 0 picks the lowest unused work area.</summary>
    public WorkArea Select(int n)
    {
        CurrentAreaNumber = n == 0 ? FreeArea() : n;
        return Current;
    }

    public WorkArea Select(string alias)
    {
        var wa = ResolveAlias(alias);
        CurrentAreaNumber = wa.Number;
        return wa;
    }

    public int FreeArea(int? except = null)
    {
        for (int i = 1; i <= MaxWorkAreas; i++)
            if (i != except && (!_areas.TryGetValue(i, out var wa) || !wa.InUse)) return i;
        throw new VfpException(ErrorCodes.InvalidArgument, "No free work area.");
    }

    // ---- Databases --------------------------------------------------------------------

    /// <summary>Resolves a file name against SET DEFAULT and SET PATH, ignoring case (FoxPro file names are case-insensitive).</summary>
    public string ResolvePath(string file, string defaultExt)
    {
        if (!Path.HasExtension(file)) file += defaultExt;
        if (Path.IsPathRooted(file)) return FindIgnoringCase(file) ?? file;
        var candidate = Path.Combine(Options.Default_, file);
        if (FindIgnoringCase(candidate) is { } hit) return hit;
        foreach (var dir in Options.Path)
        {
            var p = Path.Combine(Path.IsPathRooted(dir) ? dir : Path.Combine(Options.Default_, dir), file);
            if (FindIgnoringCase(p) is { } h) return h;
        }
        return Path.Combine(Path.GetDirectoryName(candidate) ?? ".", Path.GetFileName(candidate).ToLowerInvariant());
    }

    /// <summary>
    /// Finds a file whose path matches <paramref name="path"/> ignoring case in every segment (FoxPro code written
    /// on Windows says "..\lib\Base.vcx" for a folder named "Lib"). Returns null if there is no such file.
    /// </summary>
    public static string? FindIgnoringCase(string path)
    {
        if (File.Exists(path)) return path;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (File.Exists(full)) return full;
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir)) return null;
        var realDir = Directory.Exists(dir) ? dir : FindDirectoryIgnoringCase(dir);
        if (realDir == null) return null;
        var name = Path.GetFileName(full);
        return Directory.EnumerateFiles(realDir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds a directory ignoring case in every segment of an absolute path.</summary>
    public static string? FindDirectoryIgnoringCase(string dir)
    {
        if (Directory.Exists(dir)) return dir;
        var parent = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(parent) || parent == dir) return null;
        var realParent = FindDirectoryIgnoringCase(parent);
        if (realParent == null) return null;
        var name = Path.GetFileName(dir);
        try
        {
            return Directory.EnumerateDirectories(realParent).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    private Store GetStore(string path)
    {
        path = Path.GetFullPath(path);
        if (!_stores.TryGetValue(path, out var s))
        {
            s = Store.Open(path);
            for (int i = 0; i < TransactionLevel; i++) s.BeginTransaction();
            _stores[path] = s;
        }
        return s;
    }

    public Store CreateDatabase(string name)
    {
        var path = Path.GetFullPath(ResolvePath(name, Store.DatabaseExtension));
        var s = Store.Create(path, StoreKind.Database);
        _stores[path] = s;
        CurrentDatabase = s;
        return s;
    }

    /// <summary>
    /// Databases that live on a Data Server: joepro-data.json in the default folder maps names to addresses
    /// ({"databases": {"sales": "joepro://server/sales"}}), so OPEN DATABASE sales works unchanged in server mode.
    /// </summary>
    public string? ServerAddressOf(string name)
    {
        if (Remote.DataServerAddress.IsServerPath(name)) return name;
        var file = Path.Combine(Options.Default_, "joepro-data.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("databases", out var dbs)) return null;
            var bare = Path.GetFileNameWithoutExtension(name.Trim('"', '\''));
            foreach (var p in dbs.EnumerateObject())
                if (p.Name.Equals(bare, StringComparison.OrdinalIgnoreCase) && p.Value.GetString() is { } url && Remote.DataServerAddress.IsServerPath(url)) return url;
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    public Store OpenDatabase(string name)
    {
        if (ServerAddressOf(name) is { } url)
        {
            var address = Remote.DataServerAddress.Parse(url);
            if (!_stores.TryGetValue(address.Display, out var remote))
            {
                remote = Store.Attach(address.Display, Remote.RemoteStoreConnection.Open(address), address.Database);
                for (int i = 0; i < TransactionLevel; i++) remote.BeginTransaction();
                _stores[address.Display] = remote;
            }
            if (remote.Kind != StoreKind.Database) throw new VfpException(ErrorCodes.FileAccessDenied, $"'{name}' is not a database.");
            CurrentDatabase = remote;
            return remote;
        }
        var path = ResolvePath(name, Store.DatabaseExtension);
        if (!File.Exists(path)) throw VfpException.FileNotFound(Path.GetFileName(path));
        var s = GetStore(path);
        if (s.Kind != StoreKind.Database) throw new VfpException(ErrorCodes.FileAccessDenied, $"'{name}' is not a database.");
        CurrentDatabase = s;
        return s;
    }

    public void SetDatabase(string? name)
    {
        if (string.IsNullOrEmpty(name)) { CurrentDatabase = null; return; }
        CurrentDatabase = OpenDatabases.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new VfpException(ErrorCodes.FileDoesNotExist, $"Database {name} is not open.", name);
    }

    public void CloseDatabase(Store db)
    {
        foreach (var wa in OpenWorkAreas().Where(w => w.Table.Store == db).ToList()) wa.Close();
        _stores.Remove(db.Path);
        db.Dispose();
        if (CurrentDatabase == db) CurrentDatabase = OpenDatabases.FirstOrDefault();
    }

    // ---- Tables -----------------------------------------------------------------------

    /// <summary>
    /// CREATE TABLE: adds the table to the current database, or creates a free table (.jpt)
    /// when no database is current (or <paramref name="free"/> is set).
    /// </summary>
    public Table CreateTable(TableSchema schema, bool free = false, string? path = null)
    {
        if (CurrentDatabase != null && !free) return CurrentDatabase.CreateTable(schema, ExpressionHost);
        var file = Path.GetFullPath(path ?? ResolvePath(schema.Name, Store.FreeTableExtension));
        if (File.Exists(file))
        {
            if (Options.Safety) throw new VfpException(ErrorCodes.FileInUse, $"File '{Path.GetFileName(file)}' already exists.");
            if (_stores.Remove(file, out var old)) old.Dispose();
            File.Delete(file);
        }
        var store = Store.Create(file, StoreKind.FreeTable);
        _stores[file] = store;
        var named = new TableSchema(Path.GetFileNameWithoutExtension(file), schema.Fields);
        named.Tags.AddRange(schema.Tags);
        return store.CreateTable(named, ExpressionHost);
    }

    /// <summary>The connection string of a named connection in an open database, or null.</summary>
    public string? NamedConnection(string name)
    {
        var (db, bare) = SplitDatabaseName(name);
        foreach (var store in db != null ? new[] { db } : OpenDatabases.OrderBy(d => d == CurrentDatabase ? 0 : 1).ToArray())
            if (store.GetConnection(bare) is { } c) return c.BuildConnectString();
        return null;
    }

    /// <summary>Finds a view by name ("db!view" names a specific database); the current database is searched first.</summary>
    public (Store Database, ViewDefinition View)? FindView(string name)
    {
        var (db, bare) = SplitDatabaseName(name);
        if (bare.Contains('.') || bare.Contains(Path.DirectorySeparatorChar)) return null;
        foreach (var store in db != null ? new[] { db } : OpenDatabases.OrderBy(d => d == CurrentDatabase ? 0 : 1).ToArray())
            if (store.GetView(bare) is { } v) return (store, v);
        return null;
    }

    private (Store? Db, string Name) SplitDatabaseName(string name)
    {
        var bang = name.IndexOf('!');
        if (bang <= 0) return (null, name.Trim());
        var db = OpenDatabases.FirstOrDefault(d => string.Equals(d.Name, name[..bang].Trim(), StringComparison.OrdinalIgnoreCase))
            ?? OpenDatabase(name[..bang].Trim());
        return (db, name[(bang + 1)..].Trim());
    }

    /// <summary>The in-memory store that holds this session's cursors.</summary>
    public Store CursorStore => _cursorStore ??= Store.CreateInMemory();

    /// <summary>CREATE CURSOR: a temporary table in the session's in-memory store.</summary>
    public WorkArea CreateCursor(TableSchema schema, int? area = null)
    {
        _cursorStore ??= Store.CreateInMemory();
        var existing = FindAlias(schema.Name);
        existing?.Close();
        var internalName = $"{schema.Name}_{++_cursorSeq}";
        var s = new TableSchema(internalName, schema.Fields);
        s.Tags.AddRange(schema.Tags);
        var table = _cursorStore.CreateTable(s, ExpressionHost);
        var wa = existing ?? Area(area ?? (Current.InUse ? FreeArea() : CurrentAreaNumber));
        wa.Open(table, schema.Name, schema.Name, exclusive: true, isCursor: true);
        CurrentAreaNumber = wa.Number;
        return wa;
    }

    /// <summary>
    /// USE: opens a table by name. Looks in the current database first ("db!table" names a
    /// specific open database), then for a free table file.
    /// </summary>
    public WorkArea Use(string name, int? area = null, string? alias = null, bool again = false, bool? exclusive = null)
    {
        Table table;
        string source;
        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            var db = OpenDatabases.FirstOrDefault(d => string.Equals(d.Name, name[..bang], StringComparison.OrdinalIgnoreCase))
                ?? OpenDatabase(name[..bang]);
            table = db.OpenTable(name[(bang + 1)..], ExpressionHost);
            source = db.Name + "!" + table.Name;
        }
        else if (!name.Contains('.') && !name.Contains(Path.DirectorySeparatorChar) && CurrentDatabase?.HasTable(name) == true)
        {
            table = CurrentDatabase.OpenTable(name, ExpressionHost);
            source = CurrentDatabase.Name + "!" + table.Name;
        }
        else if (OpenDatabases.FirstOrDefault(d => !name.Contains('.') && d.HasTable(name)) is { } otherDb)
        {
            table = otherDb.OpenTable(name, ExpressionHost);
            source = otherDb.Name + "!" + table.Name;
        }
        else
        {
            var path = ResolvePath(name, Store.FreeTableExtension);
            if (!File.Exists(path)) throw VfpException.FileNotFound(Path.GetFileName(path));
            var store = GetStore(path);
            if (store.Kind != StoreKind.FreeTable)
                throw new VfpException(ErrorCodes.FileAccessDenied, $"'{name}' is a database; use OPEN DATABASE.");
            table = store.OpenTable(store.TableNames()[0], ExpressionHost);
            source = Path.GetFullPath(path);
        }

        if (!again)
        {
            var already = OpenWorkAreas().FirstOrDefault(w => w.Table == table && (area == null || w.Number != area));
            if (already != null) throw new VfpException(ErrorCodes.FileInUse, "File is in use.", table.Name);
        }
        var wa = Area(area ?? CurrentAreaNumber);
        var a = alias ?? (bang > 0 ? name[(bang + 1)..] : Path.GetFileNameWithoutExtension(name));
        if (FindAlias(a) is { } clash && clash != wa)
        {
            if (alias != null) throw new VfpException(24, "Alias name is already in use.", a);
            a = ((char)('A' + (wa.Number - 1) % 10)).ToString() + (wa.Number > 10 ? wa.Number.ToString() : "");
        }
        wa.Open(table, a, source, exclusive ?? Options.Exclusive, isCursor: false);
        CurrentAreaNumber = wa.Number;
        return wa;
    }

    /// <summary>Closes the work areas that use a free table's file and releases the file (so it can be rebuilt).</summary>
    public void ReleaseStore(string path)
    {
        path = Path.GetFullPath(path);
        if (!_stores.Remove(path, out var store)) return;
        foreach (var wa in OpenWorkAreas().Where(w => w.Table.Store == store).ToList()) wa.Close();
        store.Dispose();
    }

    /// <summary>The store of an open database or free table file, opening it if needed.</summary>
    public Store StoreOf(string path) => GetStore(path);

    public void CloseTables()
    {
        foreach (var wa in OpenWorkAreas().ToList()) wa.Close();
        CurrentAreaNumber = 1;
    }

    public void CloseAll()
    {
        CloseTables();
        foreach (var s in _stores.Values) s.Dispose();
        _stores.Clear();
        CurrentDatabase = null;
    }

    // ---- Transactions (span every store opened in the session) ---------------------------

    private IEnumerable<Store> AllStores() => _stores.Values.Concat(_cursorStore is null ? [] : [_cursorStore]);

    public void BeginTransaction()
    {
        if (TransactionLevel >= 5) throw new VfpException(1593, "Too many transaction levels.");
        TransactionLevel++;
        foreach (var s in AllStores()) s.BeginTransaction();
    }

    public void EndTransaction()
    {
        if (TransactionLevel == 0) throw new VfpException(ErrorCodes.NoTransaction, "Command cannot be issued outside a transaction.");
        foreach (var s in AllStores()) if (s.TransactionLevel > 0) s.Commit();
        TransactionLevel--;
    }

    public void Rollback()
    {
        if (TransactionLevel == 0) throw new VfpException(ErrorCodes.NoTransaction, "Command cannot be issued outside a transaction.");
        foreach (var s in AllStores()) if (s.TransactionLevel > 0) s.Rollback();
        TransactionLevel--;
        foreach (var wa in OpenWorkAreas())
        {
            if (wa.Eof || wa.HasPendingChanges) continue;
            try { wa.Go(wa.RecNo); } catch (VfpException) { wa.GoTop(); }
        }
    }

    public void Dispose()
    {
        CloseAll();
        _cursorStore?.Dispose();
    }
}
