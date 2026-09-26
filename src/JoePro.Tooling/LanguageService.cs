using System.Text.RegularExpressions;
using JoePro.Core;
using JoePro.Language;
using JoePro.Runtime;

namespace JoePro.Tooling;

public enum DiagnosticSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

/// <summary>A problem in a source file. Lines and columns are 1-based.</summary>
public sealed record Diagnostic(int Line, int Column, int Length, DiagnosticSeverity Severity, string Message, string Code);

public enum CompletionKind { Keyword, Function, Procedure, Class, Variable, Field, Property, Method, Table, Snippet }

public sealed record CompletionItem(string Label, CompletionKind Kind, string? Detail = null, string? InsertText = null);

public sealed record SymbolLocation(string? File, int Line, int Column = 1);

public enum SymbolKind { Procedure, Function, Class, Method, Property }

public sealed record DocumentSymbol(string Name, SymbolKind Kind, int Line, int EndLine, IReadOnlyList<DocumentSymbol> Children, string? Detail = null);

/// <summary>
/// Editor intelligence for FoxPro code, shared by the IDE and the LSP server: diagnostics, completion,
/// hover, go-to-definition and document symbols. When a live <see cref="Interpreter"/> is supplied,
/// completion and hover also use open tables, variables and objects.
/// </summary>
public sealed class LanguageService
{
    private readonly Interpreter? _live;
    private readonly Func<IEnumerable<string>> _workspaceFiles;

    public LanguageService(Interpreter? live = null, Func<IEnumerable<string>>? workspaceFiles = null)
    {
        _live = live;
        _workspaceFiles = workspaceFiles ?? (() => []);
    }

    private static readonly Regex WordRx = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    // ================================================================================
    // Diagnostics
    // ================================================================================

