using System.IO.Pipelines;
using System.Text.Json.Nodes;
using JoePro.Tooling;

namespace JoePro.Tests.Tooling;

public class ProtocolTests
{
    /// <summary>A client connected to a server through in-memory pipes.</summary>
    private sealed class Client : IDisposable
    {
        private readonly Pipe _toServer = new();
        private readonly Pipe _toClient = new();
        private readonly JsonRpcChannel _channel;
        private readonly Task _server;

        public Client(Func<Stream, Stream, int> server)
        {
            var serverIn = _toServer.Reader.AsStream();
            var serverOut = _toClient.Writer.AsStream();
            _server = Task.Run(() => { server(serverIn, serverOut); _toClient.Writer.Complete(); });
            _channel = new JsonRpcChannel(_toClient.Reader.AsStream(), _toServer.Writer.AsStream());
        }

        public void Send(JsonObject msg) => _channel.Write(msg);

        public JsonObject Next(Func<JsonObject, bool> match, int timeoutMs = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var t = Task.Run(() => _channel.Read());
                if (!t.Wait(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds))) break;
                var m = t.Result ?? throw new EndOfStreamException("Server closed the stream.");
                if (match(m)) return m;
            }
            throw new TimeoutException("Expected message did not arrive.");
        }

        public void Dispose()
        {
            _toServer.Writer.Complete();
            _server.Wait(5000);
        }
    }

    [Fact]
    public void Lsp_publishes_diagnostics_and_answers_completion_hover_and_symbols()
    {
        using var c = new Client((i, o) => new LspServer(i, o).Run());
        c.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject { ["rootUri"] = null } });
        var init = c.Next(m => (int?)m["id"] == 1);
        Assert.True((bool)init["result"]!["capabilities"]!["hoverProvider"]!);

        const string uri = "file:///tmp/test.prg";
        c.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "textDocument/didOpen",
            ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = "foxpro", ["version"] = 1, ["text"] = "x = (1 +\n" } },
        });
        var diag = c.Next(m => (string?)m["method"] == "textDocument/publishDiagnostics");
        Assert.Equal(1, diag["params"]!["diagnostics"]!.AsArray().Count);
        Assert.Equal(0, (int)diag["params"]!["diagnostics"]![0]!["range"]!["start"]!["line"]!);

        c.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["method"] = "textDocument/didChange",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = 2 },
                ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "FUNCTION Twice(n)\n   RETURN n * 2\nENDFUNC\n? Twi" }),
            },
        });
        diag = c.Next(m => (string?)m["method"] == "textDocument/publishDiagnostics");
        Assert.Empty(diag["params"]!["diagnostics"]!.AsArray());

        c.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "textDocument/completion",
            ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = 3, ["character"] = 5 } },
        });
        var comp = c.Next(m => (int?)m["id"] == 2);
        Assert.Contains(comp["result"]!["items"]!.AsArray(), i => (string?)i!["label"] == "Twice");

        c.Send(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "textDocument/hover",
            ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = 0, ["character"] = 11 } },
        });
        var hover = c.Next(m => (int?)m["id"] == 3);
        Assert.Contains("Twice(n)", (string)hover["result"]!["contents"]!["value"]!);

        c.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 4, ["method"] = "textDocument/documentSymbol", ["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } } });
        var syms = c.Next(m => (int?)m["id"] == 4);
        Assert.Equal("Twice", (string)syms["result"]![0]!["name"]!);

        c.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 5, ["method"] = "shutdown" });
        c.Next(m => (int?)m["id"] == 5);
        c.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "exit" });
    }

    [Fact]
    public void Dap_launches_stops_at_breakpoint_inspects_and_continues()
    {
        var dir = TestPaths.TempDir();
        var program = Path.Combine(dir, "prog.prg");
        File.WriteAllText(program, "LOCAL n\nn = 41\nn = n + 1\n? 'answer', n\n");
        using var c = new Client((i, o) => new DapServer(i, o).Run());
        int seq = 0;
        JsonObject Req(string command, JsonObject? args = null) => new() { ["seq"] = ++seq, ["type"] = "request", ["command"] = command, ["arguments"] = args };
        JsonObject Response(string command) => c.Next(m => (string?)m["type"] == "response" && (string?)m["command"] == command);

        c.Send(Req("initialize", new JsonObject { ["adapterID"] = "joepro" }));
        Assert.True((bool)Response("initialize")["body"]!["supportsConditionalBreakpoints"]!);
        c.Next(m => (string?)m["event"] == "initialized");
        c.Send(Req("launch", new JsonObject { ["program"] = program }));
        Response("launch");
        c.Send(Req("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = program },
            ["breakpoints"] = new JsonArray(new JsonObject { ["line"] = 3 }),
        }));
        Assert.True((bool)Response("setBreakpoints")["body"]!["breakpoints"]![0]!["verified"]!);
        c.Send(Req("configurationDone"));
        Response("configurationDone");

        var stopped = c.Next(m => (string?)m["event"] == "stopped");
        Assert.Equal("breakpoint", (string)stopped["body"]!["reason"]!);

        c.Send(Req("stackTrace", new JsonObject { ["threadId"] = 1 }));
        var stack = Response("stackTrace");
        Assert.Equal(3, (int)stack["body"]!["stackFrames"]![0]!["line"]!);
        Assert.Equal(program, (string)stack["body"]!["stackFrames"]![0]!["source"]!["path"]!);

        c.Send(Req("scopes", new JsonObject { ["frameId"] = 0 }));
        var locRef = (int)Response("scopes")["body"]!["scopes"]![0]!["variablesReference"]!;
        c.Send(Req("variables", new JsonObject { ["variablesReference"] = locRef }));
        var vars = Response("variables")["body"]!["variables"]!.AsArray();
        Assert.Contains(vars, v => (string?)v!["name"] == "n" && (string?)v["value"] == "41");

        c.Send(Req("evaluate", new JsonObject { ["expression"] = "n * 2", ["frameId"] = 0 }));
        Assert.Equal("82", (string)Response("evaluate")["body"]!["result"]!);

        c.Send(Req("next", new JsonObject { ["threadId"] = 1 }));
        Response("next");
        var step = c.Next(m => (string?)m["event"] == "stopped");
        Assert.Equal("step", (string)step["body"]!["reason"]!);

        c.Send(Req("continue", new JsonObject { ["threadId"] = 1 }));
        Response("continue");
        var stdout = new System.Text.StringBuilder();
        c.Next(m =>
        {
            if ((string?)m["event"] == "output" && (string?)m["body"]!["category"] == "stdout") stdout.Append((string?)m["body"]!["output"]);
            return (string?)m["event"] == "terminated";
        });
        Assert.Contains("answer         42", stdout.ToString());
        c.Send(Req("disconnect"));
        Response("disconnect");
    }
}
