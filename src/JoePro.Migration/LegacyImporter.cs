using System.Text;
using JoePro.Core;
using JoePro.Documents;
using JoePro.Data;
using JoePro.Legacy.Formats;
using JoePro.Runtime;

namespace JoePro.Migration;

/// <summary>
/// Imports legacy FoxPro data (free .DBF tables and .DBC databases, with memo files and index tags)
/// into Joe Pro tables, recording every outcome in a <see cref="MigrationReport"/>.
/// </summary>
public sealed class LegacyImporter
{
    private readonly MigrationReport _report;
    private readonly Interpreter _host;

    public LegacyImporter(MigrationReport report, Interpreter? host = null)
    {
        _report = report;
        _host = host ?? new Interpreter(new TextWriterOutput(TextWriter.Null));
    }

    /// <summary>Optional encoding to use for tables whose code page byte is unmarked.</summary>
    public Encoding? DefaultEncoding { get; set; }

    // ================================================================================
    // Folders
    // ================================================================================

    /// <summary>Imports every database and free table in a folder and analyzes its program files.</summary>
    public void ImportFolder(string folder, string targetDir)
    {
        _report.Source = Path.GetFullPath(folder);
        _report.Target = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(targetDir);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        var dbcTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The target mirrors the source folder layout, so relative references (class libraries, databases,
        // pictures) keep working after conversion.
        foreach (var dbc in files.Where(f => Ext(f) == ".dbc"))
        {
            try { dbcTables.UnionWith(ImportDatabase(dbc, MirrorDir(dbc, targetDir))); }
            catch (Exception ex) { Fail("DATA.DBC.READ", "database", dbc, ex); }
        }
        foreach (var dbf in files.Where(f => Ext(f) == ".dbf" && !dbcTables.Contains(Path.GetFullPath(f))))
        {
            try
            {
                using (var probe = DbfTable.Open(dbf))
                {
                    if (probe.DbcBacklink != null)
                    {
                        _report.Add(FindingStatus.NeedsReview, "DATA.DBC.ORPHAN", "data", new SourceLocation(Rel(dbf)),
                            $"Table belongs to database '{probe.DbcBacklink}', which was not found; imported as a free table.",
                            "Include the .DBC (with .DCT/.DCX) in the migration folder to keep long names, rules and relations.");
                    }
                }
                ImportTable(dbf, MirrorDir(dbf, targetDir));
            }
            catch (Exception ex) { Fail("DATA.TABLE.READ", "data", dbf, ex); }
        }
        var analyzer = new ProgramAnalyzer(_report);
        analyzer.IncludeRoots.Add(Path.GetFullPath(folder));
        foreach (var prg in files.Where(f => Ext(f) is ".prg" or ".h" or ".mpr" or ".qpr"))
            analyzer.AnalyzeFile(prg, Rel(prg));
        // Programs, include files and pictures are copied as they are (compatibility mode runs them unchanged).
        foreach (var f in files.Where(f => Ext(f) is ".prg" or ".h" or ".mpr" or ".qpr" or ".bmp" or ".gif" or ".jpg" or ".jpeg" or ".png" or ".ico" or ".cur" or ".msk" or ".txt" or ".xml"))
        {
            var dest = Path.Combine(MirrorDir(f, targetDir), Path.GetFileName(f));
            if (!File.Exists(dest)) File.Copy(f, dest, overwrite: false);
        }
        foreach (var form in files.Where(f => Ext(f) is ".scx" or ".vcx"))
        {
            try { ConvertClassFile(form, targetDir); }
            catch (Exception ex) { Fail(Ext(form) == ".scx" ? "FORM.READ" : "CLASSLIB.READ", Ext(form) == ".scx" ? "form" : "classlib", form, ex); CopyOriginal(form, targetDir); }
        }
        foreach (var rpt in files.Where(f => Ext(f) is ".frx" or ".lbx"))
        {
            try { ConvertReportFile(rpt, targetDir); }
            catch (Exception ex) { Fail(Ext(rpt) == ".frx" ? "REPORT.READ" : "LABEL.READ", Ext(rpt) == ".frx" ? "report" : "label", rpt, ex); CopyOriginal(rpt, targetDir); }
        }
        foreach (var mnx in files.Where(f => Ext(f) == ".mnx"))
        {
            try { ConvertMenuFile(mnx, targetDir); }
            catch (Exception ex) { Fail("MENU.READ", "menu", mnx, ex); CopyOriginal(mnx, targetDir); }
        }
        foreach (var other in files.Where(f => Ext(f) is ".pjx"))
        {
            var (kind, phase) = ("Project", "Phase 5 (Project Manager)");
            _report.Add(FindingStatus.Unsupported, "ARTIFACT.NOT_YET_CONVERTED", Ext(other).TrimStart('.'), new SourceLocation(Rel(other)),
                $"{kind} conversion ships with {phase}. The original file is preserved alongside the output.",
                "Re-run the migration after upgrading; the report will show what changed.");
            CopyOriginal(other, targetDir);
        }
    }

    private string MirrorDir(string file, string targetDir)
    {
        var sub = Path.GetDirectoryName(Rel(file)) ?? "";
        var dir = sub.Length == 0 || sub.StartsWith("..") ? targetDir : Path.Combine(targetDir, sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Converts a form (.SCX) to .jpform or a class library (.VCX) to .jpclass, reports every change the
    /// converter made, and analyzes the method code like program files.
    /// </summary>
    public string ConvertClassFile(string path, string targetDir)
    {
        if (string.IsNullOrEmpty(_report.Source)) _report.Source = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (string.IsNullOrEmpty(_report.Target)) _report.Target = Path.GetFullPath(targetDir);
        var isForm = Ext(path) == ".scx";
        var category = isForm ? "form" : "classlib";
        var prefix = isForm ? "FORM" : "CLASSLIB";
        var result = LegacyFormConverter.Convert(path);
        var dest = Path.Combine(MirrorDir(path, targetDir), Path.GetFileNameWithoutExtension(path).ToLowerInvariant() + (isForm ? ".jpform" : ".jpclass"));
        ClassFileWriter.Save(result.File, dest);
        var src = new SourceLocation(Rel(path));
        var target = new TargetLocation(dest);
        foreach (var f in result.Findings)
        {
            var (status, rule) = f.Status switch
            {
                JoePro.Documents.FindingStatus.Changed => (FindingStatus.ConvertedWithChanges, prefix + ".VALUE.CHANGED"),
                JoePro.Documents.FindingStatus.NeedsReview => (FindingStatus.NeedsReview, prefix + ".REVIEW"),
                JoePro.Documents.FindingStatus.Unsupported => (FindingStatus.Unsupported, prefix + ".UNSUPPORTED"),
                _ => (FindingStatus.Converted, prefix + ".INFO"),
            };
            _report.Add(status, rule, category, src with { Object = f.Object }, f.Message, target: target);
        }
        var members = result.File.Classes.Sum(c => c.Members.Count);
        var methods = result.File.Classes.Sum(c => c.Methods.Count);
        _report.Add(result.Findings.Any(f => f.Status == JoePro.Documents.FindingStatus.Changed) ? FindingStatus.ConvertedWithChanges : FindingStatus.Converted,
            prefix + ".CONVERTED", category, src,
            $"{(isForm ? "Form" : "Class library")} converted: {result.File.Classes.Count} class(es), {members} object(s), {methods} method(s).", target: target);
        var analyzer = new ProgramAnalyzer(_report);
        analyzer.IncludeRoots.Add(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (Directory.Exists(_report.Source)) analyzer.IncludeRoots.Add(_report.Source);
        foreach (var cls in result.File.Classes)
            foreach (var m in cls.Methods.Where(m => m.Body.Count > 0))
                analyzer.AnalyzeSource(m.Code, Rel(path), cls.Name + "." + m.Name);
        return dest;
    }

    /// <summary>Converts a report (.FRX) to .jpreport or a label (.LBX) to .jplabel and reports what changed.</summary>
    public string ConvertReportFile(string path, string targetDir)
    {
        if (string.IsNullOrEmpty(_report.Source)) _report.Source = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (string.IsNullOrEmpty(_report.Target)) _report.Target = Path.GetFullPath(targetDir);
        var isLabel = Ext(path) == ".lbx";
        var category = isLabel ? "label" : "report";
        var prefix = isLabel ? "LABEL" : "REPORT";
        var result = JoePro.Documents.Reports.LegacyReportConverter.Convert(path);
        var dest = Path.Combine(MirrorDir(path, targetDir), Path.GetFileNameWithoutExtension(path).ToLowerInvariant() + (isLabel ? ".jplabel" : ".jpreport"));
        result.Document.Save(dest);
        var src = new SourceLocation(Rel(path));
        var target = new TargetLocation(dest);
        foreach (var f in result.Findings)
        {
            var (status, rule) = f.Status switch
            {
                JoePro.Documents.FindingStatus.Changed => (FindingStatus.ConvertedWithChanges, prefix + ".VALUE.CHANGED"),
                JoePro.Documents.FindingStatus.NeedsReview => (FindingStatus.NeedsReview, prefix + ".REVIEW"),
                JoePro.Documents.FindingStatus.Unsupported => (FindingStatus.Unsupported, prefix + ".UNSUPPORTED"),
                _ => (FindingStatus.Converted, prefix + ".INFO"),
            };
            _report.Add(status, rule, category, src with { Object = f.Object }, f.Message, target: target);
        }
        var doc = result.Document;
        var objects = doc.Bands.Sum(b => b.Objects.Count);
        _report.Add(result.Findings.Any(f => f.Status == JoePro.Documents.FindingStatus.Changed) ? FindingStatus.ConvertedWithChanges : FindingStatus.Converted,
            prefix + ".CONVERTED", category, src,
            $"{(isLabel ? "Label" : "Report")} converted: {doc.Bands.Count} band(s), {objects} object(s), {doc.Groups.Count} group(s), {doc.Variables.Count} variable(s); {doc.Paper}{(doc.Landscape ? " landscape" : "")}.",
            target: target);
        if (doc.DataEnvironment != null)
        {
            var analyzer = new ProgramAnalyzer(_report);
            if (Directory.Exists(_report.Source)) analyzer.IncludeRoots.Add(_report.Source);
            analyzer.AnalyzeSource(doc.DataEnvironment, Rel(path), "DataEnvironment");
        }
        return dest;
    }

    /// <summary>Converts a menu (.MNX) to .jpmenu; DO main.mpr then runs it even without the generated .mpr.</summary>
    public string ConvertMenuFile(string path, string targetDir)
    {
        if (string.IsNullOrEmpty(_report.Source)) _report.Source = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (string.IsNullOrEmpty(_report.Target)) _report.Target = Path.GetFullPath(targetDir);
        var result = JoePro.Documents.Menus.LegacyMenuConverter.Convert(path);
        var dest = Path.Combine(MirrorDir(path, targetDir), Path.GetFileNameWithoutExtension(path).ToLowerInvariant() + ".jpmenu");
        result.Document.Save(dest);
        var src = new SourceLocation(Rel(path));
        var target = new TargetLocation(dest);
        foreach (var f in result.Findings)
            _report.Add(f.Status == JoePro.Documents.FindingStatus.NeedsReview ? FindingStatus.NeedsReview : FindingStatus.Converted,
                f.Status == JoePro.Documents.FindingStatus.NeedsReview ? "MENU.REVIEW" : "MENU.INFO", "menu", src with { Object = f.Object }, f.Message, target: target);
        var doc = result.Document;
        _report.Add(FindingStatus.Converted, "MENU.CONVERTED", "menu", src,
            $"Menu converted: {doc.Items.Count} pad(s), {doc.AllItems().Count()} item(s).", target: target);
        var analyzer = new ProgramAnalyzer(_report);
        if (Directory.Exists(_report.Source)) analyzer.IncludeRoots.Add(_report.Source);
        foreach (var code in new[] { doc.Setup, doc.Cleanup }.Concat(doc.AllItems().Select(i => i.Procedure ?? i.Command)).OfType<string>())
            analyzer.AnalyzeSource(code, Rel(path), "menu");
        return dest;
    }

    private static string Ext(string f) => Path.GetExtension(f).ToLowerInvariant();

    private string Rel(string path)
    {
        if (string.IsNullOrEmpty(_report.Source) || !Directory.Exists(_report.Source)) return Path.GetFileName(path);
        return Path.GetRelativePath(_report.Source, path);
    }

    private void CopyOriginal(string file, string targetDir)
    {
        var dir = Path.Combine(targetDir, "legacy-originals");
        Directory.CreateDirectory(dir);
        foreach (var companion in Directory.EnumerateFiles(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".*"))
            File.Copy(companion, Path.Combine(dir, Path.GetFileName(companion)), overwrite: true);
    }

    private void Fail(string rule, string category, string file, Exception ex) =>
        _report.Add(FindingStatus.Failed, rule, category, new SourceLocation(Rel(file)), ex.Message,
            "The original file is untouched. Check that the file is a valid FoxPro file and not open exclusively elsewhere.");

    // ================================================================================
    // Free tables
    // ================================================================================

    /// <summary>Imports one .DBF as a Joe Pro free table (.jpt) in <paramref name="targetDir"/>.</summary>
    public string ImportTable(string dbfPath, string targetDir)
    {
        if (string.IsNullOrEmpty(_report.Source)) _report.Source = Path.GetFullPath(dbfPath);
        if (string.IsNullOrEmpty(_report.Target)) _report.Target = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(dbfPath).ToLowerInvariant() + Store.FreeTableExtension);
        using var session = new DataSession(new SetOptions { Default_ = targetDir, Safety = false }, _host);
        if (File.Exists(target)) File.Delete(target);
        using var dbf = DbfTable.Open(dbfPath, DefaultEncoding);
        var (fields, names) = MapFields(dbf, null, dbfPath);
        var schema = new TableSchema(Path.GetFileNameWithoutExtension(dbfPath), fields);
        var table = session.CreateTable(schema, free: true, target);
        CopyRows(dbf, table, dbfPath, target, names);
        return target;
    }

    private (List<FieldDef> Fields, List<string> Names) MapFields(DbfTable dbf, List<string>? longNames, string dbfPath)
    {
        var src = new SourceLocation(Rel(dbfPath));
        if (!dbf.CodePageFromFile)
        {
            _report.Add(FindingStatus.ConvertedWithChanges, "DATA.CODEPAGE.UNMARKED", "data", src,
                $"The table has no code page mark; text was decoded as {dbf.Encoding.WebName}.",
                "Check accented characters; re-run with --codepage if another code page was used.");
        }
        if (!dbf.IsVisualFoxPro)
        {
            _report.Add(FindingStatus.ConvertedWithChanges, "DATA.FORMAT.LEGACY", "data", src,
                $"Table format is {dbf.FormatName}; converted to Joe Pro types.");
        }
        var fields = new List<FieldDef>();
        var names = new List<string>();
        for (int i = 0; i < dbf.Fields.Count; i++)
        {
            var f = dbf.Fields[i];
            var def = f.ToFieldDef();
            if (longNames != null && i < longNames.Count && !string.IsNullOrWhiteSpace(longNames[i]))
                def = def with { Name = longNames[i] };
            if (f.Type == 'G')
                _report.Add(FindingStatus.ConvertedWithChanges, "DATA.FIELD.GENERAL", "data", src with { Object = def.Name },
                    "General (OLE) field copied as binary data; OLE objects are not rendered by Joe Pro.",
                    "Replace OLE-embedded documents with files or Blob fields.");
            if (f.Type is 'P' or '@' or '+' or 'O')
                _report.Add(FindingStatus.ConvertedWithChanges, "DATA.FIELD.DBASE_TYPE", "data", src with { Object = def.Name },
                    $"dBASE field type '{f.Type}' mapped to FoxPro type '{def.Type}'.");
            fields.Add(def);
            names.Add(def.Name);
        }
        return (fields, names);
    }

    private void CopyRows(DbfTable dbf, Table table, string dbfPath, string target, List<string> names)
    {
        int read = 0, imported = 0, deleted = 0;
        var src = new SourceLocation(Rel(dbfPath));
        table.Store.BeginTransaction();
        try
        {
            foreach (var rec in dbf.Records())
            {
                read++;
                try
                {
                    var vals = rec.Values.Select((v, i) => v.IsNull && !table.Fields[i].Nullable ? table.Fields[i].BlankValue() : v).ToArray();
                    table.Append(vals, rec.Deleted);
                    imported++;
                    if (rec.Deleted) deleted++;
                }
                catch (VfpException ex)
                {
                    _report.Add(FindingStatus.Failed, "DATA.ROW.IMPORT", "data", src with { Object = $"record {rec.RecNo}" }, ex.Message,
                        "Fix the value in the legacy table or import with a wider field definition.");
                }
            }
            table.Store.Commit();
        }
        catch
        {
            table.Store.Rollback();
            throw;
        }

        // Restore autoincrement counters from the legacy header.
        for (int i = 0; i < dbf.Fields.Count; i++)
            if (dbf.Fields[i].IsAutoInc) table.SetAutoIncrementNext(names[i], dbf.Fields[i].AutoIncNext);

        var verified = imported == read && Verify(dbf, table);
        var (found, rebuilt) = RebuildIndexes(dbf, dbfPath, table);
        _report.Tables.Add(new TableSummary(Rel(dbfPath), target, read, imported, verified, dbf.Encoding.WebName, found, rebuilt, deleted));
        _report.Add(verified ? FindingStatus.Converted : FindingStatus.NeedsReview, verified ? "DATA.TABLE.IMPORTED" : "DATA.TABLE.VERIFY",
            "data", src, verified
                ? $"{imported} rows imported and verified ({deleted} deleted rows kept with their deleted flag)."
                : $"{imported} of {read} rows imported; verification found differences.",
            verified ? null : "Compare the source and target tables; see the row-level findings.",
            new TargetLocation(target, table.Name));
    }

    private static bool Verify(DbfTable dbf, Table table)
    {
        using var rows = table.Scan(null, forward: true).GetEnumerator();
        foreach (var rec in dbf.Records())
        {
            if (!rows.MoveNext()) return false;
            var row = rows.Current;
            if (row.Deleted != rec.Deleted) return false;
            for (int i = 0; i < rec.Values.Length; i++)
            {
                var a = rec.Values[i];
                var b = row.Values[i];
                if (a.IsNull && !table.Fields[i].Nullable) a = table.Fields[i].BlankValue();
                if (!Same(table.Fields[i].Coerce(a), b)) return false;
            }
        }
        return !rows.MoveNext();
    }

    private static bool Same(Value a, Value b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            ValueKind.Number => a.AsNumber.Equals(b.AsNumber),
            ValueKind.Binary => a.AsBinary.AsSpan().SequenceEqual(b.AsBinary),
            _ => a.Equals(b),
        };
    }

    private (int Found, int Rebuilt) RebuildIndexes(DbfTable dbf, string dbfPath, Table table)
    {
        var cdxPath = Path.ChangeExtension(dbfPath, ".cdx");
        cdxPath = DataSession.FindIgnoringCase(cdxPath) ?? cdxPath;
        if (!File.Exists(cdxPath))
        {
            if (dbf.HasStructuralCdx)
                _report.Add(FindingStatus.NeedsReview, "DATA.INDEX.MISSING", "index", new SourceLocation(Rel(dbfPath)),
                    "The table header says it has a structural index (.CDX) but the file was not found.",
                    "Copy the .CDX next to the .DBF and re-run, or recreate the indexes with INDEX ON.");
            return (0, 0);
        }
        List<IndexTagInfo> tags;
        try
        {
            using var cdx = CdxFile.Open(cdxPath, dbf.Encoding);
            tags = cdx.Tags.ToList();
        }
        catch (Exception ex)
        {
            _report.Add(FindingStatus.NeedsReview, "DATA.INDEX.UNREADABLE", "index", new SourceLocation(Rel(cdxPath)), ex.Message,
                "Recreate the indexes with INDEX ON; the data itself was imported.");
            return (0, 0);
        }
        int rebuilt = 0;
        foreach (var t in tags)
        {
            var src = new SourceLocation(Rel(cdxPath), t.Name, Snippet: t.KeyExpression + (t.ForExpression != null ? " FOR " + t.ForExpression : ""));
            var kind = t.Candidate ? TagKind.Candidate : t.Unique ? TagKind.Unique : TagKind.Regular;
            try
            {
                table.CreateTag(new TagDef(t.Name, t.KeyExpression, t.ForExpression, t.Descending, kind));
                rebuilt++;
                _report.Add(FindingStatus.Converted, "DATA.INDEX.REBUILT", "index", src,
                    $"Index tag rebuilt from its expression ({kind}{(t.Descending ? ", descending" : "")}).");
            }
            catch (Exception ex)
            {
                _report.Add(FindingStatus.NeedsReview, "DATA.INDEX.REBUILD", "index", src,
                    $"Index tag could not be rebuilt: {ex.Message}",
                    "If the key calls a user-defined function, load its program (SET PROCEDURE) and run INDEX ON again.");
            }
        }
        if (tags.Count > 0)
            _report.Add(FindingStatus.ConvertedWithChanges, "DATA.INDEX.COLLATION", "index", new SourceLocation(Rel(cdxPath)),
                "Index keys were rebuilt with MACHINE collation; the legacy collation sequence was not read (judgment call J1).",
                "If the application relies on GENERAL (or another) collation ordering, review sort-sensitive screens and reports.");
        return (tags.Count, rebuilt);
    }

    // ================================================================================
    // Databases (.DBC)
    // ================================================================================

    private sealed record DbcObject(int Id, int ParentId, string Type, string Name, byte[] Property, string Code);

    /// <summary>
    /// Imports a .DBC database into a Joe Pro database (.jpdb): tables with long field names,
    /// index tags and stored procedures. Returns the full paths of the legacy tables it imported.
    /// </summary>
    public IReadOnlyCollection<string> ImportDatabase(string dbcPath, string targetDir)
    {
        if (string.IsNullOrEmpty(_report.Source)) _report.Source = Path.GetFullPath(dbcPath);
        if (string.IsNullOrEmpty(_report.Target)) _report.Target = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(targetDir);
        var objects = ReadDbc(dbcPath);
        var dbName = Path.GetFileNameWithoutExtension(dbcPath).ToLowerInvariant();
        var target = Path.Combine(targetDir, dbName + Store.DatabaseExtension);
        if (File.Exists(target)) File.Delete(target);
        using var session = new DataSession(new SetOptions { Default_ = targetDir, Safety = false }, _host);
        var store = session.CreateDatabase(target);
        var imported = new List<string>();
        var dbcSrc = new SourceLocation(Rel(dbcPath));

        // Stored procedures.
        var procs = objects.FirstOrDefault(o => o.Type == "Database" && o.Name.Equals("StoredProceduresSource", StringComparison.OrdinalIgnoreCase))?.Code
                    ?? objects.Where(o => o.Type == "Database").Select(o => o.Code).FirstOrDefault(c => c.Contains("PROCEDURE", StringComparison.OrdinalIgnoreCase) || c.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(procs))
        {
            store.StoredProcedures = procs;
            _report.Add(FindingStatus.Converted, "DATA.DBC.PROCEDURES", "database", dbcSrc with { Object = "Stored procedures" },
                $"Stored procedures copied ({procs.Split('\n').Length} lines).", target: new TargetLocation(target));
            new ProgramAnalyzer(_report).AnalyzeSource(procs, Rel(dbcPath), "Stored procedures");
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(dbcPath))!;
        var candidates = Directory.EnumerateFiles(folder, "*.*").Where(f => Ext(f) == ".dbf").ToList();
        foreach (var t in objects.Where(o => o.Type == "Table"))
        {
            var fieldNames = objects.Where(o => o.Type == "Field" && o.ParentId == t.Id).OrderBy(o => o.Id).Select(o => o.Name).ToList();
            var dbfPath = LocateTableFile(t, candidates, dbcPath, fieldNames);
            var tsrc = dbcSrc with { Object = t.Name };
            if (dbfPath == null)
            {
                _report.Add(FindingStatus.Failed, "DATA.DBC.TABLE_MISSING", "database", tsrc,
                    $"Table '{t.Name}' is defined in the database but its .DBF file was not found.",
                    "Copy the table files next to the .DBC and re-run.");
                continue;
            }
            try
            {
                using var dbf = DbfTable.Open(dbfPath, DefaultEncoding);
                var (fields, names) = MapFields(dbf, fieldNames.Count == dbf.Fields.Count ? fieldNames : null, dbfPath);
                var schema = new TableSchema(t.Name, fields);
                var table = store.CreateTable(schema, _host);
                CopyRows(dbf, table, dbfPath, target, names);
                imported.Add(Path.GetFullPath(dbfPath));
                if (t.Property.Length > 0)
                    _report.Add(FindingStatus.NeedsReview, "DATA.DBC.PROPERTIES", "database", tsrc,
                        "Table and field properties (captions, validation rules, defaults, triggers, primary key) are stored in the DBC's undocumented binary property format and were not decoded yet (judgment call J2).",
                        "Re-create rules, defaults and triggers in the Database Designer, or wait for the DBC property decoder.",
                        new TargetLocation(target, t.Name));
            }
            catch (Exception ex) { Fail("DATA.TABLE.READ", "data", dbfPath, ex); }
        }

        foreach (var r in objects.Where(o => o.Type == "Relation"))
            _report.Add(FindingStatus.NeedsReview, "DATA.DBC.RELATION", "database", dbcSrc with { Object = r.Name },
                "Persistent relation (and its referential integrity rules) was not converted; its definition lives in the DBC property format.",
                "Recreate the relation in the Database Designer (Phase 5).");
        foreach (var v in objects.Where(o => o.Type == "View"))
            _report.Add(FindingStatus.Unsupported, "DATA.DBC.VIEW", "database", dbcSrc with { Object = v.Name, Snippet = ExtractSql(v.Property) },
                "Views are converted by the View Designer (Phase 5).", "The view SQL is shown here so it can be recreated with SELECT … INTO CURSOR in the meantime.");
        foreach (var c in objects.Where(o => o.Type == "Connection"))
            _report.Add(FindingStatus.Unsupported, "DATA.DBC.CONNECTION", "database", dbcSrc with { Object = c.Name },
                "Remote connections are supported with SQL pass-through in Phase 2.");
        return imported;
    }

    private static List<DbcObject> ReadDbc(string path)
    {
        using var dbc = DbfTable.Open(path);
        int Idx(string name) => dbc.Fields.ToList().FindIndex(f => f.Name == name);
        int iId = Idx("OBJECTID"), iParent = Idx("PARENTID"), iType = Idx("OBJECTTYPE"), iName = Idx("OBJECTNAME"), iProp = Idx("PROPERTY"), iCode = Idx("CODE");
        if (iId < 0 || iType < 0 || iName < 0) throw new InvalidDataException($"'{path}' is not a FoxPro database container.");
        var list = new List<DbcObject>();
        foreach (var r in dbc.Records())
        {
            if (r.Deleted) continue;
            var prop = iProp >= 0 ? r.Values[iProp] : Value.EmptyString;
            var propBytes = prop.Kind == ValueKind.Binary ? prop.AsBinary : prop.Kind == ValueKind.Character ? Encoding.Latin1.GetBytes(prop.AsString) : [];
            var code = iCode >= 0 && r.Values[iCode].Kind == ValueKind.Character ? r.Values[iCode].AsString : "";
            list.Add(new DbcObject((int)r.Values[iId].AsNumber, iParent >= 0 ? (int)r.Values[iParent].AsNumber : 0,
                r.Values[iType].AsString.Trim(), r.Values[iName].AsString.Trim(), propBytes, code));
        }
        return list;
    }

    private string? LocateTableFile(DbcObject table, List<string> candidates, string dbcPath, List<string> fieldNames)
    {
        // 1. A path string inside the property memo.
        foreach (var s in PrintableRuns(table.Property))
        {
            if (!s.EndsWith(".dbf", StringComparison.OrdinalIgnoreCase)) continue;
            var name = s.Replace('\\', Path.DirectorySeparatorChar);
            var p = DataSession.FindIgnoringCase(Path.Combine(Path.GetDirectoryName(dbcPath)!, name))
                    ?? DataSession.FindIgnoringCase(Path.Combine(Path.GetDirectoryName(dbcPath)!, Path.GetFileName(name)));
            if (p != null) return p;
        }
        // 2. A .DBF with the table's name whose backlink points to this database.
        var byName = candidates.FirstOrDefault(c => Path.GetFileNameWithoutExtension(c).Equals(table.Name, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        // 3. A back-linked .DBF with matching (truncated) field names.
        foreach (var c in candidates)
        {
            try
            {
                using var d = DbfTable.Open(c);
                if (d.DbcBacklink == null || !Path.GetFileName(d.DbcBacklink).Equals(Path.GetFileName(dbcPath), StringComparison.OrdinalIgnoreCase)) continue;
                if (d.Fields.Count == fieldNames.Count && d.Fields.Select((f, i) => fieldNames[i].ToUpperInvariant().StartsWith(f.Name)).All(x => x)) return c;
            }
            catch (IOException) { }
            catch (InvalidDataException) { }
        }
        return null;
    }

    private static IEnumerable<string> PrintableRuns(byte[] data)
    {
        var sb = new StringBuilder();
        foreach (var b in data)
        {
            if (b >= 32 && b < 127) sb.Append((char)b);
            else
            {
                if (sb.Length >= 4) yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length >= 4) yield return sb.ToString();
    }

    private static string? ExtractSql(byte[] property) =>
        PrintableRuns(property).FirstOrDefault(s => s.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase));
}
