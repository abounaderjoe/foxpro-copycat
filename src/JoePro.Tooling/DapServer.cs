using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using JoePro.Core;
using JoePro.Runtime;

namespace JoePro.Tooling;

/// <summary>
/// A Debug Adapter Protocol server (`joepro dap`) so VS Code and other DAP clients can run and debug
/// FoxPro programs with Joe Pro. The program runs on a worker thread; while it is paused, requests that
/// inspect program state are executed on that thread.
/// </summary>
public sealed class DapServer : IDebugHost, IConsoleOutput
{
    private const int ThreadId = 1;
    private const int PublicsRef = 1_000_000;
    private const int LocalsBase = 2_000_000;

    private readonly JsonRpcChannel _channel;
    private readonly BlockingCollection<JsonObject> _pausedRequests = new();
    private int _seq;
    private Interpreter? _rt;
    private Debugger? _debugger;
    private Thread? _worker;
    private volatile DebugStop? _stop;
    private JsonObject? _launchArgs;
    private readonly List<(string File, List<(int Line, string? Condition)> Lines)> _pendingBreakpoints = new();
    private volatile bool _terminated;

    public DapServer(Stream input, Stream output) => _channel = new JsonRpcChannel(input, output);

    public int Column { get; private set; }

    public int Run()
    {
        while (true)
        {
            var msg = _channel.Read();
            if (msg == null) return 0;
            if ((string?)msg["type"] != "request") continue;
            var command = (string?)msg["command"];
            // While paused, inspection and resume requests run on the program thread.
            if (_stop != null && command is "stackTrace" or "scopes" or "variables" or "evaluate" or "setVariable"
                    or "continue" or "next" or "stepIn" or "stepOut" or "disconnect" or "terminate")
            {
                _pausedRequests.Add(msg);
                if (command is "disconnect" or "terminate") { _worker?.Join(2000); Respond(msg, null); return 0; }
                continue;
            }
            try
            {
                var done = Handle(msg);
                if (done) return 0;
            }
            catch (Exception ex)
            {
                Respond(msg, null, success: false, message: ex.Message);
            }
        }
    }

