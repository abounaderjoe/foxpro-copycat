using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JoePro.Data.Remote;

/// <summary>
/// The Data Server protocol: each message is a 4-byte little-endian length followed by a UTF-8 JSON object.
/// Requests carry an id and an op; responses echo the id with ok/result or ok:false/error/kind; the server also
/// pushes events ({"event": "changed", …}). Values are null, numbers (whole numbers are integers), strings, or
/// {"$b": base64} for binary.
/// </summary>
public static class Wire
{
    public const int DefaultPort = 9150;
    public const int MaxMessage = 64 * 1024 * 1024;

    public static async Task WriteAsync(Stream s, JsonObject message, SemaphoreSlim gate, CancellationToken ct = default)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await s.WriteAsync(frame, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>The next message, or null when the other side closed the connection.</summary>
    public static async Task<JsonObject?> ReadAsync(Stream s, CancellationToken ct = default)
    {
        var head = new byte[4];
        if (!await Fill(s, head, ct).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length is < 0 or > MaxMessage) throw new InvalidDataException("Message too large.");
        var body = new byte[length];
        if (!await Fill(s, body, ct).ConfigureAwait(false)) return null;
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidDataException("A message must be a JSON object.");
    }

    private static async Task<bool> Fill(Stream s, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public static JsonNode? ToJson(object? v) => v switch
    {
        null or DBNull => null,
        byte[] b => new JsonObject { ["$b"] = Convert.ToBase64String(b) },
        long l => JsonValue.Create(l),
        int i => JsonValue.Create((long)i),
        double d when double.IsFinite(d) => JsonValue.Create(d),
        double d => JsonValue.Create(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        bool b => JsonValue.Create(b ? 1L : 0L),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)),
    };

    public static object? FromJson(JsonNode? n)
    {
        switch (n)
        {
            case null: return null;
            case JsonObject o when o["$b"] is JsonValue b: return Convert.FromBase64String(b.GetValue<string>());
            case JsonValue v:
                var e = v.GetValue<JsonElement>();
                return e.ValueKind switch
                {
                    JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
                    JsonValueKind.String => e.GetString(),
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    _ => null,
                };
            default: return n.ToJsonString();
        }
    }

    public static JsonArray Args((string Name, object? Value)[] args)
    {
        var a = new JsonArray();
        foreach (var (name, value) in args) a.Add(new JsonArray(JsonValue.Create(name), ToJson(value)));
        return a;
    }

    public static (string Name, object? Value)[] Args(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => ((string)x![0]!, FromJson(x[1]))).ToArray() : [];

    public static JsonArray Rows(List<object?[]> rows)
    {
        var a = new JsonArray();
        foreach (var r in rows)
        {
            var row = new JsonArray();
            foreach (var v in r) row.Add(ToJson(v));
            a.Add(row);
        }
        return a;
    }

    public static List<object?[]> Rows(JsonNode? n) =>
        n is JsonArray a ? a.Select(r => ((JsonArray)r!).Select(FromJson).ToArray()).ToList() : new();
}
