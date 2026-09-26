using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JoePro.Tooling;

/// <summary>Reads and writes "Content-Length:"-framed JSON messages (used by both LSP and DAP).</summary>
public sealed class JsonRpcChannel
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeLock = new();

    public JsonRpcChannel(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads the next message, or null at end of stream.</summary>
    public JsonObject? Read()
    {
        int length = -1;
        while (true)
        {
            var line = ReadHeaderLine();
            if (line == null) return null;
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[(colon + 1)..].Trim());
        }
        if (length < 0) return null;
        var buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            var n = _input.Read(buffer, read, length - read);
            if (n == 0) return null;
            read += n;
        }
        return JsonNode.Parse(buffer)?.AsObject();
    }

    private string? ReadHeaderLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = _input.ReadByte();
            if (b < 0) return sb.Length == 0 ? null : sb.ToString();
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
        }
    }

    public void Write(object message)
    {
        var body = message is JsonNode node ? Encoding.UTF8.GetBytes(node.ToJsonString()) : JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_writeLock)
        {
            if (Closed) return;
            try
            {
                _output.Write(header);
                _output.Write(body);
                _output.Flush();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Closed = true; // the client went away; drop further messages
            }
        }
    }

    public bool Closed { get; private set; }

    /// <summary>Frames a message into bytes (for tests and in-memory transports).</summary>
    public static byte[] Frame(object message)
    {
        var body = message is JsonNode node ? Encoding.UTF8.GetBytes(node.ToJsonString()) : JsonSerializer.SerializeToUtf8Bytes(message, Json);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
    }
}
