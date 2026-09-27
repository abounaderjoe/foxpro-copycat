using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoePro.Server;

public enum Permission { None, Read, Write, Admin }

public sealed class ServerUser
{
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    /// <summary>Per-database permission ("read", "write" or "admin"); "*" applies to every database.</summary>
    public Dictionary<string, Permission> Databases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Server administrators may back up, check and see the status of every database.</summary>
    public bool Admin { get; set; }
}

/// <summary>
/// The Data Server's configuration (joepro-server.json): port, TLS certificate, lock lease, the databases it serves
/// (name → .jpdb path) and its users with hashed passwords and per-database permissions.
/// </summary>
public sealed class ServerConfig
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public int Port { get; set; } = JoePro.Data.Remote.Wire.DefaultPort;
    public string Listen { get; set; } = "0.0.0.0";
    /// <summary>A .pfx certificate for TLS; without one the server speaks plain TCP (use it on trusted networks only).</summary>
    public string? Certificate { get; set; }
    public string? CertificatePassword { get; set; }
    /// <summary>A client that sends nothing for this long loses its locks and its session.</summary>
    public double LeaseSeconds { get; set; } = 30;
    public Dictionary<string, string> Databases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ServerUser> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static ServerConfig Load(string path)
    {
        var c = JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Json) ?? new ServerConfig();
        c.Databases = new(c.Databases, StringComparer.OrdinalIgnoreCase);
        c.Users = new(c.Users, StringComparer.OrdinalIgnoreCase);
        foreach (var u in c.Users.Values) u.Databases = new(u.Databases, StringComparer.OrdinalIgnoreCase);
        return c;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");

    public static (string Salt, string Hash) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32)));
    }

    public void SetUser(string name, string password, bool admin = false)
    {
        var (salt, hash) = HashPassword(password);
        if (!Users.TryGetValue(name, out var u)) Users[name] = u = new ServerUser();
        u.Salt = salt;
        u.Hash = hash;
        u.Admin |= admin;
    }

    public ServerUser? Authenticate(string user, string password)
    {
        if (!Users.TryGetValue(user, out var u) || u.Salt.Length == 0) return null;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(u.Salt), 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(u.Hash)) ? u : null;
    }

    public Permission PermissionOf(ServerUser u, string database)
    {
        if (u.Admin) return Permission.Admin;
        if (u.Databases.TryGetValue(database, out var p)) return p;
        return u.Databases.TryGetValue("*", out var all) ? all : Permission.None;
    }
}
