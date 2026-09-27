using JoePro.Core;
using JoePro.Data;
using JoePro.Documents;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>
/// Class libraries and form files: .jpclass/.jpform documents, and legacy .vcx/.scx files, which are converted
/// in memory when no converted copy exists (so legacy forms and libraries run as they are).
/// </summary>
public sealed partial class Interpreter
{
    private readonly List<ProgramUnit> _classLibraries = new();
    private readonly Dictionary<string, (DateTime Stamp, ProgramUnit Unit)> _legacyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Libraries loaded with SET CLASSLIB.</summary>
    public IReadOnlyList<ProgramUnit> ClassLibraries => _classLibraries;

    /// <summary>
    /// Finds a class library or form: a converted .jpclass/.jpform is preferred to the legacy .vcx/.scx of the same
    /// name. Relative names are looked up next to <paramref name="relativeTo"/> first, then along SET DEFAULT/PATH.
    /// </summary>
    public string? ResolveClassFile(string name, string modernExt, string legacyExt, string? relativeTo = null)
    {
        name = name.Trim().Replace('\\', Path.DirectorySeparatorChar);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var candidates = ext == legacyExt ? [Path.ChangeExtension(name, modernExt), name]
            : ext.Length > 0 ? [name]
            : new[] { name + modernExt, name + legacyExt, name + ".prg" };
        foreach (var c in candidates)
        {
            if (!Path.IsPathRooted(c) && relativeTo != null
                && DataSession.FindIgnoringCase(Path.Combine(Path.GetDirectoryName(relativeTo)!, c)) is { } near) return near;
            if (Path.IsPathRooted(c) && DataSession.FindIgnoringCase(c) is { } abs) return abs;
            if (ResolveProgramFile(c, Path.GetExtension(c)) is { } found && Path.GetExtension(found).Equals(Path.GetExtension(c), StringComparison.OrdinalIgnoreCase)) return found;
        }
        return null;
    }