    private bool Handle(JsonObject msg)
    {
        var args = msg["arguments"] as JsonObject;
        switch ((string?)msg["command"])
        {
            case "initialize":
                Respond(msg, new JsonObject
                {
                    ["supportsConfigurationDoneRequest"] = true,
                    ["supportsConditionalBreakpoints"] = true,
                    ["supportsHitConditionalBreakpoints"] = false,
                    ["supportsEvaluateForHovers"] = true,
                    ["supportsSetVariable"] = true,
                    ["supportsTerminateRequest"] = true,
                });
                Event("initialized");
                return false;
            case "launch":
                _launchArgs = args;
                Respond(msg, null);
                return false;
            case "setBreakpoints":
            {
                var path = (string?)args?["source"]?["path"];
                var lines = (args?["breakpoints"] as JsonArray ?? new JsonArray())
                    .Select(b => ((int)b!["line"]!, (string?)b["condition"])).ToList();
                var result = new JsonArray();
                if (path != null)
                {
                    if (_debugger != null) _debugger.SetLineBreakpoints(path, lines);
                    else _pendingBreakpoints.Add((path, lines));
                    foreach (var (line, _) in lines) result.Add(new JsonObject { ["verified"] = true, ["line"] = line });
                }
                Respond(msg, new JsonObject { ["breakpoints"] = result });
                return false;
            }
            case "setExceptionBreakpoints":
                Respond(msg, new JsonObject { ["breakpoints"] = new JsonArray() });
                return false;
            case "configurationDone":
                Respond(msg, null);
                StartProgram();
                return false;
            case "threads":
                Respond(msg, new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = ThreadId, ["name"] = "FoxPro program" }) });
                return false;
            case "pause":
                _debugger?.RequestPause();
                Respond(msg, null);
                return false;
            case "disconnect" or "terminate":
                Respond(msg, null);
                return true;
            case "stackTrace" or "scopes" or "variables" or "evaluate":
                // Not paused: nothing to inspect.
                Respond(msg, null, success: false, message: "The program is not paused.");
                return false;
            default:
                Respond(msg, null);
                return false;
        }
    }

    private void StartProgram()
    {
        var program = (string?)_launchArgs?["program"] ?? throw new InvalidOperationException("launch requires 'program'.");
        var cwd = (string?)_launchArgs?["cwd"] ?? Path.GetDirectoryName(Path.GetFullPath(program))!;
        var stopOnEntry = _launchArgs?["stopOnEntry"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        _rt = new Interpreter(this, cwd);
        _rt.Status += m => Output(m, "console");
        _debugger = new Debugger(_rt) { Host = this, BreakOnErrors = true };
        foreach (var (file, lines) in _pendingBreakpoints) _debugger.SetLineBreakpoints(file, lines);
        if (stopOnEntry) _debugger.RequestPause();
        _worker = new Thread(() =>
        {
            int exitCode = 0;
            try
            {
                _rt.ExecuteCommand("SET TALK OFF");
                _rt.RunProgram(Path.GetFullPath(program));
            }
            catch (QuitException) { }
            catch (VfpException ex)
            {
                Output($"Error {ex.Number}: {ex.Message}{(ex.ErrorLine is { } l ? $" (line {l})" : "")}\n", "stderr");
                exitCode = 1;
            }
            catch (Exception ex) when (ex is not ThreadInterruptedException)
            {
                Output(ex.Message + "\n", "stderr");
                exitCode = 1;
            }
            finally
            {
                foreach (var s in _rt.Sessions.ToList()) s.Dispose();
            }
            _terminated = true;
            Event("exited", new JsonObject { ["exitCode"] = exitCode });
            Event("terminated");
        }) { IsBackground = true, Name = "JoePro program" };
        _worker.Start();
    }

    // ---- IDebugHost (program thread) --------------------------------------------------------

    public DebugAction Paused(DebugStop stop)
    {
        _stop = stop;
        Event("stopped", new JsonObject
        {
            ["reason"] = stop.Reason switch
            {
                StopReason.Breakpoint => "breakpoint",
                StopReason.Step => "step",
                StopReason.Exception => "exception",
                StopReason.Pause => "pause",
                _ => "pause",
            },
            ["description"] = stop.Message,
            ["text"] = stop.Message,
            ["threadId"] = ThreadId,
            ["allThreadsStopped"] = true,
        });
        try
        {
            foreach (var req in _pausedRequests.GetConsumingEnumerable())
            {
                var action = HandlePaused(req, stop);
                if (action != null) return action.Value;
            }
            return DebugAction.Cancel;
        }
        finally
        {
            _stop = null;
        }
    }

    private DebugAction? HandlePaused(JsonObject req, DebugStop stop)
    {
        var args = req["arguments"] as JsonObject;
        switch ((string?)req["command"])
        {
            case "stackTrace":
            {
                var frames = new JsonArray();
                foreach (var f in stop.Frames)
                {
                    var frame = new JsonObject { ["id"] = f.Id, ["name"] = f.Name, ["line"] = f.Line, ["column"] = 1 };
                    if (f.File != null) frame["source"] = new JsonObject { ["name"] = Path.GetFileName(f.File), ["path"] = f.File };
                    frames.Add(frame);
                }
                Respond(req, new JsonObject { ["stackFrames"] = frames, ["totalFrames"] = stop.Frames.Count });
                return null;
            }
            case "scopes":
            {
                var frameId = (int)args!["frameId"]!;
                Respond(req, new JsonObject
                {
                    ["scopes"] = new JsonArray(
                        new JsonObject { ["name"] = "Locals", ["variablesReference"] = LocalsBase + frameId, ["expensive"] = false },
                        new JsonObject { ["name"] = "Publics", ["variablesReference"] = PublicsRef, ["expensive"] = false }),
                });
                return null;
            }
            case "variables":
            {
                var reference = (int)args!["variablesReference"]!;
                IReadOnlyList<VariableInfo> vars = reference switch
                {
                    PublicsRef => stop.Publics(),
                    >= LocalsBase => stop.Locals(reference - LocalsBase),
                    _ => stop.Children(reference),
                };
                var arr = new JsonArray();
                foreach (var v in vars) arr.Add(VariableJson(v));
                Respond(req, new JsonObject { ["variables"] = arr });
                return null;
            }
            case "evaluate":
            {
                var frameId = args?["frameId"] is JsonValue fv && fv.TryGetValue<int>(out var fid) ? fid : 0;
                var r = stop.Evaluate((string)args!["expression"]!, frameId);
                Respond(req, new JsonObject { ["result"] = r.Value, ["type"] = r.Type, ["variablesReference"] = r.ChildrenRef });
                return null;
            }
            case "setVariable":
            {
                var reference = (int)args!["variablesReference"]!;
                var frameId = reference >= LocalsBase ? reference - LocalsBase : 0;
                try
                {
                    stop.SetVariable(frameId, (string)args["name"]!, (string)args["value"]!);
                    var updated = stop.Evaluate((string)args["name"]!, frameId);
                    Respond(req, new JsonObject { ["value"] = updated.Value, ["type"] = updated.Type });
                }
                catch (VfpException ex) { Respond(req, null, success: false, message: ex.Message); }
                return null;
            }
            case "continue":
                Respond(req, new JsonObject { ["allThreadsContinued"] = true });
                return DebugAction.Continue;
            case "next":
                Respond(req, null);
                return DebugAction.StepOver;
            case "stepIn":
                Respond(req, null);
                return DebugAction.StepInto;
            case "stepOut":
                Respond(req, null);
                return DebugAction.StepOut;
            case "disconnect" or "terminate":
                return DebugAction.Cancel;
            default:
                Respond(req, null);
                return null;
        }
    }

    private static JsonObject VariableJson(VariableInfo v) => new()
    {
        ["name"] = v.Name,
        ["value"] = v.Value,
        ["type"] = v.Type,
        ["variablesReference"] = v.ChildrenRef,
    };

    public void Output(string text) => Output(text + "\n", "console");

    private void Output(string text, string category) =>
        Event("output", new JsonObject { ["category"] = category, ["output"] = text });

    // ---- IConsoleOutput: ? and LIST output go to the debug console ---------------------------

    void IConsoleOutput.Write(string text)
    {
        Output(text, "stdout");
        var nl = text.LastIndexOf('\n');
        Column = nl >= 0 ? text.Length - nl - 1 : Column + text.Length;
    }

    void IConsoleOutput.NewLine()
    {
        Output("\n", "stdout");
        Column = 0;
    }

    // ---- Protocol helpers ------------------------------------------------------------------

    private void Respond(JsonObject request, JsonNode? body, bool success = true, string? message = null)
    {
        var msg = new JsonObject
        {
            ["seq"] = Interlocked.Increment(ref _seq),
            ["type"] = "response",
            ["request_seq"] = request["seq"]?.DeepClone(),
            ["success"] = success,
            ["command"] = (string?)request["command"],
        };
        if (message != null) msg["message"] = message;
        if (body != null) msg["body"] = body;
        _channel.Write(msg);
    }

    private void Event(string name, JsonObject? body = null)
    {
        var msg = new JsonObject { ["seq"] = Interlocked.Increment(ref _seq), ["type"] = "event", ["event"] = name };
        if (body != null) msg["body"] = body;
        _channel.Write(msg);
    }

    public bool Terminated => _terminated;
}
