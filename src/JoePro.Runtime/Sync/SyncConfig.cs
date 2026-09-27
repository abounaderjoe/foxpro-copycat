using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JoePro.Core;

namespace JoePro.Runtime.Sync;

public enum SyncSide { Legacy, Joe }

public enum CaptureMode
{
    /// <summary>Compare each legacy table with the last synced state by key (no change to the legacy database).</summary>
    Snapshot,
    /// <summary>Read the _joesync_log table that the installed DBC triggers append to (exact and cheap).</summary>
    Triggers,
}

/// <summary>A table kept in sync: its legacy file, its key field and which side wins when both change the same field.</summary>
public sealed class SyncTable
{
    public string Name { get; set; } = "";
    /// <summary>The legacy table file, relative to the legacy folder (default: name.dbf).</summary>
    public string? LegacyFile { get; set; }
    /// <summary>The primary or candidate key field that identifies a row on both sides (record numbers are never used).</summary>
    public string Key { get; set; } = "";
    /// <summary>The system of record: its value wins a same-field conflict. Legacy until cutover.</summary>
    public SyncSide Authority { get; set; } = SyncSide.Legacy;
    /// <summary>When one side deletes a row the other updated, the update wins (the row comes back) unless this is set.</summary>
    public bool DeleteWins { get; set; }
}

/// <summary>joesync.json: what to keep in sync, and how.</summary>
public sealed class SyncConfig
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The legacy folder (with the .DBF files, and the .DBC in trigger mode).</summary>
    public string Legacy { get; set; } = "";
    /// <summary>The legacy database container, for trigger capture.</summary>
    public string? LegacyDatabase { get; set; }
    /// <summary>The Joe Pro database: a .jpdb path or a joepro:// address.</summary>
    public string Database { get; set; } = "";
    public CaptureMode Capture { get; set; } = CaptureMode.Snapshot;
    public List<SyncTable> Tables { get; set; } = new();
    /// <summary>Set by the cutover tool: Joe Pro is the system of record and the legacy side only follows.</summary>
    public bool CutOver { get; set; }

    public static SyncConfig Load(string path) => JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(path), Json) ?? new();
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");

    public SyncTable? Find(string name) => Tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A row as sync sees it: field name (upper case) → value.</summary>
public sealed class SyncRow : Dictionary<string, Value>
{
    public SyncRow() : base(StringComparer.OrdinalIgnoreCase) { }
    public SyncRow(IDictionary<string, Value> d) : base(d, StringComparer.OrdinalIgnoreCase) { }

    /// <summary>A comparable text form of a value (character values without trailing spaces).</summary>
    public static string Canon(Value v) => v.Kind switch
    {
        ValueKind.Null => "\0null",
        ValueKind.Character => "C" + v.AsString.TrimEnd(' '),
        ValueKind.Number => "N" + v.AsNumber.ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Currency => "N" + ((double)v.AsCurrency).ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Logical => v.AsBool ? "LT" : "LF",
        ValueKind.Date => "D" + v.JulianDay.ToString(CultureInfo.InvariantCulture),
        ValueKind.DateTime => "T" + v.JulianMs.ToString(CultureInfo.InvariantCulture),
        ValueKind.Binary => "B" + Convert.ToBase64String(v.AsBinary),
        _ => "?" + v,
    };

    public static bool Same(Value a, Value b) => Canon(a) == Canon(b);

    public string Hash() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\u0001", Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Select(k => k.ToUpperInvariant() + "=" + Canon(this[k]))))));

    public string ToJson() => JsonSerializer.Serialize(Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToDictionary(k => k.ToUpperInvariant(), k => Canon(this[k])));

    public static SyncRow FromJson(string json)
    {
        var row = new SyncRow();
        foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new()) row[k] = FromCanon(v);
        return row;
    }

    public static Value FromCanon(string s)
    {
        if (s == "\0null") return Value.Null;
        var body = s.Length > 1 ? s[1..] : "";
        return s[0] switch
        {
            'C' => Value.String(body),
            'N' => Value.Number(double.Parse(body, CultureInfo.InvariantCulture)),
            'L' => Value.Logical(body == "T"),
            'D' => Value.FromJulian(long.Parse(body, CultureInfo.InvariantCulture)),
            'T' => Value.DateTimeFromJulianMs(long.Parse(body, CultureInfo.InvariantCulture)),
            'B' => Value.Binary(Convert.FromBase64String(body)),
            _ => Value.Null,
        };
    }

    /// <summary>The text of a key value, the same on both sides.</summary>
    public static string KeyText(Value v) => v.Kind switch
    {
        ValueKind.Character => v.AsString.Trim(),
        ValueKind.Number => v.AsNumber.ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Currency => ((double)v.AsCurrency).ToString("R", CultureInfo.InvariantCulture),
        _ => Canon(v),
    };
}

/// <summary>A conflict in the log: both sides' values, which won, and whether someone has reviewed it.</summary>
public sealed record SyncConflict(long Id, DateTime TimeUtc, string Table, string Key, string Field, string Kind,
    string? LegacyValue, string? JoeValue, SyncSide Winner, bool Resolved, string? Note);

/// <summary>One sync cycle's numbers (the dashboard).</summary>
public sealed record SyncCycle(DateTime TimeUtc, int LegacyChanges, int JoeChanges, int AppliedToLegacy, int AppliedToJoe, int Conflicts, int Errors, long Milliseconds);

public sealed record SyncStatus(DateTime? LastCycleUtc, TimeSpan? Lag, int PendingBatches, int PendingRows, int Blocked, int OpenConflicts, IReadOnlyList<SyncCycle> Recent, bool CutOver);
