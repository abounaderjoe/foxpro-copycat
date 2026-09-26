using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using JoePro.Core;
using JoePro.Language;

namespace JoePro.Migration;

/// <summary>
/// Analyzes FoxPro program code (PRG files, stored procedures, method code). Joe Pro runs FoxPro code
/// in compatibility mode, so "conversion" means compiling it and flagging constructs that need a person.
/// </summary>
public sealed class ProgramAnalyzer
{
    private readonly MigrationReport _report;

    public ProgramAnalyzer(MigrationReport report) => _report = report;

    /// <summary>Folders searched for #INCLUDE files after the program's own folder (the migration source root).</summary>
    public List<string> IncludeRoots { get; } = new();

    private string? _currentPath;

    /// <summary>
    /// Finds an #INCLUDE file the way VFP would: next to the program, then in the include roots (and, as a last
    /// resort, anywhere below them), ignoring case.
    /// </summary>
    private string? ResolveInclude(string name)
    {
        name = name.Trim().Trim('"', '\'', '[', ']').Replace('\\', Path.DirectorySeparatorChar);
        var dirs = new List<string>();
        if (_currentPath != null) dirs.Add(Path.GetDirectoryName(_currentPath)!);
        dirs.AddRange(IncludeRoots);
        foreach (var d in dirs)
            if (JoePro.Data.DataSession.FindIgnoringCase(Path.Combine(d, name)) is { } hit) return File.ReadAllText(hit, System.Text.Encoding.Latin1);
        foreach (var root in IncludeRoots.Where(Directory.Exists))
        {
            var match = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f).Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));
            if (match != null) return File.ReadAllText(match, System.Text.Encoding.Latin1);
        }
        return null;
    }

    public void AnalyzeFile(string path, string displayName)
    {
        string text;
        try { text = File.ReadAllText(path, System.Text.Encoding.Latin1); }
        catch (IOException ex)
        {
            _report.Add(FindingStatus.Failed, "CODE.READ", "code", new SourceLocation(displayName), ex.Message);
            return;
        }
        if (Path.GetExtension(path).Equals(".h", StringComparison.OrdinalIgnoreCase))
        {
            _report.Add(FindingStatus.Converted, "CODE.HEADER", "code", new SourceLocation(displayName), "Header file is used as-is by #INCLUDE.");
            return;
        }
        _currentPath = path;
        try { AnalyzeSource(text, displayName, null); }
        finally { _currentPath = null; }
    }

    public void AnalyzeSource(string source, string file, string? objectName)
    {
        var before = _report.Findings.Count;
        var lines = source.Replace("\r\n", "\n").Split('\n');
        ProgramUnit unit;
        try
        {
            unit = Parser.ParseProgram(source, Path.GetFileNameWithoutExtension(file), null, ResolveInclude);
        }
        catch (CompileException ex)
        {
            _report.Add(FindingStatus.Failed, "CODE.COMPILE", "code", new SourceLocation(file, objectName, Line: ex.Line, Snippet: Snip(lines, ex.Line)),
                $"Does not compile: {ex.Message}", "Fix the syntax error (it may also fail in VFP) or report a Joe Pro parser gap.");
            return;
        }
        void Visit(string member, IEnumerable<Stmt> body)
        {
            foreach (var s in body) Walk(s, member, file, objectName, lines, s.Line);
        }
        Visit("(main)", unit.Main);
        foreach (var p in unit.Procedures.Values) Visit(p.Name, p.Body);
        foreach (var c in unit.Classes.Values)
            foreach (var m in c.Methods.Values) Visit(c.Name + "." + m.Name, m.Body);

        // Text-level checks for constructs the parser skips.
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (Regex.IsMatch(l, @"^DECLARE\s+(\w+\s+)?\w+\s+IN\s+", RegexOptions.IgnoreCase))
                Add(FindingStatus.Unsupported, "CODE.DLL.DECLARE", file, objectName, null, i + 1, lines,
                    "Win32 API declaration (DECLARE … IN). Calling native DLLs is planned for the Windows runtime.",
                    "Replace with a Joe Pro built-in if one exists; otherwise keep for the Windows DLL bridge.");
            else if (Regex.IsMatch(l, @"^SET\s+LIBR(A|AR|ARY)?\s+TO\s+", RegexOptions.IgnoreCase))
                Add(FindingStatus.Unsupported, "CODE.FLL", file, objectName, null, i + 1, lines,
                    "FoxPro API library (.FLL) cannot be loaded.", "Replace the library functions with built-ins or .NET code.");
        }
        if (_report.Findings.Count == before)
            _report.Add(FindingStatus.Converted, "CODE.COMPILES", "code", new SourceLocation(file, objectName),
                "Compiles and runs in compatibility mode with no constructs needing review.");
    }

    private void Add(FindingStatus status, string rule, string file, string? obj, string? member, int line, string[] lines, string message, string? action = null) =>
        _report.Add(status, rule, "code", new SourceLocation(file, obj ?? member, obj != null ? member : null, line > 0 ? line : null, Snip(lines, line)), message, action);

    private static string? Snip(string[] lines, int line) =>
        line >= 1 && line <= lines.Length ? lines[line - 1].Trim() : null;

    private void Walk(object? node, string member, string file, string? obj, string[] lines, int line)
    {
        switch (node)
        {
            case null:
                return;
            case Stmt s:
                if (s.Line > 0) line = s.Line;
                Check(s, member, file, obj, lines, line);
                break;
            case Expr e:
                Check(e, member, file, obj, lines, line);
                break;
            case string or Value or Token:
                return;
            case ITuple t:
                for (int i = 0; i < t.Length; i++) Walk(t[i], member, file, obj, lines, line);
                return;
            case IEnumerable seq:
                foreach (var x in seq) Walk(x, member, file, obj, lines, line);
                return;
            default:
                if (!node.GetType().Namespace!.StartsWith("JoePro.Language")) return;
                break;
        }
        foreach (var p in node.GetType().GetProperties())
        {
            if (p.GetIndexParameters().Length > 0 || p.Name is "EqualityContract" or "Line") continue;
            Walk(p.GetValue(node), member, file, obj, lines, line);
        }
    }

    private void Check(object node, string member, string file, string? obj, string[] lines, int line)
    {
        switch (node)
        {
            case MacroStmt or MacroExpr:
                Add(FindingStatus.NeedsReview, "CODE.MACRO.DYNAMIC", file, obj, member, line, lines,
                    "Macro substitution builds code at run time, so it cannot be verified before it runs.",
                    "Runs unchanged in compatibility mode. Check which values the macro can take; prefer EVALUATE() or name expressions.");
                break;
            case NoOpStmt { Verb: "@ SAY/GET" }:
                Add(FindingStatus.Unsupported, "CODE.SCREEN.SAYGET", file, obj, member, line, lines,
                    "Legacy @ … SAY/GET screen code is not supported.", "Rebuild the screen as a form (Form Designer, Phase 3).");
                break;
            case NoOpStmt { Verb: "ON" }:
                Add(FindingStatus.NeedsReview, "CODE.ON.EVENT", file, obj, member, line, lines,
                    "ON KEY LABEL / ON SHUTDOWN / ON ESCAPE handlers are ignored until the UI runtime ships (Phase 3).");
                break;
            case SetStmt { Option: "__REPORT" or "__LABEL" }:
                Add(FindingStatus.NeedsReview, "CODE.UI.REPORT", file, obj, member, line, lines,
                    "REPORT FORM / LABEL FORM needs the report engine (Phase 4).");
                break;
            case SetStmt st when st.Option.StartsWith("__") && st.Option is "__MODIFY" or "__MODI" or "__BUILD" or "__KEYBOARD":
                Add(FindingStatus.NeedsReview, "CODE.IDE.COMMAND", file, obj, member, line, lines,
                    $"{st.Option[2..]} is an interactive/IDE command that has no effect in the runtime yet.");
                break;
            case CallExpr { Name: var n } c when (n.Equals("CREATEOBJECT", StringComparison.OrdinalIgnoreCase) || n.Equals("GETOBJECT", StringComparison.OrdinalIgnoreCase))
                                                 && c.Args.Count > 0 && c.Args[0] is LiteralExpr { Value.Kind: ValueKind.Character } l && l.Value.AsString.Contains('.'):
                Add(FindingStatus.NeedsReview, "CODE.COM.AUTOMATION", file, obj, member, line, lines,
                    $"COM automation object '{l.Value.AsString}' requires the COM bridge (Windows, Phase 2).",
                    "Keep for Windows deployments or replace with a .NET library.");
                break;
            case CallExpr { Name: var n2 } c2 when n2.Equals("SYS", StringComparison.OrdinalIgnoreCase) && c2.Args.Count > 0 && c2.Args[0] is LiteralExpr { Value.Kind: ValueKind.Number } sl
                                                  && !SupportedSys.Contains((int)sl.Value.AsNumber):
                Add(FindingStatus.NeedsReview, "CODE.SYS.FUNCTION", file, obj, member, line, lines,
                    $"SYS({(int)sl.Value.AsNumber}) is not implemented yet and returns an empty string.");
                break;
        }
    }

    private static readonly HashSet<int> SupportedSys = [0, 1, 2, 3, 5, 6, 10, 11, 12, 16, 987, 1037, 2003, 2004, 2015, 2018, 2019, 2023, 3050, 3054];
}
