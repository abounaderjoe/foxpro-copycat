using JoePro.Core;
using JoePro.Data;
using JoePro.Data.Remote;
using JoePro.Runtime;
using JoePro.Server;

namespace JoePro.Tests.Server;

/// <summary>The Data Server with real clients: permissions, locks and leases, change notifications, admin tasks, concurrency.</summary>
public sealed class DataServerTests : IAsyncLifetime
{
    private readonly string _dir = TestPaths.TempDir();
    private DataServer _server = null!;
    private readonly List<Interpreter> _clients = new();

    public async Task InitializeAsync()
    {
        var setup = new Interpreter(new TextWriterOutput(TextWriter.Null), _dir);
        setup.ExecuteCommand("""
            CREATE DATABASE sales
            CREATE TABLE customer (id I, name C(20), visits I)
            ALTER TABLE customer ADD PRIMARY KEY id TAG id
            INSERT INTO customer VALUES (1, 'Acme', 0)
            INSERT INTO customer VALUES (2, 'Globex', 0)
            CLOSE DATABASES ALL
            """);
        setup.Session.Dispose();
        var config = new ServerConfig { Port = 0, Listen = "127.0.0.1", LeaseSeconds = 2 };
        config.Databases["sales"] = "sales.jpdb";
        config.SetUser("alice", "secret");
        config.SetUser("bob", "hunter2");
        config.SetUser("root", "admin", admin: true);
        config.Users["alice"].Databases["sales"] = Permission.Write;
        config.Users["bob"].Databases["*"] = Permission.Read;
        _server = new DataServer(config, _dir);
        _server.Start();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) c.Session.Dispose();
        await _server.DisposeAsync();
    }

    private string Url(string user, string password, string db = "sales") => $"joepro://{user}:{password}@127.0.0.1:{_server.Port}/{db}";

    private Interpreter Client()
    {
        var dir = Path.Combine(_dir, "client" + _clients.Count);
        Directory.CreateDirectory(dir);
        var rt = new Interpreter(new TextWriterOutput(TextWriter.Null), dir);
        _clients.Add(rt);
        return rt;
    }

    [Fact]
    public void Clients_share_the_database_with_permissions_locks_and_notifications()
    {
        var alice = Client();
        var bob = Client();
        alice.ExecuteCommand($"OPEN DATABASE \"{Url("alice", "secret")}\"\nUSE customer");
        Assert.True(alice.Session.CurrentDatabase!.IsRemote);
        Assert.Equal("SALES", alice.Session.CurrentDatabase.Name);
        alice.ExecuteCommand("INSERT INTO customer VALUES (3, 'Initech', 0)");

        // bob maps the name in joepro-data.json: the program says OPEN DATABASE sales.
        File.WriteAllText(Path.Combine(bob.Options.Default_, "joepro-data.json"), "{\"databases\": {\"sales\": \"" + Url("bob", "hunter2") + "\"}}");
        var changed = 0;
        bob.ExecuteCommand("OPEN DATABASE sales\nUSE customer ORDER id");
        ((RemoteStoreConnection)bob.Session.CurrentDatabase!.Link).Changed += () => Interlocked.Increment(ref changed);
        bob.ExecuteCommand("GO BOTTOM");
        Assert.Equal("Initech", bob.Evaluate("TRIM(customer.name)").AsString);

        // bob may only read.
        var denied = Assert.ThrowsAny<Exception>(() => bob.ExecuteCommand("REPLACE name WITH 'x'"));
        Assert.Contains("readonly", denied.Message, StringComparison.OrdinalIgnoreCase);

        // Record locks are held on the server.
        alice.ExecuteCommand("GO TOP");
        Assert.True(alice.Evaluate("RLOCK()").AsBool);
        bob.ExecuteCommand("GO TOP");
        Assert.False(bob.Evaluate("RLOCK()").AsBool);
        Assert.True(bob.Evaluate("ISRLOCKED(1)").AsBool);
        alice.ExecuteCommand("UNLOCK");
        Assert.True(bob.Evaluate("RLOCK()").AsBool);
        bob.ExecuteCommand("UNLOCK");

        // Commits notify the other clients, whose cached rows refresh.
        alice.ExecuteCommand("REPLACE visits WITH 5 FOR id = 2");
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref changed) == 0 && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.True(changed > 0);
        Thread.Sleep(300);
        bob.ExecuteCommand("SEEK 2");
        Assert.Equal(5, bob.Evaluate("customer.visits").AsNumber);

        // A transaction commits as one change.
        alice.ExecuteCommand("BEGIN TRANSACTION\nREPLACE visits WITH 9 FOR id = 1\nROLLBACK");
        bob.ExecuteCommand("SEEK 1");
        Assert.Equal(0, bob.Evaluate("customer.visits").AsNumber);
    }

    [Fact]
    public void Sign_in_and_database_permissions_are_checked()
    {
        var c = Client();
        Assert.Contains("password", Assert.Throws<DataServerException>(() => c.ExecuteCommand($"OPEN DATABASE \"{Url("alice", "wrong")}\"")).Message);
        Assert.Contains("no database", Assert.Throws<DataServerException>(() => c.ExecuteCommand($"OPEN DATABASE \"{Url("alice", "secret", "hr")}\"")).Message);
        var client = DataServerClient.Connect(DataServerAddress.Parse(Url("alice", "secret")));
        try
        {
            var h = (long)client.Send("open", new System.Text.Json.Nodes.JsonObject { ["db"] = "sales" })["h"]!;
            var attach = new System.Text.Json.Nodes.JsonObject { ["h"] = h, ["sql"] = "ATTACH DATABASE '/tmp/x.db' AS x", ["args"] = new System.Text.Json.Nodes.JsonArray() };
            Assert.Equal("permission", Assert.Throws<DataServerException>(() => client.Send("exec", attach)).Kind);
            Assert.Equal("permission", Assert.Throws<DataServerException>(() => client.Send("status", new())).Kind);
        }
        finally { client.Close(); }
    }

    [Fact]
    public void A_crashed_client_loses_its_locks()
    {
        var alice = Client();
        alice.ExecuteCommand($"OPEN DATABASE \"{Url("alice", "secret")}\"\nUSE customer\nGO TOP");
        Assert.True(alice.Evaluate("RLOCK()").AsBool);
        var bob = Client();
        bob.ExecuteCommand($"OPEN DATABASE \"{Url("bob", "hunter2")}\"\nUSE customer\nGO TOP");
        Assert.False(bob.Evaluate("RLOCK()").AsBool);
        // alice's process dies: its connection drops without an UNLOCK.
        ((RemoteStoreConnection)alice.Session.CurrentDatabase!.Link).Client.Close();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (_server.Locks.Snapshot().Count > 0 && DateTime.UtcNow < until) Thread.Sleep(20);
        Assert.True(bob.Evaluate("RLOCK()").AsBool);
    }

    [Fact]
    public void Administrators_back_up_check_and_see_status()
    {
        var alice = Client();
        alice.ExecuteCommand($"OPEN DATABASE \"{Url("alice", "secret")}\"\nUSE customer\nGO TOP\n=RLOCK()");
        var admin = DataServerClient.Connect(DataServerAddress.Parse(Url("root", "admin")));
        try
        {
            var status = admin.Send("status", new())["result"]!;
            Assert.Contains("alice", status.ToJsonString());
            Assert.Single(status["locks"]!.AsArray());
            admin.Send("backup", new System.Text.Json.Nodes.JsonObject { ["db"] = "sales", ["to"] = "backups/sales-copy.jpdb" });
            Assert.Empty(admin.Send("check", new System.Text.Json.Nodes.JsonObject { ["db"] = "sales" })["result"]!.AsArray());
        }
        finally { admin.Close(); }
        using var copy = Store.Open(Path.Combine(_dir, "backups", "sales-copy.jpdb"));
        Assert.Equal(2, copy.OpenTable("customer").RecordCount);
        Assert.Empty(DataServer.CheckFile(Path.Combine(_dir, "backups", "sales-copy.jpdb")));
    }

    [Fact]
    public void Concurrent_clients_do_not_lose_updates()
    {
        const int clients = 8, rounds = 25;
        var errors = new List<Exception>();
        var threads = Enumerable.Range(0, clients).Select(i =>
        {
            var rt = Client();
            rt.ExecuteCommand($"OPEN DATABASE \"{Url("alice", "secret")}\"\nUSE customer ORDER id\nSET REPROCESS TO AUTOMATIC");
            return new Thread(() =>
            {
                try
                {
                    for (int r = 0; r < rounds; r++)
                    {
                        rt.ExecuteCommand("SEEK 1");
                        while (!rt.Evaluate("RLOCK()").AsBool) Thread.Sleep(1);
                        // Re-read under the lock, then write.
                        rt.ExecuteCommand("GO RECNO()\nREPLACE visits WITH visits + 1\nUNLOCK");
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            });
        }).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) Assert.True(t.Join(TimeSpan.FromMinutes(2)));
        Assert.Empty(errors);
        using var store = Store.Open(Path.Combine(_dir, "sales.jpdb"));
        var row = store.OpenTable("customer").Read(1)!;
        Assert.Equal(clients * rounds, row.Values[2].AsNumber);
        Assert.Empty(_server.Locks.Snapshot());
    }
}
