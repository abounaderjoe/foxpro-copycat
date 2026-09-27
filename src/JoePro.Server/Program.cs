using System.Text.Json.Nodes;
using JoePro.Data.Remote;
using JoePro.Server;

// joepro-server: run the Joe Pro Data Server and administer it.
var configPath = "joepro-server.json";
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--config" or "-c" && i + 1 < args.Length) configPath = args[++i];
    else rest.Add(args[i]);
}
configPath = Path.GetFullPath(configPath);
var command = rest.Count > 0 ? rest[0].ToLowerInvariant() : "run";
string Arg(int i, string usage) => i < rest.Count ? rest[i] : throw new ArgumentException("Usage: joepro-server " + usage);
string? Option(string name) { var i = rest.IndexOf(name); return i >= 0 && i + 1 < rest.Count ? rest[i + 1] : null; }
ServerConfig LoadConfig() => File.Exists(configPath) ? ServerConfig.Load(configPath) : throw new FileNotFoundException($"No configuration at {configPath}; run joepro-server init.");

try
{
    switch (command)
    {
        case "run":
        {
            var config = LoadConfig();
            var server = new DataServer(config, Path.GetDirectoryName(configPath)!);
            server.Log += Console.WriteLine;
            server.Start();
            var stop = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
            await stop.Task;
            await server.DisposeAsync();
            Console.WriteLine("Stopped.");
            return 0;
        }
        case "init":
        {
            if (File.Exists(configPath)) throw new InvalidOperationException($"{configPath} already exists.");
            var config = new ServerConfig();
            var password = Option("--password") ?? throw new ArgumentException("Usage: joepro-server init --admin <name> --password <password>");
            config.SetUser(Option("--admin") ?? "admin", password, admin: true);
            config.Save(configPath);
            Console.WriteLine($"Created {configPath} with administrator {Option("--admin") ?? "admin"}.");
            return 0;
        }
        case "add-database":
        {
            var config = LoadConfig();
            var name = Arg(1, "add-database <name> <path.jpdb>");
            var path = Arg(2, "add-database <name> <path.jpdb>");
            if (!File.Exists(Path.Combine(Path.GetDirectoryName(configPath)!, path))) throw new FileNotFoundException($"{path} does not exist.");
            config.Databases[name] = path;
            config.Save(configPath);
            Console.WriteLine($"Serving {name} from {path}.");
            return 0;
        }
        case "add-user":
        {
            var config = LoadConfig();
            var name = Arg(1, "add-user <name> --password <password> [--admin]");
            config.SetUser(name, Option("--password") ?? throw new ArgumentException("--password is required."), rest.Contains("--admin"));
            config.Save(configPath);
            Console.WriteLine($"User {name} saved.");
            return 0;
        }
        case "grant":
        {
            var config = LoadConfig();
            var usage = "grant <user> <database|*> <read|write|admin|none>";
            var user = Arg(1, usage);
            if (!config.Users.TryGetValue(user, out var u)) throw new KeyNotFoundException($"No user {user}.");
            u.Databases[Arg(2, usage)] = Enum.Parse<Permission>(Arg(3, usage), ignoreCase: true);
            config.Save(configPath);
            Console.WriteLine($"{user}: {Arg(2, usage)} = {Arg(3, usage)}.");
            return 0;
        }
        case "backup":
        {
            // Works while the server runs (SQLite online backup).
            var config = LoadConfig();
            var db = Arg(1, "backup <database> <destination>");
            DataServer.BackupFile(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, config.Databases[db])), Path.GetFullPath(Arg(2, "backup <database> <destination>")));
            Console.WriteLine("Backup written.");
            return 0;
        }
        case "check":
        {
            var config = LoadConfig();
            var db = Arg(1, "check <database>");
            var problems = DataServer.CheckFile(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, config.Databases[db])));
            foreach (var p in problems) Console.WriteLine(p);
            Console.WriteLine(problems.Count == 0 ? $"{db}: no problems found." : $"{db}: {problems.Count} problem(s).");
            return problems.Count == 0 ? 0 : 2;
        }
        case "status":
        {
            // joepro-server status joepro://admin@host  (password in JOEPRO_PASSWORD or the URL)
            var address = DataServerAddress.Parse(Arg(1, "status joepro://user@host[:port]/any") + (Arg(1, "").TrimEnd('/').Count(c => c == '/') < 3 ? "/_" : ""));
            var client = DataServerClient.Connect(address);
            try
            {
                var s = client.Send("status", new JsonObject())["result"]!;
                Console.WriteLine(s.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            finally { client.Close(); }
            return 0;
        }
        default:
            Console.WriteLine("""
                joepro-server — the Joe Pro Data Server

                  joepro-server [run] [--config joepro-server.json]   Serve the configured databases
                  joepro-server init --admin <name> --password <pw>    Create a configuration
                  joepro-server add-database <name> <path.jpdb>        Serve a database
                  joepro-server add-user <name> --password <pw> [--admin]
                  joepro-server grant <user> <database|*> <read|write|admin|none>
                  joepro-server backup <database> <file>              Online backup
                  joepro-server check <database>                      Integrity check
                  joepro-server status joepro://admin@host            Sessions and locks of a running server
                """);
            return command is "help" or "--help" or "-h" ? 0 : 1;
    }
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or KeyNotFoundException or InvalidOperationException or JoePro.Core.VfpException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