    public IReadOnlyList<Diagnostic> Diagnostics(string text, string? file = null)
    {
        var list = new List<Diagnostic>();
        var lines = SplitLines(text);
        ProgramUnit unit;
        try
        {
            unit = Parser.ParseProgram(text, file != null ? Path.GetFileNameWithoutExtension(file) : "", file);
        }
        catch (CompileException ex)
        {
            var line = Math.Clamp(ex.Line, 1, Math.Max(1, lines.Length));
            var content = lines.Length >= line ? lines[line - 1] : "";
            var start = content.Length - content.TrimStart().Length;
            list.Add(new Diagnostic(line, start + 1, Math.Max(1, content.Trim().Length), DiagnosticSeverity.Error, ex.Message, "JP0001"));
            return list;
        }

        // Calls to functions that are neither built in nor defined anywhere we can see.
        var known = KnownProcedures(unit);
        for (int i = 0; i < lines.Length; i++)
        {
            var code = CodePart(lines[i]);
            foreach (Match m in Regex.Matches(code, @"(?<![\.\w&])([A-Za-z_]\w*)\s*\("))
            {
                var name = m.Groups[1].Value;
                if (IsKnownCallable(name, known) || IsArrayOrKeyword(name, code, m.Index)) continue;
                list.Add(new Diagnostic(i + 1, m.Groups[1].Index + 1, name.Length, DiagnosticSeverity.Information,
                    $"'{name}' is not a built-in function or a procedure defined in this file or the workspace. It may come from SET PROCEDURE or a program file on the path.", "JP1001"));
            }
            var raw = Preprocessor.StripInlineComment(lines[i]);
            if (code.Length > 0 && Regex.IsMatch(raw, @"^\s*(IF|CASE|DO\s+WHILE|LOCATE\s+FOR)\b.*(<>|!=|#)\s*(""""|'')", RegexOptions.IgnoreCase))
            {
                var col = raw.IndexOfAny(['<', '!', '#']);
                list.Add(new Diagnostic(i + 1, col + 1, 2, DiagnosticSeverity.Warning,
                    "With SET EXACT OFF, 'x <> \"\"' is always false. Use !EMPTY(x) instead.", "JP2001"));
            }
        }
        return list;
    }

    private static readonly HashSet<string> NotFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "IF", "AND", "OR", "NOT", "IN", "WHERE", "ON", "VALUES", "INTO", "DIMENSION", "DECLARE", "LOCAL", "PUBLIC", "PRIVATE",
        "PROCEDURE", "FUNCTION", "CASE", "WHILE", "RETURN", "ARRAY", "EXISTS", "TABLE", "CURSOR", "FROM", "JOIN", "SELECT", "C", "N", "V",
        "Q", "F", "B", "CHAR", "CHARACTER", "VARCHAR", "NUMERIC", "FLOAT", "DOUBLE", "INTEGER", "INT", "CURRENCY", "DATE", "DATETIME",
        "LOGICAL", "MEMO", "GENERAL", "BLOB", "VARBINARY", "LPARAMETERS", "PARAMETERS", "TO", "WITH", "HAVING", "BY", "COUNT", "SUM", "AVG",
        "MIN", "MAX", "CNT", "THEN", "ELSE", "AS", "CHECK", "DEFAULT", "ERROR",
    };

    private static bool IsArrayOrKeyword(string name, string line, int index) =>
        NotFunctions.Contains(name) || Regex.IsMatch(line, $@"\b(DIMENSION|DIME|DECLARE|LOCAL\s+ARRAY|PUBLIC\s+ARRAY)\b[^\n]*\b{Regex.Escape(name)}\s*\(", RegexOptions.IgnoreCase);

    private HashSet<string> KnownProcedures(ProgramUnit unit)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.UnionWith(unit.Procedures.Keys);
        foreach (var file in _workspaceFiles())
        {
            set.Add(Path.GetFileNameWithoutExtension(file));
            foreach (var s in QuickSymbols(file)) set.Add(s.Name);
        }
        return set;
    }

    private bool IsKnownCallable(string name, HashSet<string> known) =>
        known.Contains(name) || JoePro.Runtime.Builtins.Library.TryGet(name, out _) || JoePro.Runtime.Builtins.Library.IsAggregate(name)
        || (_live?.FindVariable(name)?.IsArray ?? false);

    /// <summary>The part of a line before a && comment (strings are not inspected for calls).</summary>
    private static string CodePart(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith('*') || t.StartsWith("NOTE ", StringComparison.OrdinalIgnoreCase)) return "";
        var s = Preprocessor.StripInlineComment(line);
        return Regex.Replace(s, "\"[^\"]*\"|'[^']*'", m => new string(' ', m.Length));
    }

    // ================================================================================
    // Completion
    // ================================================================================

    public IReadOnlyList<CompletionItem> Complete(string text, int line, int column)
    {
        var lines = SplitLines(text);
        var lineText = line >= 1 && line <= lines.Length ? lines[line - 1] : "";
        var before = lineText[..Math.Clamp(column - 1, 0, lineText.Length)];
        var prefixMatch = Regex.Match(before, @"([A-Za-z_][A-Za-z0-9_]*)?$");
        var prefix = prefixMatch.Value;
        var head = before[..^prefix.Length];

        IEnumerable<CompletionItem> items;
        var member = Regex.Match(head, @"([A-Za-z_][A-Za-z0-9_\.]*)\.\s*$");
        if (member.Success && !Regex.IsMatch(head, @"\d\.\s*$"))
            items = MemberCompletions(member.Groups[1].Value, text, line);
        else if (head.TrimEnd().Length == 0)
            items = StatementCompletions(text);
        else
            items = ExpressionCompletions(text);

        return items
            .Where(i => prefix.Length == 0 || i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.Label, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(i => i.Kind is CompletionKind.Field or CompletionKind.Property or CompletionKind.Variable ? 0 : 1)
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(200).ToList();
    }

    private IEnumerable<CompletionItem> StatementCompletions(string text) =>
        FunctionDocs.Keywords.Select(k => new CompletionItem(k, CompletionKind.Keyword, FunctionDocs.Commands.GetValueOrDefault(k.Split(' ')[0])))
            .Concat(ExpressionCompletions(text));

    private IEnumerable<CompletionItem> ExpressionCompletions(string text)
    {
        foreach (var name in JoePro.Runtime.Builtins.Library.Names.Where(n => !n.StartsWith("__")))
        {
            var doc = FunctionDocs.Functions.GetValueOrDefault(name);
            yield return new CompletionItem(name, CompletionKind.Function, doc?.Signature ?? name + "()", name + "(");
        }
        var unit = TryParse(text);
        if (unit != null)
        {
            foreach (var p in unit.Procedures.Values)
                yield return new CompletionItem(p.Name, p.IsFunction ? CompletionKind.Function : CompletionKind.Procedure, Signature(p));
            foreach (var c in unit.Classes.Values)
                yield return new CompletionItem(c.Name, CompletionKind.Class, $"DEFINE CLASS {c.Name} AS {c.Parent}");
        }
        foreach (var v in DeclaredVariables(text)) yield return new CompletionItem(v, CompletionKind.Variable, "variable");
        if (_live != null)
        {
            foreach (var wa in _live.Session.OpenWorkAreas())
            {
                yield return new CompletionItem(wa.Alias, CompletionKind.Table, $"work area {wa.Number}: {wa.Source}");
                if (wa == _live.Session.Current)
                    foreach (var f in wa.Table.Fields) yield return new CompletionItem(f.Name.ToLowerInvariant(), CompletionKind.Field, FieldDetail(wa.Alias, f));
            }
            foreach (var v in _live.VisibleVariables()) yield return new CompletionItem(v.Name, CompletionKind.Variable, v.IsArray ? "array" : Formatter.ToDisplay(v.Value, _live.Options).Trim());
        }
        foreach (var (name, sig) in new[] { ("THIS", "the current object"), ("THISFORM", "the current form"), ("THISFORMSET", "the current form set") })
            yield return new CompletionItem(name, CompletionKind.Keyword, sig);
    }

    private static string FieldDetail(string alias, FieldDef f) =>
        $"{alias}.{f.Name} {f.Type}({f.Width}{(f.Decimals > 0 ? "," + f.Decimals : "")}){(f.Nullable ? " NULL" : "")}";

    private IEnumerable<CompletionItem> MemberCompletions(string target, string text, int line)
    {
        var upper = target.ToUpperInvariant();
        // alias.field
        if (_live?.Session.FindAlias(target) is { } wa)
            return wa.Table.Fields.Select(f => new CompletionItem(f.Name.ToLowerInvariant(), CompletionKind.Field, FieldDetail(wa.Alias, f)));
        // m.variable
        if (upper == "M")
            return DeclaredVariables(text).Select(v => new CompletionItem(v, CompletionKind.Variable, "variable"))
                .Concat(_live?.VisibleVariables().Select(v => new CompletionItem(v.Name, CompletionKind.Variable)) ?? []);
        // Live object variable
        if (_live?.FindVariable(target.Split('.')[0]) is { Value.Kind: ValueKind.Object } ov && ov.Value.AsObject is VfpObject obj)
        {
            foreach (var part in target.Split('.').Skip(1))
                obj = obj.FindProperty(part)?.Value is { Kind: ValueKind.Object } pv ? (VfpObject)pv.AsObject : obj;
            return ObjectMembers(obj);
        }
        // THIS / THISFORM inside a class definition: members from the class source and its base class.
        // The line being typed is incomplete, so parse without it.
        var lines = SplitLines(text);
        if (line >= 1 && line <= lines.Length) lines[line - 1] = "";
        var unit = TryParse(text) ?? TryParse(string.Join("\n", lines));
        if (unit != null && (upper.StartsWith("THIS") || upper == "THISFORM"))
        {
            var cls = unit.Classes.Values.FirstOrDefault(c => line >= c.Line && line <= EndOfClass(text, c.Line));
            if (cls != null) return ClassMembers(cls, unit, target.Split('.').Skip(1).ToList());
        }
        // Base class name typed directly (for example after CREATEOBJECT results assigned elsewhere): offer common members.
        return BaseMembers("Custom");
    }

    private static IEnumerable<CompletionItem> ObjectMembers(VfpObject o)
    {
        foreach (var (name, v) in o.Properties)
            yield return new CompletionItem(name, v.Value.Kind == ValueKind.Object ? CompletionKind.Property : CompletionKind.Property,
                v.Value.Kind == ValueKind.Object ? $"({((VfpObject)v.Value.AsObject).Class.Name})" : v.Value.VarType.ToString());
        foreach (var c in o.Class.Hierarchy().Where(h => h.Definition != null))
            foreach (var m in c.Definition!.Methods.Values)
                yield return new CompletionItem(m.Name, CompletionKind.Method, Signature(m));
        foreach (var m in NativeMethods(o.Class.BaseClass)) yield return new CompletionItem(m, CompletionKind.Method, "native method");
    }

    private IEnumerable<CompletionItem> ClassMembers(ClassDef cls, ProgramUnit unit, List<string> path)
    {
        var baseName = cls.Parent;
        var def = cls;
        foreach (var part in path)
        {
            var member = def.Objects.FirstOrDefault(o => o.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (member == null) return BaseMembers(baseName);
            baseName = member.Class;
            if (!unit.Classes.TryGetValue(member.Class, out var md)) return BaseMembers(member.Class);
            def = md;
        }
        var items = new List<CompletionItem>();
        for (var c = def; c != null; c = unit.Classes.GetValueOrDefault(c.Parent))
        {
            items.AddRange(c.Members.Where(m => !m.Name.Contains('.')).Select(m => new CompletionItem(m.Name, CompletionKind.Property, "property")));
            items.AddRange(c.Objects.Where(o => !o.Name.Contains('.')).Select(o => new CompletionItem(o.Name, CompletionKind.Property, $"({o.Class})")));
            items.AddRange(c.Methods.Values.Where(m => !m.Name.Contains('.')).Select(m => new CompletionItem(m.Name, CompletionKind.Method, Signature(m))));
            baseName = c.Parent;
        }
        items.AddRange(BaseMembers(baseName));
        return items;
    }

    private static IEnumerable<CompletionItem> BaseMembers(string baseClass)
    {
        var canonical = BaseClasses.Exists(baseClass) ? BaseClasses.Canonical(baseClass) : "Custom";
        var probe = new VfpObject(new ClassInfo(canonical, canonical, null, null, null));
        BaseClasses.InitializeNative(probe, canonical);
        foreach (var p in probe.Properties.Keys) yield return new CompletionItem(p, CompletionKind.Property, $"{canonical} property");
        foreach (var m in NativeMethods(canonical)) yield return new CompletionItem(m, CompletionKind.Method, $"{canonical} method");
    }

    private static IEnumerable<string> NativeMethods(string baseClass)
    {
        string[] common = ["AddProperty", "RemoveProperty", "AddObject", "NewObject", "RemoveObject", "ResetToDefault", "Init", "Destroy", "Error"];
        string[] visual = ["Refresh", "SetFocus", "Show", "Hide", "Release", "Click", "DblClick", "GotFocus", "LostFocus", "When", "Valid", "InteractiveChange", "ProgrammaticChange"];
        string[] collection = ["Add", "Remove", "Item", "GetKey"];
        string[] list = ["AddItem", "RemoveItem", "Clear", "List"];
        var result = common.AsEnumerable();
        if (BaseClasses.IsVisual(baseClass)) result = result.Concat(visual);
        if (baseClass.Equals("Collection", StringComparison.OrdinalIgnoreCase)) result = result.Concat(collection);
        if (baseClass is "ListBox" or "ComboBox") result = result.Concat(list);
        if (baseClass is "Form") result = result.Concat(["Load", "Unload", "Activate", "Deactivate", "QueryUnload", "Resize"]);
        return result;
    }

    private static int EndOfClass(string text, int startLine)
    {
        var lines = SplitLines(text);
        for (int i = startLine; i < lines.Length; i++)
            if (Regex.IsMatch(lines[i], @"^\s*ENDD(E|EF|EFI|EFIN|EFINE)?\b", RegexOptions.IgnoreCase)) return i + 1;
        return lines.Length;
    }

    private static IEnumerable<string> DeclaredVariables(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(text))
        {
            var m = Regex.Match(line, @"^\s*(LOCAL|LOCA|PRIVATE|PRIV|PUBLIC|PUBL|LPARAMETERS|LPAR|PARAMETERS|PARA|DIMENSION|DIME)\s+(ARRAY\s+)?(.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
                foreach (var part in CodePart(m.Groups[3].Value).Split(','))
                {
                    var w = WordRx.Match(part);
                    if (w.Success && !w.Value.Equals("AS", StringComparison.OrdinalIgnoreCase)) set.Add(w.Value);
                }
            var a = Regex.Match(line, @"^\s*([A-Za-z_]\w*)\s*=(?!=)");
            if (a.Success) set.Add(a.Groups[1].Value);
        }
        return set;
    }

    // ================================================================================
    // Hover and definition
    // ================================================================================

    /// <summary>The word under the cursor, plus the qualified name if it follows a dot (alias.field, obj.member).</summary>
    public static (string Word, string? Qualifier, int StartColumn)? WordAt(string text, int line, int column)
    {
        var lines = SplitLines(text);
        if (line < 1 || line > lines.Length) return null;
        var s = lines[line - 1];
        foreach (Match m in WordRx.Matches(s))
        {
            if (column - 1 < m.Index || column - 1 > m.Index + m.Length) continue;
            string? qualifier = null;
            if (m.Index > 0 && s[m.Index - 1] == '.')
            {
                var q = Regex.Match(s[..(m.Index - 1)], @"([A-Za-z_][A-Za-z0-9_\.]*)$");
                if (q.Success) qualifier = q.Value;
            }
            return (m.Value, qualifier, m.Index + 1);
        }
        return null;
    }

    public string? Hover(string text, int line, int column)
    {
        var at = WordAt(text, line, column);
        if (at == null) return null;
        var (word, qualifier, _) = at.Value;
        if (qualifier != null && _live?.Session.FindAlias(qualifier) is { } wa && wa.FieldIndex(word) is var fi and >= 0)
            return $"(field) {FieldDetail(wa.Alias, wa.Table.Fields[fi])}\nCurrent value: {Formatter.ToDisplay(wa.Get(fi), _live.Options).Trim()}";
        var unit = TryParse(text);
        if (qualifier == null && unit != null)
        {
            if (unit.Procedures.TryGetValue(word, out var p)) return $"({(p.IsFunction ? "function" : "procedure")}) {Signature(p)}";
            if (unit.Classes.TryGetValue(word, out var c)) return $"(class) {c.Name} AS {c.Parent}{(c.ParentLib != null ? " OF " + c.ParentLib : "")}";
        }
        var lineText = SplitLines(text)[line - 1];
        var isCall = Regex.IsMatch(lineText[(at.Value.StartColumn - 1 + word.Length)..], @"^\s*\(");
        if (qualifier == null && isCall && FunctionDocs.Functions.TryGetValue(word, out var doc))
            return $"{doc.Signature} → {doc.Returns}\n{doc.Summary}";
        if (qualifier == null && Regex.IsMatch(lineText.TrimStart(), $@"^{Regex.Escape(word)}\b", RegexOptions.IgnoreCase))
        {
            var cmd = FunctionDocs.Commands.FirstOrDefault(c => c.Key.StartsWith(word, StringComparison.OrdinalIgnoreCase) && word.Length >= Math.Min(4, c.Key.Length));
            if (cmd.Value != null) return cmd.Value;
        }
        if (qualifier == null && FunctionDocs.Functions.TryGetValue(word, out var doc2) && isCall) return doc2.Signature;
        if (_live != null && qualifier == null)
        {
            var wa2 = _live.Session.Current;
            if (wa2.InUse && wa2.FieldIndex(word) is var fi2 and >= 0)
                return $"(field) {FieldDetail(wa2.Alias, wa2.Table.Fields[fi2])}\nCurrent value: {Formatter.ToDisplay(wa2.Get(fi2), _live.Options).Trim()}";
            if (_live.FindVariable(word) is { } v)
                return v.IsArray ? $"(array) {word}[{v.Array!.Rows}{(v.Array.TwoDimensional ? ", " + v.Array.Cols : "")}]" : $"(variable) {word} = {Formatter.ToDisplay(v.Value, _live.Options).Trim()}";
        }
        if (FunctionDocs.Functions.TryGetValue(word, out var doc3) && qualifier == null) return $"{doc3.Signature} → {doc3.Returns}\n{doc3.Summary}";
        return null;
    }

    /// <summary>Go to definition: procedures, functions, classes and methods in this file or the workspace.</summary>
    public SymbolLocation? Definition(string text, int line, int column, string? currentFile)
    {
        var at = WordAt(text, line, column);
        if (at == null) return null;
        var word = at.Value.Word;
        foreach (var s in Symbols(text).SelectMany(Flatten))
            if (s.Name.Equals(word, StringComparison.OrdinalIgnoreCase) || s.Name.EndsWith("." + word, StringComparison.OrdinalIgnoreCase))
                return new SymbolLocation(currentFile, s.Line);
        foreach (var file in _workspaceFiles())
        {
            if (currentFile != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileNameWithoutExtension(file).Equals(word, StringComparison.OrdinalIgnoreCase)) return new SymbolLocation(file, 1);
            foreach (var s in QuickSymbols(file))
                if (s.Name.Equals(word, StringComparison.OrdinalIgnoreCase)) return new SymbolLocation(file, s.Line);
        }
        return null;
    }

    private static IEnumerable<DocumentSymbol> Flatten(DocumentSymbol s) => s.Children.SelectMany(Flatten).Prepend(s);

    // ================================================================================
    // Symbols
    // ================================================================================

    /// <summary>Procedures, functions and classes (with their methods), from source text. Works on incomplete code.</summary>
    public IReadOnlyList<DocumentSymbol> Symbols(string text)
    {
        var lines = SplitLines(text);
        var result = new List<DocumentSymbol>();
        List<DocumentSymbol>? classMembers = null;
        (string Name, int Line, string Parent)? cls = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            var dc = Regex.Match(l, @"^\s*DEFINE\s+CLASS\s+(\w+)\s+AS\s+(\w+)", RegexOptions.IgnoreCase);
            if (dc.Success)
            {
                cls = (dc.Groups[1].Value, i + 1, dc.Groups[2].Value);
                classMembers = new List<DocumentSymbol>();
                continue;
            }
            if (cls != null && Regex.IsMatch(l, @"^\s*ENDD(E|EF|EFI|EFIN|EFINE)?\b", RegexOptions.IgnoreCase))
            {
                result.Add(new DocumentSymbol(cls.Value.Name, SymbolKind.Class, cls.Value.Line, i + 1, classMembers!, "AS " + cls.Value.Parent));
                cls = null;
                classMembers = null;
                continue;
            }
            var pm = Regex.Match(l, @"^\s*(?:(PROTECTED|HIDDEN)\s+)?(PROC(?:E|ED|EDU|EDUR|EDURE)?|FUNC(?:T|TI|TIO|TION)?)\s+([\w\.]+)(.*)$", RegexOptions.IgnoreCase);
            if (pm.Success)
            {
                var isFunc = pm.Groups[2].Value.StartsWith("F", StringComparison.OrdinalIgnoreCase);
                var end = FindEndOfProc(lines, i);
                var sym = new DocumentSymbol(pm.Groups[3].Value, classMembers != null ? SymbolKind.Method : isFunc ? SymbolKind.Function : SymbolKind.Procedure,
                    i + 1, end, [], pm.Groups[4].Value.Trim());
                if (classMembers != null) classMembers.Add(sym); else result.Add(sym);
            }
            else if (classMembers != null)
            {
                var prop = Regex.Match(l, @"^\s*([A-Za-z_]\w*)\s*=(?!=)");
                if (prop.Success) classMembers.Add(new DocumentSymbol(prop.Groups[1].Value, SymbolKind.Property, i + 1, i + 1, []));
            }
        }
        if (cls != null) result.Add(new DocumentSymbol(cls.Value.Name, SymbolKind.Class, cls.Value.Line, lines.Length, classMembers!, "AS " + cls.Value.Parent));
        return result;
    }

    private static int FindEndOfProc(string[] lines, int start)
    {
        for (int j = start + 1; j < lines.Length; j++)
        {
            if (Regex.IsMatch(lines[j], @"^\s*(ENDP|ENDPR|ENDPRO|ENDPROC|ENDF|ENDFU|ENDFUN|ENDFUNC)\b", RegexOptions.IgnoreCase)) return j + 1;
            if (Regex.IsMatch(lines[j], @"^\s*(?:(PROTECTED|HIDDEN)\s+)?(PROC|FUNC|DEFINE\s+CLASS|ENDD)", RegexOptions.IgnoreCase)) return j;
        }
        return lines.Length;
    }

    private readonly Dictionary<string, (DateTime Stamp, List<DocumentSymbol> Symbols)> _fileSymbols = new(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<DocumentSymbol> QuickSymbols(string file)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (!_fileSymbols.TryGetValue(file, out var cached) || cached.Stamp != stamp)
            {
                cached = (stamp, Symbols(File.ReadAllText(file)).SelectMany(Flatten).ToList());
                _fileSymbols[file] = cached;
            }
            return cached.Symbols;
        }
        catch (IOException) { return []; }
    }

    // ================================================================================
    // Helpers
    // ================================================================================

    private static ProgramUnit? TryParse(string text)
    {
        try { return Parser.ParseProgram(text); }
        catch (CompileException) { return null; }
    }

    private static string Signature(ProcedureDef p) => $"{p.Name}({string.Join(", ", p.Parameters)})";

    public static string[] SplitLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
