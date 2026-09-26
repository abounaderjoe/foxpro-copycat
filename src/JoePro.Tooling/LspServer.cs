using System.Text.Json.Nodes;

namespace JoePro.Tooling;

/// <summary>
/// A Language Server Protocol server for FoxPro (`joepro lsp`), so editors such as VS Code get Joe Pro's
/// diagnostics, completion, hover, go-to-definition and document symbols. Documents are fully synchronized.
/// </summary>
public sealed class LspServer
{
    private readonly JsonRpcChannel _channel;
    private readonly Dictionary<string, string> _documents = new();
    private readonly LanguageService _service;
    private string? _root;
    private bool _shutdown;

    public LspServer(Stream input, Stream output)
    {
        _channel = new JsonRpcChannel(input, output);
        _service = new LanguageService(null, WorkspaceFiles);
    }

    private IEnumerable<string> WorkspaceFiles()
    {
        if (_root == null || !Directory.Exists(_root)) return [];
        return Directory.EnumerateFiles(_root, "*.prg", SearchOption.AllDirectories).Take(5000);
    }

    /// <summary>Processes messages until "exit" or end of input. Returns the process exit code.</summary>
    public int Run()
    {
        while (true)
        {
            var msg = _channel.Read();
            if (msg == null) return _shutdown ? 0 : 1;
            var method = (string?)msg["method"];
            var id = msg["id"]?.DeepClone();
            try
            {
                if (method == "exit") return _shutdown ? 0 : 1;
                var result = Handle(method, msg["params"] as JsonObject);
                if (id != null) _channel.Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (Exception ex) when (id != null)
            {
                _channel.Write(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message },
                });
            }
        }
    }

    private JsonNode? Handle(string? method, JsonObject? p)
    {
        switch (method)
        {
            case "initialize":
                _root = p?["rootUri"] is JsonValue ru && ru.TryGetValue<string>(out var r) ? PathOf(r) : (string?)p?["rootPath"];
                return new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        ["textDocumentSync"] = 1,
                        ["completionProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray(".") },
                        ["hoverProvider"] = true,
                        ["definitionProvider"] = true,
                        ["documentSymbolProvider"] = true,
                        ["referencesProvider"] = true,
                        ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
                        ["signatureHelpProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ",") },
                    },
                    ["serverInfo"] = new JsonObject { ["name"] = "joepro", ["version"] = "0.1" },
                };
            case "initialized":
                return null;
            case "shutdown":
                _shutdown = true;
                return null;
            case "textDocument/didOpen":
            {
                var doc = p!["textDocument"]!;
                var uri = (string)doc["uri"]!;
                _documents[uri] = (string)doc["text"]!;
                Publish(uri);
                return null;
            }
            case "textDocument/didChange":
            {
                var uri = (string)p!["textDocument"]!["uri"]!;
                var changes = p["contentChanges"]!.AsArray();
                if (changes.Count > 0) _documents[uri] = (string)changes[^1]!["text"]!;
                Publish(uri);
                return null;
            }
            case "textDocument/didClose":
            {
                var uri = (string)p!["textDocument"]!["uri"]!;
                _documents.Remove(uri);
                _channel.Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/publishDiagnostics", ["params"] = new JsonObject { ["uri"] = uri, ["diagnostics"] = new JsonArray() } });
                return null;
            }
            case "textDocument/completion":
            {
                var (text, line, col) = Position(p!);
                var items = new JsonArray();
                foreach (var i in _service.Complete(text, line, col))
                    items.Add(new JsonObject
                    {
                        ["label"] = i.Label,
                        ["kind"] = LspKind(i.Kind),
                        ["detail"] = i.Detail,
                        ["insertText"] = i.InsertText ?? i.Label,
                    });
                return new JsonObject { ["isIncomplete"] = false, ["items"] = items };
            }
            case "textDocument/hover":
            {
                var (text, line, col) = Position(p!);
                var hover = _service.Hover(text, line, col);
                return hover == null ? null : new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = "```foxpro\n" + hover + "\n```" } };
            }
            case "textDocument/definition":
            {
                var uri = (string)p!["textDocument"]!["uri"]!;
                var (text, line, col) = Position(p);
                var loc = _service.Definition(text, line, col, PathOf(uri));
                if (loc == null) return null;
                return new JsonObject
                {
                    ["uri"] = loc.File != null ? new Uri(Path.GetFullPath(loc.File)).AbsoluteUri : uri,
                    ["range"] = Range(loc.Line, 1, loc.Line, 1),
                };
            }
            case "textDocument/references":
            {
                var uri = (string)p!["textDocument"]!["uri"]!;
                var (text, line, col) = Position(p);
                var arr = new JsonArray();
                foreach (var refSpan in _service.References(text, line, col, PathOf(uri)))
                    arr.Add(new JsonObject { ["uri"] = refSpan.File != null ? new Uri(Path.GetFullPath(refSpan.File)).AbsoluteUri : uri, ["range"] = Range(refSpan.Line, refSpan.Column, refSpan.Line, refSpan.Column + refSpan.Length) });
                return arr;
            }
            case "textDocument/prepareRename":
            {
                var (text, line, col) = Position(p!);
                var at = LanguageService.WordAt(text, line, col);
                if (at == null || _service.RenameTargets(text, line, col, "x", null) == null) return null;
                return new JsonObject { ["range"] = Range(line, at.Value.StartColumn, line, at.Value.StartColumn + at.Value.Word.Length), ["placeholder"] = at.Value.Word };
            }
            case "textDocument/rename":
            {
                var uri = (string)p!["textDocument"]!["uri"]!;
                var (text, line, col) = Position(p);
                var newName = (string)p["newName"]!;
                var targets = _service.RenameTargets(text, line, col, newName, PathOf(uri));
                if (targets == null) return null;
                var changes = new JsonObject();
                foreach (var g in targets.GroupBy(t => t.File != null ? new Uri(Path.GetFullPath(t.File)).AbsoluteUri : uri))
                {
                    var edits = new JsonArray();
                    foreach (var t in g) edits.Add(new JsonObject { ["range"] = Range(t.Line, t.Column, t.Line, t.Column + t.Length), ["newText"] = newName });
                    changes[g.Key] = edits;
                }
                return new JsonObject { ["changes"] = changes };
            }
            case "textDocument/signatureHelp":
            {
                var (text, line, col) = Position(p!);
                var sig = _service.SignatureHelp(text, line, col);
                if (sig == null) return null;
                var ps = new JsonArray();
                foreach (var prm in sig.Parameters) ps.Add(new JsonObject { ["label"] = prm });
                return new JsonObject
                {
                    ["signatures"] = new JsonArray(new JsonObject { ["label"] = sig.Label, ["documentation"] = sig.Documentation, ["parameters"] = ps }),
                    ["activeSignature"] = 0,
                    ["activeParameter"] = sig.ActiveParameter,
                };
            }
            case "textDocument/documentSymbol":
            {
                var uri = (string)p!["textDocument"]!["uri"]!;
                var arr = new JsonArray();
                foreach (var s in _service.Symbols(_documents.GetValueOrDefault(uri, ""))) arr.Add(SymbolJson(s));
                return arr;
            }
            default:
                return null;
        }
    }

    private (string Text, int Line, int Column) Position(JsonObject p)
    {
        var uri = (string)p["textDocument"]!["uri"]!;
        var pos = p["position"]!;
        return (_documents.GetValueOrDefault(uri, ""), (int)pos["line"]! + 1, (int)pos["character"]! + 1);
    }

    private void Publish(string uri)
    {
        var diags = new JsonArray();
        foreach (var d in _service.Diagnostics(_documents[uri], PathOf(uri)))
            diags.Add(new JsonObject
            {
                ["range"] = Range(d.Line, d.Column, d.Line, d.Column + d.Length),
                ["severity"] = (int)d.Severity,
                ["code"] = d.Code,
                ["source"] = "joepro",
                ["message"] = d.Message,
            });
        _channel.Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "textDocument/publishDiagnostics", ["params"] = new JsonObject { ["uri"] = uri, ["diagnostics"] = diags } });
    }

    private static JsonObject Range(int l1, int c1, int l2, int c2) => new()
    {
        ["start"] = new JsonObject { ["line"] = l1 - 1, ["character"] = c1 - 1 },
        ["end"] = new JsonObject { ["line"] = l2 - 1, ["character"] = c2 - 1 },
    };

    private static JsonObject SymbolJson(DocumentSymbol s)
    {
        var children = new JsonArray();
        foreach (var c in s.Children) children.Add(SymbolJson(c));
        return new JsonObject
        {
            ["name"] = s.Name,
            ["detail"] = s.Detail,
            ["kind"] = s.Kind switch { SymbolKind.Class => 5, SymbolKind.Method => 6, SymbolKind.Property => 7, _ => 12 },
            ["range"] = Range(s.Line, 1, s.EndLine, 1),
            ["selectionRange"] = Range(s.Line, 1, s.Line, 1),
            ["children"] = children,
        };
    }

    private static int LspKind(CompletionKind k) => k switch
    {
        CompletionKind.Keyword => 14, CompletionKind.Function => 3, CompletionKind.Procedure => 3, CompletionKind.Class => 7,
        CompletionKind.Variable => 6, CompletionKind.Field => 5, CompletionKind.Property => 10, CompletionKind.Method => 2,
        CompletionKind.Table => 22, _ => 15,
    };

    private static string? PathOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null;
}
