using System.Globalization;
using System.Text.Json;
using JoePro.Core;

namespace JoePro.Data;

/// <summary>A view stored in a database (CREATE SQL VIEW). Properties follow DBSETPROP names.</summary>
public sealed class ViewDefinition
{
    public string Name { get; set; } = "";
    public string Sql { get; set; } = "";
    public bool Remote { get; set; }
    public string? Connection { get; set; }
    public bool ShareConnection { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, string>> FieldProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>View-level defaults, as DBGETPROP reports them for a new view.</summary>
    public static readonly IReadOnlyDictionary<string, Value> ViewDefaults = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
    {
        ["SendUpdates"] = Value.False,
        ["Tables"] = Value.EmptyString,
        ["WhereType"] = Value.Number(3),
        ["UpdateType"] = Value.Number(1),
        ["BatchUpdateCount"] = Value.Number(1),
        ["FetchSize"] = Value.Number(100),
        ["MaxRecords"] = Value.Number(-1),
        ["FetchMemo"] = Value.True,
        ["ShareConnection"] = Value.False,
        ["Comment"] = Value.EmptyString,
        ["Prepared"] = Value.False,
        ["CompareMemo"] = Value.True,
        ["UseMemoSize"] = Value.Number(255),
        ["Offline"] = Value.False,
        ["AllowSimultaneousFetch"] = Value.False,
    };

    public static readonly IReadOnlyDictionary<string, Value> FieldDefaults = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
    {
        ["KeyField"] = Value.False,
        ["Updatable"] = Value.False,
        ["UpdateName"] = Value.EmptyString,
        ["Caption"] = Value.EmptyString,
        ["DefaultValue"] = Value.EmptyString,
        ["RuleExpression"] = Value.EmptyString,
        ["RuleText"] = Value.EmptyString,
        ["DataType"] = Value.EmptyString,
        ["Comment"] = Value.EmptyString,
    };

    public Value Get(string prop) =>
        Properties.TryGetValue(prop, out var s) ? PropValue.Decode(s) : ViewDefaults.TryGetValue(prop, out var d) ? d : Value.EmptyString;

    public void Set(string prop, Value v) => Properties[prop] = PropValue.Encode(v);

    public Value GetField(string field, string prop) =>
        FieldProperties.TryGetValue(field, out var fp) && fp.TryGetValue(prop, out var s) ? PropValue.Decode(s)
            : FieldDefaults.TryGetValue(prop, out var d) ? d : Value.EmptyString;

    public void SetField(string field, string prop, Value v)
    {
        if (!FieldProperties.TryGetValue(field, out var fp)) FieldProperties[field] = fp = new(StringComparer.OrdinalIgnoreCase);
        fp[prop] = PropValue.Encode(v);
    }
}

/// <summary>A named connection stored in a database (CREATE CONNECTION).</summary>
public sealed class ConnectionDefinition
{
    public string Name { get; set; } = "";
    public string? DataSource { get; set; }
    public string? UserId { get; set; }
    public string? Password { get; set; }
    public string? Database { get; set; }
    public string? ConnectString { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The connection string SQLCONNECT uses: CONNSTRING when given, else an ODBC DSN string.</summary>
    public string BuildConnectString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectString)) return ConnectString;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(DataSource)) parts.Add("DSN=" + DataSource);
        if (!string.IsNullOrEmpty(UserId)) parts.Add("UID=" + UserId);
        if (!string.IsNullOrEmpty(Password)) parts.Add("PWD=" + Password);
        if (!string.IsNullOrEmpty(Database)) parts.Add("DATABASE=" + Database);
        return string.Join(";", parts);
    }
}

/// <summary>Stores FoxPro values in text property bags, keeping their type.</summary>
public static class PropValue
{
    public static string Encode(Value v) => v.Kind switch
    {
        ValueKind.Logical => v.AsBool ? "L:T" : "L:F",
        ValueKind.Number => "N:" + v.AsNumber.ToString("R", CultureInfo.InvariantCulture),
        ValueKind.Null => "X:",
        _ => "C:" + v.AsString,
    };

    public static Value Decode(string s)
    {
        if (s.Length < 2 || s[1] != ':') return Value.String(s);
        var body = s[2..];
        return s[0] switch
        {
            'L' => Value.Logical(body == "T"),
            'N' => double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Value.Number(d, body.Contains('.') ? body.Length - body.IndexOf('.') - 1 : 0) : Value.Number(0),
            'X' => Value.Null,
            _ => Value.String(body),
        };
    }
}

public static class DbObjectStore
{
    public const string ViewKind = "VIEW";
    public const string ConnectionKind = "CONNECTION";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static ViewDefinition? GetView(this Store store, string name) =>
        store.GetObject(ViewKind, name) is { } json ? JsonSerializer.Deserialize<ViewDefinition>(json, Json) is { } v ? Normalize(v) : null : null;

    public static void SaveView(this Store store, ViewDefinition view) =>
        store.SaveObject(ViewKind, view.Name, JsonSerializer.Serialize(view, Json));

    public static ConnectionDefinition? GetConnection(this Store store, string name) =>
        store.GetObject(ConnectionKind, name) is { } json ? JsonSerializer.Deserialize<ConnectionDefinition>(json, Json) : null;

    public static void SaveConnection(this Store store, ConnectionDefinition conn) =>
        store.SaveObject(ConnectionKind, conn.Name, JsonSerializer.Serialize(conn, Json));

    // System.Text.Json creates case-sensitive dictionaries; restore FoxPro's case-insensitive names.
    private static ViewDefinition Normalize(ViewDefinition v)
    {
        v.Properties = new Dictionary<string, string>(v.Properties, StringComparer.OrdinalIgnoreCase);
        v.FieldProperties = v.FieldProperties.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        return v;
    }
}