    /// <summary>Loads a class library or form file; .vcx and .scx files are converted in memory.</summary>
    internal ProgramUnit LoadClassFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".vcx" or ".scx")) return LoadProgram(path);
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_legacyCache.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Unit;
        var result = LegacyFormConverter.Convert(path);
        var text = ClassFileWriter.Write(result.File);
        var unit = Parser.ParseProgram(text, Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), path, inc => ResolveInclude(inc, path));
        _legacyCache[path] = (stamp, unit);
        RegisterUnit(unit);
        var unsupported = result.Findings.Count(f => f.Status == FindingStatus.Unsupported);
        Notify($"{Path.GetFileName(path)} was converted in memory{(unsupported > 0 ? $" ({unsupported} unsupported item(s))" : "")}. IMPORT FOXPRO converts it permanently.");
        return unit;
    }

    /// <summary>NEWOBJECT(cClass, cModule): loads the named library, program or form.</summary>
    internal ProgramUnit LoadLibrary(string name) =>
        LoadClassFile(ResolveClassFile(name, ".jpclass", ".vcx", _frame.Unit?.File) ?? throw VfpException.FileNotFound(name));

    /// <summary>SET CLASSLIB TO lib [, lib …] [ADDITIVE] (no libraries: clears the list).</summary>
    internal void SetClassLib(List<Token> raw)
    {
        var words = raw.Select(t => t.Text.ToUpperInvariant()).ToList();
        var toAt = words.IndexOf("TO");
        bool additive = words.Contains("ADDITIVE");
        if (!additive) _classLibraries.Clear();
        if (toAt < 0) return;
        var names = new List<string>();
        var current = new List<Token>();
        foreach (var t in raw.Skip(toAt + 1))
        {
            var w = t.Text.ToUpperInvariant();
            if (t.Kind == TokenKind.Ident && w is "ADDITIVE" or "IN" or "ALIAS") break;
            if (t.IsOp(",")) { if (current.Count > 0) names.Add(Join(current)); current.Clear(); continue; }
            current.Add(t);
        }
        if (current.Count > 0) names.Add(Join(current));
        foreach (var n in names)
        {
            var unit = LoadLibrary(n);
            if (!_classLibraries.Contains(unit)) _classLibraries.Add(unit);
        }

        static string Join(List<Token> tokens) => tokens.Count == 1 && tokens[0].Kind == TokenKind.String ? tokens[0].Text : string.Concat(tokens.Select(t => t.Text));
    }

    /// <summary>
    /// A class library changed on disk (CREATE/ADD/RENAME/REMOVE CLASS or a designer save): drops the cached copy and
    /// reloads it where SET CLASSLIB uses it, so the next CREATEOBJECT sees the change.
    /// </summary>
    public void InvalidateClassLibrary(string path)
    {
        var full = Path.GetFullPath(path);
        _programCache.Remove(full);
        _legacyCache.Remove(full);
        _classes.Clear();
        for (int i = 0; i < _classLibraries.Count; i++)
        {
            if (_classLibraries[i].File is not { } f || !Path.GetFullPath(f).Equals(full, StringComparison.OrdinalIgnoreCase)) continue;
            _loadedUnits.Remove(_classLibraries[i]);
            if (File.Exists(full)) _classLibraries[i] = LoadClassFile(full);
            else _classLibraries.RemoveAt(i--);
        }
    }

    /// <summary>The library that defines a class among the SET CLASSLIB libraries, or null.</summary>
    internal string? ClassLibraryOf(string className) =>
        _classLibraries.FirstOrDefault(u => u.Classes.ContainsKey(className))?.File;

    // ---- Data environment -----------------------------------------------------------------------------

    private static bool IsDataEnvironment(VfpObject o) => o.Class.BaseClass.Equals("DataEnvironment", StringComparison.OrdinalIgnoreCase);

    internal static VfpObject? DataEnvironmentOf(VfpObject form) => form.Members.FirstOrDefault(IsDataEnvironment);

    private static string Prop(VfpObject o, string name) =>
        o.FindProperty(name)?.Value is { Kind: ValueKind.Character } v ? v.AsString.Trim() : "";

    /// <summary>DataEnvironment.OpenTables(): opens each Cursor (with its order, filter and buffering) and sets up Relations.</summary>
    internal void OpenDataEnvironmentTables(VfpObject de)
    {
        var formFile = OwningForm(de)?.Class.Unit?.File ?? de.Class.Unit?.File;
        foreach (var cursor in de.Members.Where(m => m.Class.BaseClass.Equals("Cursor", StringComparison.OrdinalIgnoreCase)))
        {
            var source = Prop(cursor, "CursorSource");
            if (source.Length == 0) continue;
            var alias = Prop(cursor, "Alias") is { Length: > 0 } a ? a : Path.GetFileNameWithoutExtension(source);
            var database = Prop(cursor, "Database");
            string target = source;
            if (database.Length > 0)
            {
                var dbPath = ResolveNear(database, formFile, Store.DatabaseExtension);
                var dbName = Path.GetFileNameWithoutExtension(dbPath);
                if (!Session.OpenDatabases.Any(d => d.Name.Equals(dbName, StringComparison.OrdinalIgnoreCase))) Session.OpenDatabase(dbPath);
                target = dbName + "!" + Path.GetFileNameWithoutExtension(source);
            }
            else if (source.Contains('.') || source.Contains('/') || source.Contains('\\'))
                target = ResolveNear(source, formFile, Store.FreeTableExtension);
            if (Session.FindAlias(alias) is { } open) open.Close();
            var noData = cursor.FindProperty("NoDataOnLoad")?.Value is { Kind: ValueKind.Logical } nd && nd.AsBool;
            var readOnly = cursor.FindProperty("ReadOnly")?.Value is { Kind: ValueKind.Logical } ro && ro.AsBool;
            ExecUse(new UseStmt(new LiteralExpr(Value.String(target)), new LiteralExpr(Value.Number(0)), new LiteralExpr(Value.String(alias)),
                Again: true, Exclusive: null, Order: null, NoUpdate: readOnly, NoData: noData));
            var wa = Session.FindAlias(alias)!;
            if (Prop(cursor, "Order") is { Length: > 0 } order) { wa.SetOrder(order); wa.GoTop(); }
            if (Prop(cursor, "Filter") is { Length: > 0 } filter) { wa.SetFilter(Compile(filter)); wa.GoTop(); }
            var bmo = cursor.FindProperty("BufferModeOverride")?.Value is { Kind: ValueKind.Number } b ? (int)b.AsNumber : 1;
            if (bmo == 1 && OwningForm(de)?.FindProperty("BufferMode")?.Value is { Kind: ValueKind.Number } fb) bmo = (int)fb.AsNumber switch { 1 => 2, 2 => 3, _ => 1 };
            if (bmo is >= 2 and <= 5 && wa.BufferMode != bmo) wa.SetBuffering(bmo);
        }
        foreach (var rel in de.Members.Where(m => m.Class.BaseClass.Equals("Relation", StringComparison.OrdinalIgnoreCase)))
        {
            var parent = Prop(rel, "ParentAlias");
            var child = Prop(rel, "ChildAlias");
            var expr = Prop(rel, "RelationalExpr");
            if (parent.Length == 0 || child.Length == 0 || expr.Length == 0) continue;
            var order = Prop(rel, "ChildOrder");
            ExecuteCommand((order.Length > 0 ? $"SET ORDER TO {order} IN {child}\n" : "") + $"SET RELATION TO {expr} INTO {child} IN {parent} ADDITIVE");
        }
        if (Prop(de, "InitialSelectedAlias") is { Length: > 0 } initial && Session.FindAlias(initial) != null) Session.Select(initial);
    }

    /// <summary>
    /// A report's data environment (DEFINE CLASS … AS DataEnvironment code): creates it, raises BeforeOpenTables and
    /// opens its tables unless AutoOpenTables is .F. Relative table names are found next to the report file.
    /// </summary>
    public VfpObject OpenReportDataEnvironment(string classCode, string reportFile)
    {
        var unit = Parser.ParseProgram(classCode, "DATAENVIRONMENT", reportFile, inc => ResolveInclude(inc, reportFile));
        var def = unit.Classes.Values.FirstOrDefault() ?? throw new VfpException(1733, "The report's data environment has no class definition.");
        var de = CreateObject(ResolveClass(def.Name, unit), []) ?? throw new VfpException(1733, "The report's data environment could not be created.");
        RaiseEvent(de, "BeforeOpenTables", []);
        if (de.FindProperty("AutoOpenTables")?.Value is not { Kind: ValueKind.Logical } auto || auto.AsBool)
            InvokeMethod(de, "OpenTables", []);
        return de;
    }

    /// <summary>After a report: closes the data environment's tables (unless AutoCloseTables is .F.) and releases it.</summary>
    public void CloseReportDataEnvironment(VfpObject de)
    {
        if (de.FindProperty("AutoCloseTables")?.Value is not { Kind: ValueKind.Logical } auto || auto.AsBool)
            InvokeMethod(de, "CloseTables", []);
        RaiseEvent(de, "AfterCloseTables", []);
        ReleaseObject(de);
    }

    /// <summary>A new private data session (reports with PrivateDataSession); dispose it with <see cref="EndPrivateSession"/>.</summary>
    public DataSession BeginPrivateSession()
    {
        var session = new DataSession(Options.Clone(), this);
        Sessions.Add(session);
        return session;
    }

    public void EndPrivateSession(DataSession session)
    {
        Sessions.Remove(session);
        session.Dispose();
    }

    /// <summary>DataEnvironment.CloseTables(): closes the tables its cursors opened.</summary>
    internal void CloseDataEnvironmentTables(VfpObject de)
    {
        foreach (var cursor in de.Members.Where(m => m.Class.BaseClass.Equals("Cursor", StringComparison.OrdinalIgnoreCase)))
        {
            var source = Prop(cursor, "CursorSource");
            var alias = Prop(cursor, "Alias") is { Length: > 0 } a ? a : Path.GetFileNameWithoutExtension(source);
            Session.FindAlias(alias)?.Close();
        }
    }

    private string ResolveNear(string path, string? formFile, string defaultExt)
    {
        path = path.Replace('\\', Path.DirectorySeparatorChar);
        if (!Path.HasExtension(path)) path += defaultExt;
        if (!Path.IsPathRooted(path) && formFile != null && DataSession.FindIgnoringCase(Path.Combine(Path.GetDirectoryName(formFile)!, path)) is { } near) return near;
        return Session.ResolvePath(path, defaultExt);
    }
}
