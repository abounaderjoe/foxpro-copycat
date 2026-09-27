using System.Globalization;
using System.Text;
using System.Xml.Linq;
using JoePro.Core;
using JoePro.Data;
using JoePro.Language;
using JoePro.Legacy.Formats;

namespace JoePro.Runtime;

public sealed partial class Interpreter
{
    private bool ExecDataStmt(Stmt s)
    {
        switch (s)
        {
            case UseStmt u: ExecUse(u); return true;
            case SelectAreaStmt sa:
            {
                var v = EvalAlias(sa.Area);
                if (v.Kind == ValueKind.Number) Session.Select((int)v.AsNumber);
                else Session.Select(ResolveWorkArea(v).Number);
                return true;
            }
            case GoStmt g:
            {
                var wa = AreaOf(g.In);
                if (g.Where == "TOP") wa.GoTop();
                else if (g.Where == "BOTTOM") wa.GoBottom();
                else wa.Go((int)Eval(g.RecNo!).AsNumber);
                return true;
            }
            case SkipStmt sk:
                AreaOf(sk.In).Skip(sk.Count != null ? (int)Eval(sk.Count).AsNumber : 1);
                return true;
            case SeekStmt se:
            {
                var wa = AreaOf(se.In);
                var tag = se.Order != null ? FindTag(wa, se.Order) : null;
                wa.Seek(Eval(se.Key), tag);
                return true;
            }
            case LocateStmt lo: ExecLocate(lo); return true;
            case ContinueStmt: ExecContinue(); return true;
            case ReplaceStmt r: ExecReplace(r); return true;
            case AppendBlankStmt ab:
            {
                var wa = AreaOf(ab.In);
                if (ab.FromInsert) Notify("INSERT BLANK: the new record is appended at the end (Joe Pro never renumbers records).");
                AppendWithDefaults(wa, null);
                return true;
            }
            case AppendFromStmt af: ExecAppendFrom(af); return true;
            case DeleteStmt d: ExecDelete(d); return true;
            case PackStmt p:
            {
                var wa = AreaOf(p.In);
                if (!wa.Exclusive && !wa.IsCursor) throw new VfpException(110, "File must be opened exclusively.");
                wa.Table.Pack();
                wa.GoTop();
                return true;
            }
            case ZapStmt z:
            {
                var wa = AreaOf(z.In);
                if (!wa.Exclusive && !wa.IsCursor) throw new VfpException(110, "File must be opened exclusively.");
                wa.Table.Zap();
                wa.GoTop();
                return true;
            }
            case IndexStmt ix:
            {
                var wa = Session.Current;
                var kind = ix.Kind switch { "UNIQUE" => TagKind.Unique, "CANDIDATE" => TagKind.Candidate, _ => Options.IsOn("UNIQUE", false) ? TagKind.Unique : TagKind.Regular };
                var tag = new TagDef(ix.Tag, ExprPrinter.Print(ix.Key), ix.For != null ? ExprPrinter.Print(ix.For) : null, ix.Descending, kind);
                wa.Table.CreateTag(tag);
                wa.SetOrder(ix.Tag);
                wa.GoTop();
                Talk($"{wa.Table.RecordCount} records indexed.");
                return true;
            }
            case ReindexStmt:
                Session.Current.Table.Reindex();
                return true;
            case DeleteTagStmt dt:
            {
                var wa = Session.Current;
                var tags = dt.All ? wa.Table.Schema.Tags.Select(t => t.Name).ToList() : dt.Tags;
                foreach (var t in tags)
                {
                    if (wa.Order?.Name.Equals(t, StringComparison.OrdinalIgnoreCase) == true) wa.SetOrder(null);
                    wa.Table.DropTag(t);
                }
                return true;
            }
            case SetOrderStmt so:
            {
                var wa = AreaOf(so.In);
                if (so.Tag == null) wa.SetOrder(null);
                else wa.SetOrder(NameValue(so.Tag), so.Descending);
                return true;
            }
            case SetFilterStmt sf:
            {
                var wa = AreaOf(sf.In);
                wa.SetFilter(sf.Filter == null ? null : new BoundExpression(this, sf.Filter));
                return true;
            }
            case SetRelationStmt sr: ExecSetRelation(sr); return true;
            case SetStmt st: ExecSet(st); return true;
            case SetDatabaseStmt sd:
                Session.SetDatabase(sd.Name == null ? null : NameValue(sd.Name));
                return true;
            case CloseStmt c:
                switch (c.What)
                {
                    case "TABLES": Session.CloseTables(); break;
                    case "DATABASES" or "ALL":
                        Session.CloseAll();
                        if (c.What == "ALL") _procedureFiles.Clear();
                        break;
                    default: break;
                }
                return true;
            case CreateTableStmt ct: ExecCreateTable(ct); return true;
            case AlterTableStmt at: ExecAlterTable(at); return true;
            case AlterTableRuleStmt ar: ExecAlterTableRule(ar); return true;
            case TriggerStmt ts: ExecTrigger(ts); return true;
            case ProceduresFileStmt pf: ExecProceduresFile(pf); return true;
            case CreateDatabaseStmt cd:
                Session.CreateDatabase(NameValue(cd.Name));
                return true;
            case OpenDatabaseStmt od:
                Session.OpenDatabase(NameValue(od.Name));
                return true;
            case ListStmt ls: ExecList(ls); return true;
            case AggregateStmt ag: ExecAggregate(ag); return true;
            case ScatterStmt sc: ExecScatter(sc); return true;
            case GatherStmt ga: ExecGather(ga); return true;
            case TransactionStmt tr:
                if (tr.Kind == "BEGIN") Session.BeginTransaction();
                else if (tr.Kind == "END") Session.EndTransaction();
                else Session.Rollback();
                return true;
            case BrowseStmt br when Ui != null && AreaOf(br.Scope.In) is { InUse: true } bwa
                                    && Ui.Browse(bwa, br.Fields?.Select(f => ExprPrinter.Print(f)).ToList()):
                return true;
            case BrowseStmt br:
                ExecList(new ListStmt(false, br.Fields, br.Scope.Kind == "DEFAULT" ? br.Scope with { Kind = "ALL" } : br.Scope, false, false));
                return true;
            case CopyToStmt cp: ExecCopyTo(cp); return true;
            case ImportStmt im: ExecImport(im); return true;
            case SqlSelectStmt sq:
                SqlEngine.Execute(this, sq.Query, materialize: true);
                return true;
            case CreateViewStmt cv: Views.Create(this, cv); return true;
            case CopyFileStmt cf: ExecCopyFile(cf); return true;
            case AddTableStmt adt: ExecAddTable(adt); return true;
            case ReportFormStmt rf: ExecReportForm(rf); return true;
            case DefineMenuStmt dm: ExecDefineMenu(dm); return true;
            case DefinePadStmt dp: ExecDefinePad(dp); return true;
            case DefinePopupStmt dpo: ExecDefinePopup(dpo); return true;
            case DefineBarStmt db: ExecDefineBar(db); return true;
            case OnMenuStmt om: ExecOnMenu(om); return true;
            case ActivateMenuStmt am: ExecActivateMenu(am); return true;
            case MenuControlStmt mc: ExecMenuControl(mc); return true;
            case SetMenuFlagStmt smf: ExecSetMenuFlag(smf); return true;
            case RemoveTableStmt rmt: ExecRemoveTable(rmt); return true;
            case BlockStmt bs:
                foreach (var inner in bs.Stmts) ExecStmt(inner);
                return true;
            case BlankStmt bl:
            {
                var wa = bl.Scope.In != null ? ResolveWorkArea(EvalAlias(bl.Scope.In)) : Session.Current;
                if (!wa.InUse) throw VfpException.NoTableOpen();
                var idx = bl.Fields == null ? Enumerable.Range(0, wa.Table.Fields.Count).ToList()
                    : bl.Fields.Select(f => wa.FieldIndex(f) is var i and >= 0 ? i : throw VfpException.FieldNotFound(f)).ToList();
                ForEachInScope(wa, bl.Scope, "NEXT1", () => wa.Replace(idx.Select(i => (i, wa.Table.Fields[i].BlankValue())).ToList()));
                return true;
            }
            case CreateConnectionStmt cc: ExecCreateConnection(cc); return true;
            case DeleteDbObjectStmt dd: ExecDeleteDbObject(dd); return true;
            case RenameDbObjectStmt rd: ExecRenameDbObject(rd); return true;
            case SqlInsertStmt si: SqlEngine.Insert(this, si); return true;
            case SqlUpdateStmt su: SqlEngine.Update(this, su); return true;
            case SqlDeleteStmt sdel: SqlEngine.Delete(this, sdel); return true;
        }
        return ExecCommandStmt(s);
    }

    /// <summary>An expression bound to this interpreter for SET FILTER / SET RELATION.</summary>
    internal sealed class BoundExpression(Interpreter rt, Expr expr) : ICompiledExpression
    {
        public string Source => ExprPrinter.Print(expr);

        public Value Evaluate(IRecord record)
        {
            rt._recordContext.Push(record);
            try { return rt.Eval(expr); }
            finally { rt._recordContext.Pop(); }
        }
    }

    private TagDef FindTag(WorkArea wa, Expr tagExpr)
    {
        var name = NameValue(tagExpr);
        return wa.Table.Schema.FindTag(name) ?? throw new VfpException(1683, "Index tag is not found.", name);
    }

    // ---- USE -----------------------------------------------------------------------

    /// <summary>ADD TABLE: moves a free table into the current database (a .dbf is imported with its index tags).</summary>
    private void ExecAddTable(AddTableStmt adt)
    {
        var db = RequireDatabase();
        var source = NameValue(adt.Name);
        var longName = adt.LongName != null ? NameValue(adt.LongName) : null;
        List<FieldDef> fields;
        var rows = new List<(bool Deleted, Value[] Values)>();
        var tags = new List<TagDef>();
        string? moveFrom = null;
        if (Path.GetExtension(source).Equals(".dbf", StringComparison.OrdinalIgnoreCase))
        {
            var path = Session.ResolvePath(source, ".dbf");
            if (!File.Exists(path)) throw VfpException.FileNotFound(Path.GetFileName(path));
            using var dbf = DbfTable.Open(path);
            fields = dbf.Fields.Select(f => f.ToFieldDef()).ToList();
            foreach (var r in dbf.Records()) rows.Add((r.Deleted, r.Values));
            var cdxPath = DataSession.FindIgnoringCase(Path.ChangeExtension(path, ".cdx"));
            if (cdxPath != null)
            {
                using var cdx = CdxFile.Open(cdxPath, dbf.Encoding);
                tags.AddRange(cdx.Tags.Select(t => new TagDef(t.Name, t.KeyExpression, t.ForExpression, t.Descending,
                    t.Candidate ? TagKind.Candidate : t.Unique ? TagKind.Unique : TagKind.Regular)));
            }
            longName ??= Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            var path = Session.ResolvePath(source, Store.FreeTableExtension);
            if (!File.Exists(path)) throw VfpException.FileNotFound(Path.GetFileName(path));
            if (Session.OpenWorkAreas().Any(w => string.Equals(w.Source, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
                throw new VfpException(ErrorCodes.FileInUse, "File is in use.", Path.GetFileName(path));
            using (var other = new DataSession(Options.Clone(), this))
            {
                var t = other.Use(path).Table;
                fields = t.Fields.ToList();
                tags.AddRange(t.Schema.Tags.Select(g => g with { KeyColumn = "" }));
                foreach (var r in t.Scan(null, forward: true)) rows.Add((r.Deleted, r.Values));
            }
            moveFrom = path;
            longName ??= Path.GetFileNameWithoutExtension(path);
        }
        if (db.HasTable(longName)) throw new VfpException(1561, $"Table {longName.ToUpperInvariant()} is already in the database.");
        var table = db.CreateTable(new TableSchema(longName, fields.Select(f => f with { AutoIncNext = f.AutoIncNext })), this);
        table.Store.Batch(() => { foreach (var (del, vals) in rows) table.Append(vals, del); });
        foreach (var tag in tags) table.CreateTag(tag);
        if (moveFrom != null) File.Delete(moveFrom); // the table now lives in the database
        Talk($"Table {longName.ToUpperInvariant()} added to database {db.Name} ({rows.Count} records).");
    }

    /// <summary>REMOVE TABLE: takes a table out of the database, keeping it as a free table unless DELETE is given.</summary>
    private void ExecRemoveTable(RemoveTableStmt rmt)
    {
        var db = RequireDatabase();
        var name = NameValue(rmt.Name);
        if (!db.HasTable(name)) throw new VfpException(1562, $"Cannot find object {name.ToUpperInvariant()} in the database.");
        var t = db.OpenTable(name, this);
        foreach (var wa in Session.OpenWorkAreas().Where(w => w.TableOrNull == t).ToList()) wa.Close();
        if (!rmt.Delete)
        {
            var path = Path.Combine(Path.GetDirectoryName(db.Path)!, name.ToLowerInvariant() + Store.FreeTableExtension);
            using var other = new DataSession(Options.Clone(), this);
            var free = other.CreateTable(new TableSchema(name, t.Fields), free: true, path);
            free.Store.Batch(() => { foreach (var r in t.Scan(null, forward: true)) free.Append(r.Values, r.Deleted); });
            foreach (var tag in t.Schema.Tags) free.CreateTag(tag with { KeyColumn = "" });
        }
        db.DropTable(name);
    }

    private void ExecCopyFile(CopyFileStmt cf)
    {
        var src = NameValue(cf.Source).Replace('\\', Path.DirectorySeparatorChar);
        var dst = NameValue(cf.Destination).Replace('\\', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(src)) src = Path.Combine(Options.Default_, src);
        if (!Path.IsPathRooted(dst)) dst = Path.Combine(Options.Default_, dst);
        var dir = Path.GetDirectoryName(src)!;
        var pattern = Path.GetFileName(src);
        var files = pattern.IndexOfAny(['*', '?']) >= 0 ? Directory.GetFiles(dir, pattern) : [DataSession.FindIgnoringCase(src) ?? throw VfpException.FileNotFound(Path.GetFileName(src))];
        foreach (var f in files)
        {
            var target = Directory.Exists(dst) ? Path.Combine(dst, Path.GetFileName(f)) : dst;
            File.Copy(f, target, overwrite: true);
        }
    }

    private Store RequireDatabase() =>
        Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");

    private void ExecCreateConnection(CreateConnectionStmt cc)
    {
        string? Opt(Expr? e) => e == null ? null : Eval(e) is { Kind: ValueKind.Character } v ? v.AsString : NameValue(e);
        RequireDatabase().SaveConnection(new ConnectionDefinition
        {
            Name = NameValue(cc.Name), DataSource = Opt(cc.DataSource), UserId = Opt(cc.UserId), Password = Opt(cc.Password),
            Database = Opt(cc.Database), ConnectString = Opt(cc.ConnectString),
        });
    }

    private void ExecDeleteDbObject(DeleteDbObjectStmt dd)
    {
        var name = NameValue(dd.Name);
        var db = RequireDatabase();
        if (dd.Kind == "VIEW" && Session.OpenWorkAreas().Any(w => w.ViewState is ViewCursor vc && vc.ViewName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new VfpException(ErrorCodes.FileInUse, "File is in use.", name);
        if (!db.DeleteObject(dd.Kind, name))
            throw new VfpException(1562, $"Cannot find object {name.ToUpperInvariant()} in the database.");
    }

    private void ExecRenameDbObject(RenameDbObjectStmt rd)
    {
        var db = RequireDatabase();
        var from = NameValue(rd.From);
        var to = NameValue(rd.To);
        var json = db.GetObject(rd.Kind, from) ?? throw new VfpException(1562, $"Cannot find object {from.ToUpperInvariant()} in the database.");
        if (db.HasObject(rd.Kind, to)) throw new VfpException(1561, $"Object {to.ToUpperInvariant()} already exists.");
        if (rd.Kind == "VIEW")
        {
            var v = db.GetView(from)!;
            v.Name = to;
            db.DeleteObject(rd.Kind, from);
            db.SaveView(v);
        }
        else
        {
            var c = db.GetConnection(from)!;
            c.Name = to;
            db.DeleteObject(rd.Kind, from);
            db.SaveConnection(c);
        }
    }

    private void ExecUse(UseStmt u)
    {
        int? area = null;
        if (u.In != null)
        {
            var v = EvalAlias(u.In);
            area = v.Kind == ValueKind.Number ? ((int)v.AsNumber == 0 ? Session.FreeArea() : (int)v.AsNumber) : ResolveWorkArea(v).Number;
        }
        if (u.Table == null)
        {
            (area.HasValue ? Session.Area(area.Value) : Session.Current).Close();
            return;
        }
        var name = NameValue(u.Table);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var targetArea = area ?? Session.CurrentAreaNumber;
        Session.Area(targetArea).Close();
        WorkArea wa;
        if (ext == ".dbf")
        {
            wa = OpenLegacyDbf(name, targetArea, u.Alias != null ? NameValue(u.Alias) : null);
        }
        else if (ext == "" && Session.FindView(name) is { } view)
        {
            var viewAlias = u.Alias != null ? NameValue(u.Alias) : null;
            if (Session.FindAlias(viewAlias ?? view.View.Name) is { } open && open.Number != targetArea && !u.Again)
                throw new VfpException(ErrorCodes.FileInUse, "File is in use.", view.View.Name);
            wa = Views.Open(this, view.Database, view.View, targetArea, viewAlias, u.NoData);
        }
        else
        {
            wa = Session.Use(name, targetArea, u.Alias != null ? NameValue(u.Alias) : null, u.Again, u.Exclusive);
        }
        if (u.NoUpdate) wa.ReadOnly = true;
        if (u.Order != null)
        {
            wa.SetOrder(NameValue(u.Order));
            wa.GoTop();
        }
        if (area == null || area == Session.CurrentAreaNumber) Session.Select(wa.Number);
    }

    /// <summary>
    /// USE legacy.dbf opens a read-only snapshot of a FoxPro table in a cursor, so legacy data can be
    /// inspected immediately. IMPORT converts it into a Joe Pro table permanently.
    /// </summary>
    private WorkArea OpenLegacyDbf(string name, int area, string? alias)
    {
        var path = Session.ResolvePath(name, ".dbf");
        if (!File.Exists(path)) throw VfpException.FileNotFound(Path.GetFileName(path));
        using var dbf = DbfTable.Open(path);
        var schema = new TableSchema(alias ?? Path.GetFileNameWithoutExtension(path), dbf.Fields.Select(f => f.ToFieldDef() with { AutoIncNext = null }));
        Session.Select(area);
        var wa = Session.CreateCursor(schema, area);
        foreach (var rec in dbf.Records()) wa.Table.Append(rec.Values, rec.Deleted);
        wa.GoTop();
        wa.ReadOnly = true;
        Notify($"Opened {Path.GetFileName(path)} as a read-only snapshot. Use IMPORT to convert it into a Joe Pro table.");
        return wa;
    }

    // ---- REPORT FORM / LABEL FORM ---------------------------------------------------------

    /// <summary>The report engine, attached by the host (CLI, IDE). Without it REPORT FORM reports an error.</summary>
    public IReportRunner? Reports { get; set; }

    private void ExecReportForm(ReportFormStmt rf)
    {
        var (modern, legacy) = rf.Label ? (".jplabel", ".lbx") : (".jpreport", ".frx");
        if (rf.Name == null) throw new VfpException(1, $"{(rf.Label ? "LABEL" : "REPORT")} FORM ? needs a file name here; give the file name.");
        var name = EvalName(rf.Name);
        var path = ResolveClassFile(name, modern, legacy, _frame.Unit?.File) ?? throw VfpException.FileNotFound(Path.HasExtension(name) ? name : name + modern);
        if (Reports == null) throw new VfpException(1, "Reports are not available in this host (the report engine is not attached).");
        int? Int(Expr? e) => e == null ? null : (int)Eval(e).AsNumber;
        VfpObject? listener = null;
        if (rf.Listener != null)
            listener = Eval(rf.Listener) is { Kind: ValueKind.Object } lo ? (VfpObject)lo.AsObject : throw new VfpException(1924, "The OBJECT clause needs a ReportListener object.");
        Reports.Run(new ReportRequest
        {
            Path = path, Label = rf.Label, Scope = rf.Scope, Environment = rf.Environment,
            Heading = rf.Heading == null ? null : Formatter.ToDisplay(Eval(rf.Heading), Options).Trim(),
            NoConsole = rf.NoConsole, Plain = rf.Plain, RangeFrom = Int(rf.RangeFrom), RangeTo = Int(rf.RangeTo),
            Preview = rf.Preview, NoWait = rf.NoWait, ToPrinter = rf.ToPrinter, Prompt = rf.Prompt,
            ToFile = rf.ToFile == null ? null : Session.ResolvePath(EvalName(rf.ToFile), ""),
            Ascii = rf.Ascii, Summary = rf.Summary, Sample = rf.Sample, Listener = listener, ObjectType = Int(rf.ObjectType), NameVar = rf.NameVar,
        });
    }

    private string EvalName(Expr e) => e switch
    {
        LiteralExpr { Value.Kind: ValueKind.Character } l => l.Value.AsString.Trim().Trim('"', '\''),
        MacroExpr m => GetVariable(m.VarName).AsString.Trim(),
        _ => Eval(e).AsString.Trim(),
    };

    /// <summary>The record numbers a command scope (ALL/NEXT/REST/RECORD, FOR, WHILE) visits, in order (REPORT FORM).</summary>
    public List<int> RecordsInScope(WorkArea wa, Scope scope, string defaultKind = "ALL")
    {
        var list = new List<int>();
        if (!wa.InUse) return list;
        var saved = Session.CurrentAreaNumber;
        Session.Select(wa.Number);
        try { ForEachInScope(wa, scope, defaultKind, () => list.Add(wa.RecNo)); }
        finally { Session.Select(saved); }
        return list;
    }

    // ---- Scope iteration -----------------------------------------------------------------

    /// <summary>Visits records in a command scope, positioning the work area on each record that passes FOR/WHILE.</summary>
    internal int ForEachInScope(WorkArea wa, Scope scope, string defaultKind, Action action, bool snapshot = false)
    {
        var kind = scope.Kind == "DEFAULT" ? (scope.For != null || scope.While != null ? (defaultKind == "NEXT1" ? "ALL" : defaultKind) : defaultKind) : scope.Kind;
        if (scope.Kind == "DEFAULT" && scope.While != null && defaultKind != "ALL") kind = "REST";
        int count = 0;
        bool Passes() => scope.For == null || Truthy(Eval(scope.For));
        bool While() => scope.While == null || Truthy(Eval(scope.While));

        switch (kind)
        {
            case "NEXT1":
                if (!wa.Eof && Passes()) { action(); count++; }
                return count;
            case "RECORD":
                wa.Go((int)Eval(scope.Count!).AsNumber);
                if (Passes()) { action(); count++; }
                return count;
        }
        if (wa.TableOrNull is { } table && wa.BufferMode == 1)
        {
            // Many rows: commit once for the whole command.
            int batched = 0;
            table.Store.Batch(() => batched = ScopeLoop(wa, scope, kind, action, snapshot, Passes, While));
            return batched;
        }
        return ScopeLoop(wa, scope, kind, action, snapshot, Passes, While);
    }

    private int ScopeLoop(WorkArea wa, Scope scope, string kind, Action action, bool snapshot, Func<bool> Passes, Func<bool> While)
    {
        int count = 0;
        if (kind == "ALL") wa.GoTop();
        int limit = kind == "NEXT" ? (int)Eval(scope.Count!).AsNumber : int.MaxValue;
        if (snapshot)
        {
            // Collect matching records first so that changing key fields cannot skip or repeat records.
            var recs = new List<int>();
            int n = 0;
            while (!wa.Eof && n < limit)
            {
                if (!While()) break;
                if (Passes()) recs.Add(wa.RecNo);
                n++;
                wa.Skip();
            }
            foreach (var r in recs)
            {
                wa.Go(r);
                action();
                count++;
            }
            if (kind != "NEXT") { if (!wa.Eof) wa.GoBottom(); if (!wa.Eof) wa.Skip(); }
            return count;
        }
        int seen = 0;
        while (!wa.Eof && seen < limit)
        {
            Session.Select(wa.Number);
            if (!While()) break;
            if (Passes()) { action(); count++; }
            seen++;
            if (seen >= limit) break;
            wa.Skip();
        }
        return count;
    }

    private Flow ExecScan(ScanStmt sc)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var scope = sc.Scope;
        var kind = scope.Kind == "DEFAULT" ? "ALL" : scope.Kind;
        if (scope.Kind == "DEFAULT" && scope.While != null) kind = "REST";
        if (kind == "ALL") wa.GoTop();
        else if (kind == "RECORD") wa.Go((int)Eval(scope.Count!).AsNumber);
        int limit = kind is "NEXT" ? (int)Eval(scope.Count!).AsNumber : kind == "RECORD" ? 1 : int.MaxValue;
        int seen = 0;
        while (!wa.Eof && seen < limit)
        {
            Session.Select(wa.Number);
            if (scope.While != null && !Truthy(Eval(scope.While))) break;
            if (scope.For == null || Truthy(Eval(scope.For)))
            {
                var f = Exec(sc.Body);
                Session.Select(wa.Number);
                if (f == Flow.Exit) break;
                if (f == Flow.Return) return f;
            }
            seen++;
            if (wa.Eof || seen >= limit) break;
            wa.Skip();
        }
        Session.Select(wa.Number);
        return Flow.Normal;
    }

    private void ExecLocate(LocateStmt lo)
    {
        var wa = AreaOf(lo.Scope.In);
        _locates[(Session.Id, wa.Number)] = (lo.Scope.For, lo.Scope.While);
        var kind = lo.Scope.Kind == "DEFAULT" ? "ALL" : lo.Scope.Kind;
        if (kind == "ALL") wa.GoTop();
        Locate(wa, lo.Scope.For, lo.Scope.While, kind == "NEXT" ? (int)Eval(lo.Scope.Count!).AsNumber : int.MaxValue);
    }

    private void ExecContinue()
    {
        var wa = Session.Current;
        if (!_locates.TryGetValue((Session.Id, wa.Number), out var state))
            throw new VfpException(42, "CONTINUE without LOCATE.");
        if (!wa.Eof) wa.Skip();
        Locate(wa, state.For, state.While, int.MaxValue);
    }

    private void Locate(WorkArea wa, Expr? @for, Expr? @while, int limit)
    {
        var saved = Session.CurrentAreaNumber;
        Session.Select(wa.Number);
        try
        {
            int n = 0;
            while (!wa.Eof && n < limit)
            {
                if (@while != null && !Truthy(Eval(@while))) break;
                if (@for == null || Truthy(Eval(@for)))
                {
                    wa.Found = true;
                    return;
                }
                n++;
                wa.Skip();
            }
            wa.Found = false;
            if (!wa.Eof) { wa.GoBottom(); if (!wa.Eof) wa.Skip(); }
        }
        finally
        {
            Session.Select(saved);
        }
    }

    // ---- REPLACE / DELETE / APPEND ---------------------------------------------------------

    private void ExecReplace(ReplaceStmt r)
    {
        var wa = AreaOf(r.Scope.In);
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var saved = Session.CurrentAreaNumber;
        var count = ForEachInScope(wa, r.Scope, "NEXT1", () =>
        {
            var assignments = new List<(int, Value)>();
            foreach (var (fieldExpr, valueExpr, additive) in r.Items)
            {
                var (target, idx) = ResolveReplaceField(fieldExpr, wa);
                var v = Eval(valueExpr);
                if (additive && target.Get(idx) is { Kind: ValueKind.Character } old) v = Value.String(old.AsString + v.AsString);
                if (target == wa) assignments.Add((idx, v));
                else target.Replace(idx, v);
            }
            if (assignments.Count > 0) ReplaceWithRules(wa, assignments);
        }, snapshot: r.Scope.Kind != "DEFAULT" || r.Scope.For != null);
        Session.Select(saved);
        Talk($"{count} record{(count == 1 ? "" : "s")} replaced.");
    }

    private (WorkArea Area, int Field) ResolveReplaceField(Expr e, WorkArea defaultArea)
    {
        switch (e)
        {
            case NameExpr n:
            {
                var i = defaultArea.FieldIndex(n.Name);
                if (i < 0) throw VfpException.FieldNotFound(n.Name);
                return (defaultArea, i);
            }
            case MemberExpr { Target: NameExpr a } m:
            {
                var wa = Session.ResolveAlias(a.Name);
                var i = wa.FieldIndex(m.Name);
                if (i < 0) throw VfpException.FieldNotFound(m.Name);
                return (wa, i);
            }
            case AliasFieldExpr af:
            {
                var wa = Session.ResolveAlias(af.Alias);
                var i = wa.FieldIndex(af.Field);
                if (i < 0) throw VfpException.FieldNotFound(af.Field);
                return (wa, i);
            }
            case MacroExpr mac:
                return ResolveReplaceField(Parser.ParseExpression(GetVariable(mac.VarName).AsString), defaultArea);
            default:
                throw VfpException.Syntax("Invalid field reference in REPLACE.");
        }
    }

    /// <summary>REPLACE with field/record validation rules and the update trigger (database tables).</summary>
    internal void ReplaceWithRules(WorkArea wa, List<(int, Value)> assignments)
    {
        var before = wa.Current?.Clone();
        var keys = RiKeys(wa);
        void Change()
        {
            wa.Replace(assignments);
            foreach (var (idx, _) in assignments) CheckFieldRule(wa, idx);
            CheckRecordRule(wa);
            RunTrigger(wa, wa.Table.Schema.UpdateTrigger, "Update");
            if (keys.Count > 0) EnforceRiUpdate(wa, keys);
        }
        try
        {
            // Referential integrity may change other tables: the whole change is undone together.
            if (keys.Count > 0) wa.Table.Store.Atomic(Change);
            else Change();
        }
        catch
        {
            if (before != null) wa.Replace(assignments.Select(a => (a.Item1, before.Values[a.Item1])).ToList());
            throw;
        }
    }

    private void CheckFieldRule(WorkArea wa, int idx)
    {
        var f = wa.Table.Fields[idx];
        if (string.IsNullOrWhiteSpace(f.RuleExpr)) return;
        if (!Truthy(EvalInArea(wa, f.RuleExpr!)))
            throw new VfpException(ErrorCodes.FieldRuleViolated, f.RuleText ?? $"Field {f.Name} validation rule is violated.");
    }

    private void CheckRecordRule(WorkArea wa)
    {
        var s = wa.Table.Schema;
        if (string.IsNullOrWhiteSpace(s.RuleExpr)) return;
        if (!Truthy(EvalInArea(wa, s.RuleExpr!)))
            throw new VfpException(ErrorCodes.TableRuleViolated, s.RuleText ?? $"Record validation rule for {s.Name} is violated.");
    }

    private void RunTrigger(WorkArea wa, string? trigger, string kind)
    {
        if (string.IsNullOrWhiteSpace(trigger)) return;
        if (!Truthy(EvalInArea(wa, trigger!)))
            throw new VfpException(ErrorCodes.TriggerFailed, $"Trigger failed in {wa.Alias}.", kind);
    }

    public Value EvalInArea(WorkArea wa, string expr)
    {
        var saved = Session.CurrentAreaNumber;
        Session.Select(wa.Number);
        try { return Evaluate(expr); }
        finally { Session.Select(saved); }
    }

    /// <summary>APPEND BLANK / INSERT: applies field default values, then the insert trigger.</summary>
    internal void AppendWithDefaults(WorkArea wa, Value[]? values)
    {
        var fields = wa.Table.Fields;
        var vals = values ?? new Value[fields.Count];
        if (values == null)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                vals[i] = string.IsNullOrWhiteSpace(fields[i].DefaultExpr) ? fields[i].BlankValue() : EvalInArea(wa, fields[i].DefaultExpr!);
                if (fields[i].Nullable && string.IsNullOrWhiteSpace(fields[i].DefaultExpr) && Options.Null) vals[i] = Value.Null;
            }
        }
        wa.AppendBlank(vals);
        try
        {
            CheckRecordRule(wa);
            RunTrigger(wa, wa.Table.Schema.InsertTrigger, "Insert");
            EnforceRiInsert(wa);
        }
        catch
        {
            wa.Delete();
            throw;
        }
    }

    private void ExecDelete(DeleteStmt d)
    {
        var wa = AreaOf(d.Scope.In);
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var count = ForEachInScope(wa, d.Scope, "NEXT1", () =>
        {
            if (d.Recall) wa.Delete(false);
            else DeleteWithRules(wa);
        }, snapshot: true);
        Talk($"{count} record{(count == 1 ? "" : "s")} {(d.Recall ? "recalled" : "deleted")}.");
    }

    private void ExecAppendFrom(AppendFromStmt af)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
        if (wa.BufferMode == 1) wa.Table.Store.Batch(() => AppendFromCore(wa, af));
        else AppendFromCore(wa, af);
    }

    private void AppendFromCore(WorkArea wa, AppendFromStmt af)
    {
        if (af.Type == "ARRAY")
        {
            var arr = FindVariable(NameValue(af.Source))?.Array ?? throw VfpException.VariableNotFound(NameValue(af.Source));
            int arrRows = arr.TwoDimensional ? arr.Rows : 1;
            int cols = arr.TwoDimensional ? arr.Cols : arr.Length;
            for (int r = 1; r <= arrRows; r++)
            {
                var vals = new Value[wa.Table.Fields.Count];
                for (int c = 0; c < vals.Length; c++)
                    vals[c] = c < cols ? wa.Table.Fields[c].Coerce(arr.TwoDimensional ? arr[r, c + 1] : arr[c + 1]) : wa.Table.Fields[c].BlankValue();
                wa.AppendBlank(vals);
            }
            return;
        }
        var name = NameValue(af.Source);
        var type = af.Type;
        if (type is "CSV" or "DELIMITED" or "SDF")
        {
            AppendFromText(wa, Session.ResolvePath(name, type == "SDF" ? ".txt" : ".csv"), type);
            return;
        }
        IEnumerable<(Dictionary<string, Value> Row, bool Deleted)> rows;
        if (Path.GetExtension(name).Equals(".dbf", StringComparison.OrdinalIgnoreCase) || type is "FOXPLUS" or "FOX2X")
        {
            var path = Session.ResolvePath(name, ".dbf");
            using var dbf = DbfTable.Open(path);
            rows = dbf.Records().Select(r => (dbf.Fields.Select((f, i) => (f.Name, r.Values[i])).ToDictionary(x => x.Name, x => x.Item2, StringComparer.OrdinalIgnoreCase), r.Deleted)).ToList();
        }
        else
        {
            using var other = new DataSession(Options.Clone(), this);
            var src = other.Use(name);
            var list = new List<(Dictionary<string, Value>, bool)>();
            src.GoTop();
            while (!src.Eof)
            {
                list.Add((src.Table.Fields.Select((f, i) => (f.Name, src.Get(i))).ToDictionary(x => x.Name, x => x.Item2, StringComparer.OrdinalIgnoreCase), src.Deleted));
                src.Skip();
            }
            rows = list;
        }
        int count = 0;
        foreach (var (row, deleted) in rows)
        {
            if (deleted && Options.Deleted) continue;
            var vals = wa.Table.Fields.Select(f =>
                (af.Fields == null || af.Fields.Contains(f.Name, StringComparer.OrdinalIgnoreCase)) && row.TryGetValue(f.Name, out var v)
                    ? ConvertForField(f, v) : f.BlankValue()).ToArray();
            wa.AppendBlank(vals);
            if (af.For != null && !Truthy(Eval(af.For))) { wa.Delete(); continue; }
            count++;
        }
        Talk($"{count} records added.");
    }

    private void AppendFromText(WorkArea wa, string path, string type)
    {
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var vals = new Value[wa.Table.Fields.Count];
            if (type == "SDF")
            {
                int pos = 0;
                for (int i = 0; i < vals.Length; i++)
                {
                    var f = wa.Table.Fields[i];
                    var piece = pos < line.Length ? line.Substring(pos, Math.Min(f.Width, line.Length - pos)) : "";
                    pos += f.Width;
                    vals[i] = TextToValue(f, piece);
                }
            }
            else
            {
                var parts = SplitCsv(line);
                for (int i = 0; i < vals.Length; i++) vals[i] = TextToValue(wa.Table.Fields[i], i < parts.Count ? parts[i] : "");
            }
            wa.AppendBlank(vals);
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (q)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') q = false;
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { list.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }

    private Value TextToValue(FieldDef f, string text)
    {
        text = text.Trim();
        return f.Type switch
        {
            'C' or 'V' or 'M' => f.Coerce(Value.String(text)),
            'N' or 'F' or 'B' or 'I' or 'Y' => f.Coerce(Value.Number(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0)),
            'D' => text.Length == 8 && DateOnly.TryParseExact(text, "yyyyMMdd", out var dt) ? Value.DateOf(dt) : Value.FromJulian(Formatter.ParseDate(text, Options)),
            'L' => Value.Logical(text.Length > 0 && "TtYy".Contains(text[0])),
            _ => f.BlankValue(),
        };
    }

    private static Value ConvertForField(FieldDef f, Value v)
    {
        if (v.IsNull) return f.Nullable ? v : f.BlankValue();
        try { return f.Coerce(v); }
        catch (VfpException) { return f.BlankValue(); }
    }

    // ---- SET -------------------------------------------------------------------------

    private void ExecSetRelation(SetRelationStmt sr)
    {
        var parent = Session.Current;
        if (sr.Off)
        {
            var child = ResolveWorkArea(EvalAlias(sr.OffInto!));
            parent.Relations.RemoveAll(r => r.Child == child);
            return;
        }
        if (!sr.Additive) parent.Relations.Clear();
        foreach (var (expr, into) in sr.Relations)
        {
            var child = ResolveWorkArea(EvalAlias(into));
            parent.Relations.Add((new BoundExpression(this, expr), child));
        }
        if (parent.InUse && !parent.Eof) parent.Go(parent.RecNo);
    }

    private void ExecSet(SetStmt st)
    {
        bool On() => st.Value switch
        {
            "ON" => true,
            "OFF" => false,
            _ when st.Expr is MacroExpr m => GetVariable(m.VarName).AsString.Trim().Equals("ON", StringComparison.OrdinalIgnoreCase),
            _ => throw VfpException.Syntax($"SET {st.Option} expects ON or OFF."),
        };
        int Num() => st.Expr != null ? (int)Eval(st.Expr).AsNumber : int.Parse(st.Value ?? "0", CultureInfo.InvariantCulture);
        string Text() => st.Expr != null ? Formatter.ToDisplay(Eval(st.Expr), Options).Trim() : (st.Value ?? "");
        var o = Options;
        if (st.Option == "__CREATE" && st.Value == "VIEW")
        {
            // CREATE SQL VIEW without a definition: the View Designer.
            if (!TryOpenDesigner(true, "VIEW")) throw VfpException.NotSupported("CREATE SQL VIEW without AS (the View Designer)");
            return;
        }
        switch (st.Option)
        {
            case "CLASSLIB":
                SetClassLib(st.Raw);
                break;
            case "SYSMENU":
                ExecSetSysMenu(string.Join(" ", st.Raw.Select(t => t.Text)));
                break;
            case "TEXTMERGE":
            {
                // SET TEXTMERGE [ON | OFF] [TO [file] [ADDITIVE]] [NOSHOW | SHOW]
                var words = st.Raw.Select(t => t.Text.ToUpperInvariant()).ToList();
                if (words.Contains("ON")) TextMergeOn = true;
                if (words.Contains("OFF")) TextMergeOn = false;
                if (words.Contains("NOSHOW")) _textMergeShow = false;
                if (words.Contains("SHOW")) _textMergeShow = true;
                var toAt = words.IndexOf("TO");
                if (toAt >= 0)
                {
                    var target = st.Raw.Skip(toAt + 1).TakeWhile(t => !t.Text.Equals("ADDITIVE", StringComparison.OrdinalIgnoreCase)
                        && !t.Text.Equals("NOSHOW", StringComparison.OrdinalIgnoreCase) && !t.Text.Equals("SHOW", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (target.Count == 0) _textMergeFile = null;
                    else
                    {
                        var name = target[0].Kind == TokenKind.String ? target[0].Text : string.Concat(target.Select(t => t.Text));
                        if (target[0].IsOp("(")) name = Eval(Parser.ParseExpression(string.Join(" ", target.Select(t => t.Kind == TokenKind.String ? "\"" + t.Text + "\"" : t.Text)))).AsString;
                        _textMergeFile = Session.ResolvePath(name.Trim(), ".txt");
                        if (!words.Contains("ADDITIVE")) File.WriteAllText(_textMergeFile, "");
                    }
                }
                break;
            }
            case "EXACT": o.Exact = On(); break;
            case "ANSI": o.Ansi = On(); break;
            case "DELETED": o.Deleted = On(); break;
            case "NEAR": o.Near = On(); break;
            case "TALK": o.Talk = st.Value != "OFF"; break;
            case "CENTURY": if (st.Value is "ON" or "OFF") o.Century = On(); break;
            case "NULL": o.Null = On(); break;
            case "SECONDS": o.Seconds = On(); break;
            case "EXCLUSIVE": o.Exclusive = On(); break;
            case "SAFETY": o.Safety = On(); break;
            case "OPTIMIZE": o.Optimize = On(); break;
            case "HOURS": o.Hours = st.Expr != null || st.Value is not ("ON" or "OFF") ? Num() : 12; break;
            case "DECIMALS": o.Decimals = Num(); break;
            case "ENGINEBEHAVIOR": o.EngineBehavior = Num(); break;
            case "COLLATE": o.Collate = Text().ToUpperInvariant(); break;
            case "POINT": o.Point = Text() is { Length: > 0 } p ? p[0] : '.'; break;
            case "SEPARATOR": o.Separator = Text() is { Length: > 0 } sp ? sp[0] : ','; break;
            case "MARK": o.Mark = Text() is { Length: > 0 } mk ? mk[0] : null; break;
            case "DATE":
            {
                var t = Text().ToUpperInvariant();
                if (t.Length == 0) break;
                o.Date = t switch
                {
                    "AMERICAN" => DateFormat.American, "ANSI" => DateFormat.Ansi, "BRITISH" => DateFormat.British,
                    "FRENCH" => DateFormat.French, "GERMAN" => DateFormat.German, "ITALIAN" => DateFormat.Italian,
                    "JAPAN" => DateFormat.Japan, "USA" => DateFormat.Usa, "MDY" => DateFormat.Mdy, "DMY" => DateFormat.Dmy,
                    "YMD" => DateFormat.Ymd, "TAIWAN" => DateFormat.Taiwan, "LONG" => DateFormat.Long, "SHORT" => DateFormat.Short,
                    _ => throw VfpException.Syntax($"Unknown date format {t}."),
                };
                break;
            }
            case "DEFAULT":
            {
                var path = Text().Trim('"', '\'');
                var full = Path.GetFullPath(Path.Combine(o.Default_, path));
                if (!Directory.Exists(full)) throw new VfpException(202, "Invalid path or file name.", path);
                o.Default_ = full;
                break;
            }
            case "PATH":
                o.Path.Clear();
                o.Path.AddRange(Text().Trim('"', '\'').Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                break;
            case "STEP":
                if (st.Value == "ON")
                {
                    if (Debugger != null) Debugger.Suspend(_frame, _frame.Line);
                    else Notify("SET STEP ON: no debugger is attached; continuing.");
                }
                break;
            case "EVENTTRACKING":
                if (Debugger != null && st.Value is "ON" or "OFF") Debugger.EventTracking = st.Value == "ON";
                break;
            case "COVERAGE":
            {
                var file = st.Expr != null ? Text() : (st.Value ?? "").Trim();
                var additive = file.EndsWith(" ADDITIVE", StringComparison.OrdinalIgnoreCase);
                if (additive) file = file[..^" ADDITIVE".Length].Trim();
                file = file.Trim('"', '\'');
                var dbg = Debugger ?? new Debugger(this);
                if (file.Length == 0) dbg.StopCoverage();
                else dbg.StartCoverage(Path.Combine(Options.Default_, file), additive);
                break;
            }
            case "DATASESSION":
                SetDataSession(Num());
                break;
            case "CONSOLE": o.Console = On(); EnsureRouter(); break;
            case "ASSERTS": o.Asserts = On(); break;
            case "NULLDISPLAY": o.NullDisplay = st.Expr != null || st.Value != null ? Text().Trim('"', '\'') : null; break;
            case "MEMOWIDTH": o.MemoWidth = Math.Clamp(Num(), 8, 8192); break;
            case "FDOW": o.Fdow = Math.Clamp(Num(), 1, 7); break;
            case "FWEEK": o.Fweek = Math.Clamp(Num(), 1, 3); break;
            case "ALTERNATE":
                SetAlternate(st, st.Expr != null ? Text() : st.Value);
                break;
            case "MESSAGE":
                // SET MESSAGE TO [cText]: the status bar message.
                StoreSetting(st);
                if (st.Expr != null) Notify(Text());
                break;
            case "MULTILOCKS" or "REPROCESS" or "STATUS" or "ECHO" or "ESCAPE" or "BELL" or "NOTIFY"
                or "CPDIALOG" or "STRICTDATE" or "FIXED" or "UDFPARMS" or "COMPATIBLE" or "HELP"
                or "RESOURCE" or "CURSOR" or "TYPEAHEAD" or "CARRY" or "CONFIRM" or "FULLPATH" or "UNIQUE" or "LOCK"
                or "REFRESH" or "CURRENCY" or "CLOCK" or "ROLLOVER" or "BLOCKSIZE" or "VARCHARMAPPING"
                or "TABLEVALIDATE" or "LIBRARY"
                or "AUTOINCERROR" or "INDEX" or "KEY" or "SKIP" or "DEBUG" or "PRINTER" or "DEVICE" or "LOGERRORS"
                or "SYSFORMATS" or "NOCPTRANS" or "OLEOBJECT" or "SQLBUFFERING" or "SPACE" or "HEADINGS" or "":
                StoreSetting(st);
                if (!UnsupportedSettings.Contains(st.Option)) UnsupportedSettings.Add(st.Option);
                break;
            default:
                if (st.Option.StartsWith("__", StringComparison.Ordinal)) ExecIdeCommand(st.Option[2..], st.Value ?? "");
                else if (!UnsupportedSettings.Contains(st.Option)) UnsupportedSettings.Add(st.Option);
                break;
        }
    }

    /// <summary>Commands that open designers or touch the OS. Designers arrive in later phases.</summary>
    private void ExecIdeCommand(string verb, string rest)
    {
        switch (verb)
        {
            case "ERASE" or "DELETE":
                EraseCommand(rest);
                break;
            case "DELETE DATABASE":
                DeleteDatabaseCommand(rest);
                break;
            case "PACK DATABASE":
                PackDatabaseCommand();
                break;
            case "COMPILE DATABASE":
                CompileDatabaseCommand(rest);
                break;
            case "DROP TABLE":
                DropTableCommand(rest);
                break;
            case "DIR":
                ExecDir(rest);
                break;
            case "TYPE":
                ExecType(rest);
                break;
            case "ON":
                ExecOnCommand(rest);
                break;
            case "RELEASE":
                ReleaseLibraryCommand(rest);
                break;
            case "RUN":
                ExecRun(rest);
                break;
            case "KEYBOARD":
                ExecKeyboard(rest);
                break;
            case "PUSH" or "POP" when System.Text.RegularExpressions.Regex.Match(rest.Trim(), @"^(MENU|POPUP)\s+(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } mp:
            {
                bool popup = mp.Groups[1].Value.Equals("POPUP", StringComparison.OrdinalIgnoreCase);
                var name = mp.Groups[2].Value.Trim('"', '\'');
                if (verb == "PUSH") Menus.Push(popup, name);
                else if (!Menus.Pop(popup, name)) throw new VfpException(1777, $"{(popup ? "Popup" : "Menu")} {name.ToUpperInvariant()} has not been pushed.");
                Ui?.MenusChanged();
                break;
            }
            case "DEBUG":
                if (Ui?.OpenDesigner(new DesignerRequest("DEBUGGER", "", false)) != true) Notify("DEBUG: the debugger is available in the Joe Pro IDE.");
                break;
            case "PUSH" or "POP" when rest.TrimStart().StartsWith("KEY", StringComparison.OrdinalIgnoreCase):
                PushPopKey(verb == "PUSH", rest.TrimStart()[3..]);
                break;
            case "LIST" or "DISPLAY":
                ListCommand(verb == "DISPLAY", rest);
                break;
            case "ACTIVATE" when rest.Trim().Equals("SCREEN", StringComparison.OrdinalIgnoreCase):
                break; // output already goes to the screen
            case "MODIFY" or "MODI" or "ZOOM" when ScreenWindowCommand(verb == "ZOOM" ? "ZOOM" : "MODIFY", rest):
                break;
            case "CREATE" or "CREA" when CreateFromCommand(rest):
                break;
            case "EJECT" or "PRINTJOB" or "ENDPRINTJOB" or "FREE" or "ASSIST" or "MENU":
                Notify($"{verb} {rest}".Trim() + $" is not supported: {Builtins.CommandCoverage.ReasonFor(verb)}");
                break;
            case "RENAME" when rest.TrimStart().StartsWith("TABLE ", StringComparison.OrdinalIgnoreCase):
                RenameTableCommand(rest.TrimStart()[6..]);
                break;
            case "RENAME" when rest.TrimStart().StartsWith("CLASS ", StringComparison.OrdinalIgnoreCase):
                RenameClassCommand(rest.TrimStart()[6..]);
                break;
            case "ADD" or "REMOVE":
                if (verb == "ADD") AddClassCommand(rest.TrimStart()[5..]);
                else RemoveClassCommand(rest.TrimStart()[5..]);
                break;
            case "RENAME":
            {
                var parts = System.Text.RegularExpressions.Regex.Split(rest, @"\s+TO\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (parts.Length != 2) throw VfpException.Syntax();
                var from = NameText(parts[0]);
                var src = Session.ResolvePath(from, "");
                if (!File.Exists(src)) throw VfpException.FileNotFound(from);
                File.Move(src, Path.Combine(Options.Default_, NameText(parts[1])));
                break;
            }
            case "MD" or "MKDIR":
                Directory.CreateDirectory(Path.Combine(Options.Default_, NameText(rest)));
                break;
            case "RD" or "RMDIR":
            {
                var dir = Path.Combine(Options.Default_, NameText(rest));
                if (!Directory.Exists(dir)) throw new VfpException(202, "Invalid path or file name.", rest.Trim());
                Directory.Delete(dir);
                break;
            }
            case "ASSERT":
                AssertCommand(rest);
                break;
            case "FLUSH" or "UNLOCK" or "DOEVENTS" or "EXTERNAL" or "SLEEP" or "LOCK" or "VALIDATE":
                if (verb == "UNLOCK") foreach (var w in Session.OpenWorkAreas()) w.Unlock();
                break;
            case "BUILD":
                BuildCommand(rest);
                break;
            case "CREATE" or "CREA" when TryOpenDesigner(true, rest):
                break;
            case "MODIFY" or "MODI" when TryOpenDesigner(false, rest):
                break;
            case "MODIFY" or "MODI":
            {
                var m = System.Text.RegularExpressions.Regex.Match(rest, @"^(COMM\w*|FILE)\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && Ui != null)
                {
                    var file = m.Groups[2].Value.Trim().Trim('"', '\'');
                    var isCommand = m.Groups[1].Value.StartsWith("COMM", StringComparison.OrdinalIgnoreCase);
                    var path = Path.Combine(Options.Default_, isCommand && !Path.HasExtension(file) ? file + ".prg" : file);
                    if (Ui.ModifyFile(DataSession.FindIgnoringCase(path) ?? path)) break;
                }
                Notify($"MODIFY {rest}: this designer is not available in this build (see roadmap).");
                break;
            }
            case "DEBUGOUT":
            {
                var text = rest.Trim().Length == 0 ? "" : string.Join(" ",
                    Parser.ParseInteractive("? " + rest).Main.OfType<PrintStmt>().SelectMany(p => p.Items).Select(e => Formatter.ToDisplay(Eval(e), Options).Trim()));
                if (Debugger != null) Debugger.Output(text);
                else Notify("DEBUGOUT " + text);
                break;
            }
            default:
                Notify($"{verb} {rest}: this designer/command is not available in this build (see roadmap).");
                break;
        }
    }

    /// <summary>A file or folder name argument in raw command text: (expression), "quoted" or plain.</summary>
    internal string NameText(string raw)
    {
        var t = raw.Trim();
        if (t.StartsWith('(') && t.EndsWith(')')) return Eval(Parser.ParseExpression(t)).AsString.Trim();
        return t.Trim('"', '\'').Trim();
    }

    /// <summary>Designer file extensions by kind (the first is the native format, later ones are legacy formats read by conversion).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> DesignerExtensions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["FORM"] = [".jpform", ".scx"],
        ["CLASS"] = [".jpclass", ".vcx"],
        ["CLASSLIB"] = [".jpclass", ".vcx"],
        ["REPORT"] = [".jpreport", ".frx"],
        ["LABEL"] = [".jplabel", ".lbx"],
        ["MENU"] = [".jpmenu", ".mnx"],
        ["QUERY"] = [".jpquery", ".qpr"],
        ["PROJECT"] = [".jpproj", ".pjx"],
        ["DATABASE"] = [".jpdb", ".dbc"],
    };

    /// <summary>CREATE/MODIFY FORM|CLASS|REPORT|… name: hands the request to the UI host's designer.</summary>
    private bool TryOpenDesigner(bool create, string rest)
    {
        var m = System.Text.RegularExpressions.Regex.Match(rest.Trim(),
            @"^(?<kind>FORM|CLASSLIB|CLASS|REPO\w*|LABE?L?|MENU|QUER\w*|PROJ\w*|DATA\w*|SCREEN|STRU\w*|PROC\w*|VIEW)\b\s*(?<rest>.*)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success)
        {
            // CREATE [name | ?]: a new table in the Table Designer.
            if (!create || Ui == null) return false;
            var tableName = TokenizeDesignerArgs(rest).FirstOrDefault(w => !IsDesignerClause(w)) ?? "";
            if (tableName is "" or "?" || tableName.Contains(' ')) return false;
            var storePath = Session.CurrentDatabase is { } cdb && !Path.HasExtension(tableName) ? cdb.Path : Session.ResolvePath(tableName, Store.FreeTableExtension);
            return Ui.OpenDesigner(new DesignerRequest("TABLE", storePath, true, Path.GetFileNameWithoutExtension(tableName)));
        }
        var kindWord = m.Groups["kind"].Value.ToUpperInvariant();
        // Class libraries are files the runtime can create without a user interface.
        if (create && kindWord == "CLASSLIB") { CreateClassLibrary(m.Groups["rest"].Value); return true; }
        if (create && kindWord.StartsWith("REPO") && System.Text.RegularExpressions.Regex.IsMatch(m.Groups["rest"].Value, @"\bFROM\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            QuickReportCommand(m.Groups["rest"].Value);
            return true;
        }
        if (create && kindWord.StartsWith("PROJ") && Ui == null)
        {
            // Without a Project Manager: an empty project file.
            var projName = TokenizeDesignerArgs(m.Groups["rest"].Value).FirstOrDefault() ?? throw VfpException.Syntax("CREATE PROJECT needs a name.");
            var projPath = Path.Combine(Options.Default_, Path.HasExtension(projName) ? projName : projName + ".jpproj");
            new JoePro.Documents.Projects.ProjectDocument { Name = Path.GetFileNameWithoutExtension(projPath) }.Save(projPath);
            return true;
        }
        if (create && kindWord == "CLASS")
        {
            var created = CreateClassCommand(m.Groups["rest"].Value);
            if (Ui != null) Ui.OpenDesigner(created);
            return true;
        }
        if (kindWord == "VIEW")
        {
            // CREATE VIEW / MODIFY VIEW name: a view of the current database in the View Designer.
            if (Ui == null) return false;
            var db = Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
            var viewName = TokenizeDesignerArgs(m.Groups["rest"].Value).FirstOrDefault(w => !IsDesignerClause(w)) ?? "";
            if (viewName.StartsWith('(') && viewName.EndsWith(')')) viewName = Eval(Parser.ParseExpression(viewName)).AsString.Trim();
            if (!create && db.GetView(viewName) == null) throw new VfpException(1562, $"Cannot find object {viewName.ToUpperInvariant()} in the database.");
            return Ui.OpenDesigner(new DesignerRequest("VIEW", db.Path, create, viewName.Length > 0 ? viewName : null));
        }
        if (!create && (kindWord.StartsWith("STRU") || kindWord.StartsWith("PROC")))
        {
            if (Ui == null) return false;
            if (kindWord.StartsWith("PROC"))
            {
                var db = Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
                return Ui.OpenDesigner(new DesignerRequest("PROCEDURE", db.Path, false));
            }
            // MODIFY STRUCTURE: the table in the current work area, in the Table Designer.
            var wa = Session.Current;
            if (!wa.InUse) throw VfpException.NoTableOpen();
            if (wa.IsCursor) throw new VfpException(ErrorCodes.InvalidArgument, "A cursor's structure cannot be modified.");
            var store = wa.Table.Store;
            return Ui.OpenDesigner(new DesignerRequest("TABLE", store.Path, false, wa.Table.Name));
        }
        if (Ui == null) return false;
        var kind = m.Groups["kind"].Value.ToUpperInvariant() switch
        {
            "SCREEN" => "FORM",
            var k when k.StartsWith("REPO") => "REPORT",
            var k when k.StartsWith("LAB") => "LABEL",
            var k when k.StartsWith("QUER") => "QUERY",
            var k when k.StartsWith("PROJ") => "PROJECT",
            var k when k.StartsWith("DATA") => "DATABASE",
            var k => k,
        };
        var words = TokenizeDesignerArgs(m.Groups["rest"].Value);
        string? Clause(string kw)
        {
            var i = words.FindIndex(w => w.Equals(kw, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < words.Count ? words[i + 1] : null;
        }
        var name = words.Count > 0 && !IsDesignerClause(words[0]) ? words[0] : "";
        if (name == "?") name = "";
        if (name.StartsWith('(') && name.EndsWith(')')) name = Eval(Parser.ParseExpression(name)).AsString.Trim();
        var exts = DesignerExtensions[kind];
        string Resolve(string file, string[] extensions)
        {
            if (file.Length == 0) return "";
            var full = Path.IsPathRooted(file) ? file : Path.Combine(Options.Default_, file);
            if (Path.HasExtension(file)) return DataSession.FindIgnoringCase(full) ?? full;
            foreach (var ext in extensions)
                if (DataSession.FindIgnoringCase(full + ext) is { } found) return found;
            return full + extensions[0];
        }
        DesignerRequest request;
        if (kind == "CLASS")
        {
            var lib = Clause("OF") ?? "";
            if (lib.Length == 0) throw VfpException.Syntax($"{(create ? "CREATE" : "MODIFY")} CLASS needs OF classlibrary.");
            request = new DesignerRequest(kind, Resolve(lib, exts), create, name, Clause("AS"), Clause("FROM") is { } from ? Resolve(from, exts) : null);
        }
        else if (kind == "DATABASE" && name.Length == 0 && !create && Session.CurrentDatabase is { } current)
            request = new DesignerRequest(kind, current.Path, false);
        else request = new DesignerRequest(kind, Resolve(name, exts), create, BaseClass: Clause("AS"), BaseLibrary: Clause("FROM") is { } f ? Resolve(f, DesignerExtensions["CLASS"]) : null);
        return Ui.OpenDesigner(request);
    }

    // ---- Class libraries: CREATE CLASSLIB, CREATE CLASS, ADD/RENAME/REMOVE CLASS -------------------------

    /// <summary>Resolves a class library name for writing: .jpclass (a legacy .vcx cannot be written; import it first).</summary>
    private string WritableLibrary(string name)
    {
        name = name.Trim().Trim('"', '\'');
        if (name.Length == 0) throw VfpException.Syntax("A class library name is required.");
        if (name.StartsWith('(') && name.EndsWith(')')) name = Eval(Parser.ParseExpression(name)).AsString.Trim();
        var full = Path.IsPathRooted(name) ? name : Path.Combine(Options.Default_, name);
        if (!Path.HasExtension(full)) full += ".jpclass";
        full = DataSession.FindIgnoringCase(full) ?? full;
        if (Path.GetExtension(full).Equals(".vcx", StringComparison.OrdinalIgnoreCase))
            throw new VfpException(1102, $"{Path.GetFileName(full)} is a legacy class library and is read-only. IMPORT FOXPRO it (or open it and save it) to get a .jpclass library.", full);
        return full;
    }

    /// <summary>Resolves a class library name for reading: .jpclass first, then a legacy .vcx.</summary>
    private string ReadableLibrary(string name)
    {
        name = name.Trim().Trim('"', '\'');
        if (name.StartsWith('(') && name.EndsWith(')')) name = Eval(Parser.ParseExpression(name)).AsString.Trim();
        var full = Path.IsPathRooted(name) ? name : Path.Combine(Options.Default_, name);
        if (Path.HasExtension(full)) return DataSession.FindIgnoringCase(full) ?? throw new VfpException(1, "File does not exist.", full);
        return DataSession.FindIgnoringCase(full + ".jpclass") ?? DataSession.FindIgnoringCase(full + ".vcx") ?? throw new VfpException(1, "File does not exist.", full + ".jpclass");
    }

    private void CreateClassLibrary(string rest)
    {
        var path = WritableLibrary(TokenizeDesignerArgs(rest).FirstOrDefault() ?? "");
        if (File.Exists(path))
        {
            if (Options.Safety) throw new VfpException(7, "File already exists.", path);
        }
        JoePro.Documents.ClassFileWriter.Save(JoePro.Documents.ClassLibrary.Empty(), path);
        InvalidateClassLibrary(path);
    }

    /// <summary>CREATE CLASS name OF lib AS parent [FROM parentlib]: adds the class to the library (creating it if needed).</summary>
    private DesignerRequest CreateClassCommand(string rest)
    {
        var words = TokenizeDesignerArgs(rest);
        string? Clause(string kw)
        {
            var i = words.FindIndex(w => w.Equals(kw, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < words.Count ? words[i + 1] : null;
        }
        var name = words.Count > 0 && !IsDesignerClause(words[0]) ? words[0] : throw VfpException.Syntax("CREATE CLASS needs a class name.");
        var lib = WritableLibrary(Clause("OF") ?? throw VfpException.Syntax("CREATE CLASS needs OF classlibrary."));
        var parent = Clause("AS") ?? "Custom";
        string? parentLib = null;
        if (Clause("FROM") is { } from)
        {
            var fromPath = ReadableLibrary(from);
            parentLib = Path.GetRelativePath(Path.GetDirectoryName(lib)!, fromPath).Replace('\\', '/');
            if (Path.GetFullPath(fromPath).Equals(Path.GetFullPath(lib), StringComparison.OrdinalIgnoreCase)) parentLib = null;
        }
        else if (!BaseClasses.Exists(parent) && !(File.Exists(lib) && JoePro.Documents.ClassLibrary.Load(lib).Find(parent) != null))
            throw new VfpException(1733, $"Class definition {parent.ToUpperInvariant()} is not found.", parent);
        if (BaseClasses.Exists(parent) && parentLib == null) parent = BaseClasses.Canonical(parent);
        var file = File.Exists(lib) ? JoePro.Documents.ClassLibrary.Load(lib) : JoePro.Documents.ClassLibrary.Empty();
        try { JoePro.Documents.ClassLibrary.NewClass(file, name, parent, parentLib); }
        catch (ArgumentException ex) { throw new VfpException(1, ex.Message, name); }
        JoePro.Documents.ClassFileWriter.Save(file, lib);
        InvalidateClassLibrary(lib);
        return new DesignerRequest("CLASS", lib, false, name);
    }

    /// <summary>ADD CLASS name [OF source] TO target [OVERWRITE].</summary>
    private void AddClassCommand(string rest)
    {
        var words = TokenizeDesignerArgs(rest);
        string? Clause(string kw)
        {
            var i = words.FindIndex(w => w.Equals(kw, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < words.Count ? words[i + 1] : null;
        }
        var name = words.Count > 0 ? words[0] : throw VfpException.Syntax();
        var target = WritableLibrary(Clause("TO") ?? throw VfpException.Syntax("ADD CLASS needs TO classlibrary."));
        var sourcePath = Clause("OF") is { } of ? ReadableLibrary(of) : ClassLibraryOf(name) ?? throw new VfpException(1733, $"Class definition {name.ToUpperInvariant()} is not found.", name);
        var source = JoePro.Documents.ClassLibrary.Load(sourcePath);
        var targetFile = File.Exists(target) ? JoePro.Documents.ClassLibrary.Load(target) : JoePro.Documents.ClassLibrary.Empty();
        var reference = Path.GetRelativePath(Path.GetDirectoryName(target)!, Path.ChangeExtension(sourcePath, ".jpclass")).Replace('\\', '/');
        try { JoePro.Documents.ClassLibrary.CopyClass(source, name, targetFile, reference, words.Any(w => w.Equals("OVERWRITE", StringComparison.OrdinalIgnoreCase))); }
        catch (ArgumentException ex) { throw new VfpException(1, ex.Message, name); }
        JoePro.Documents.ClassFileWriter.Save(targetFile, target);
        InvalidateClassLibrary(target);
    }

    /// <summary>RENAME CLASS old OF lib TO new.</summary>
    private void RenameClassCommand(string rest)
    {
        var words = TokenizeDesignerArgs(rest);
        string? Clause(string kw)
        {
            var i = words.FindIndex(w => w.Equals(kw, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < words.Count ? words[i + 1] : null;
        }
        if (words.Count == 0) throw VfpException.Syntax();
        var lib = WritableLibrary(Clause("OF") ?? throw VfpException.Syntax("RENAME CLASS needs OF classlibrary."));
        var file = JoePro.Documents.ClassLibrary.Load(lib);
        try { JoePro.Documents.ClassLibrary.RenameClass(file, words[0], Clause("TO") ?? throw VfpException.Syntax()); }
        catch (ArgumentException ex) { throw new VfpException(1733, ex.Message, words[0]); }
        JoePro.Documents.ClassFileWriter.Save(file, lib);
        InvalidateClassLibrary(lib);
    }

    /// <summary>REMOVE CLASS name OF lib.</summary>
    private void RemoveClassCommand(string rest)
    {
        var words = TokenizeDesignerArgs(rest);
        var of = words.FindIndex(w => w.Equals("OF", StringComparison.OrdinalIgnoreCase));
        if (words.Count == 0 || of < 0 || of + 1 >= words.Count) throw VfpException.Syntax("REMOVE CLASS needs OF classlibrary.");
        var lib = WritableLibrary(words[of + 1]);
        var file = JoePro.Documents.ClassLibrary.Load(lib);
        List<string> dependents;
        try { dependents = JoePro.Documents.ClassLibrary.RemoveClass(file, words[0]); }
        catch (ArgumentException ex) { throw new VfpException(1733, ex.Message, words[0]); }
        JoePro.Documents.ClassFileWriter.Save(file, lib);
        InvalidateClassLibrary(lib);
        if (dependents.Count > 0) Notify($"REMOVE CLASS {words[0]}: {string.Join(", ", dependents)} still refer to it.");
    }

    /// <summary>Loads a project (.jpproj, or a legacy .pjx converted in memory).</summary>
    /// <summary>Loads a project; its installed packages join SET PATH.</summary>
    public JoePro.Documents.Projects.ProjectDocument LoadProject(string path)
    {
        var project = Path.GetExtension(path).Equals(".pjx", StringComparison.OrdinalIgnoreCase)
            ? JoePro.Documents.Projects.LegacyProjectConverter.Convert(path, mapConverted: false).Document
            : JoePro.Documents.Projects.ProjectDocument.Load(path);
        PackageManager.UsePackages(this, Path.GetDirectoryName(Path.GetFullPath(path))!);
        return project;
    }

    /// <summary>The last BUILD's result (errors, output).</summary>
    public BuildResult? LastBuild { get; private set; }

    /// <summary>BUILD PROJECT name | BUILD APP name FROM project | BUILD EXE name FROM project [RECOMPILE].</summary>
    private void BuildCommand(string rest)
    {
        var words = TokenizeDesignerArgs(rest);
        if (words.Count < 2) throw VfpException.Syntax("BUILD PROJECT | APP | EXE name [FROM project]");
        var kind = words[0].ToUpperInvariant();
        string ProjectPath(string name)
        {
            var full = Path.IsPathRooted(name) ? name : Path.Combine(Options.Default_, name);
            if (Path.HasExtension(full)) return DataSession.FindIgnoringCase(full) ?? throw VfpException.FileNotFound(name);
            return DataSession.FindIgnoringCase(full + ".jpproj") ?? DataSession.FindIgnoringCase(full + ".pjx") ?? throw VfpException.FileNotFound(name + ".jpproj");
        }
        var fromAt = words.FindIndex(w => w.Equals("FROM", StringComparison.OrdinalIgnoreCase));
        if (kind == "PROJECT" && fromAt > 0)
        {
            // BUILD PROJECT name FROM file1, file2…: creates (or adds to) the project; the first file is the main one.
            var projFile = Path.Combine(Options.Default_, Path.HasExtension(words[1]) ? words[1] : words[1] + ".jpproj");
            var doc = File.Exists(projFile) ? JoePro.Documents.Projects.ProjectDocument.Load(projFile) : new JoePro.Documents.Projects.ProjectDocument { Name = Path.GetFileNameWithoutExtension(projFile) };
            foreach (var w in words.Skip(fromAt + 1).SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                var rel = Path.HasExtension(w) ? w : w + ".prg";
                if (doc.Find(rel) == null) doc.Add(rel);
                doc.Main ??= JoePro.Documents.Projects.ProjectDocument.Normalize(rel);
            }
            doc.Save(projFile);
            words = words.Take(fromAt).ToList();
        }
        var projectPath = ProjectPath(kind == "PROJECT" ? words[1] : fromAt > 0 && fromAt + 1 < words.Count ? words[fromAt + 1] : throw VfpException.Syntax($"BUILD {kind} name FROM project"));
        var project = LoadProject(projectPath);
        var dir = Path.GetDirectoryName(projectPath)!;
        var target = Path.IsPathRooted(words[1]) ? words[1] : Path.Combine(Options.Default_, words[1]);
        BuildResult result = kind switch
        {
            "PROJECT" => ProjectBuilder.Check(project, dir),
            "APP" or "DLL" or "MTDLL" => ProjectBuilder.BuildApp(project, dir, Path.HasExtension(target) && !target.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? target : Path.ChangeExtension(target, ".jpapp")),
            "EXE" => ProjectBuilder.BuildExe(project, dir, Path.HasExtension(target) ? Path.ChangeExtension(target, null) : target),
            _ => throw VfpException.Syntax($"BUILD {kind} is not supported."),
        };
        LastBuild = result;
        if (result.Succeeded)
        {
            var warnings = result.Warnings.Count == 0 ? "" : $" {result.Warnings.Count} warning(s): " + string.Join("; ", result.Warnings.Take(3).Select(w => $"{w.File}: {w.Message}"));
            Notify((kind == "PROJECT" ? $"Project {project.Name} built: {result.Files.Count} file(s), no errors." : $"Built {result.Output} ({result.Files.Count} file(s)).") + warnings);
        }
        else
        {
            Notify($"Build of {project.Name} failed with {result.Errors.Count} error(s): " + string.Join("; ", result.Errors.Take(3).Select(e => $"{e.File}{(e.Line > 0 ? $"({e.Line})" : "")}: {e.Message}")));
            if (kind != "PROJECT") throw new VfpException(1, $"Build failed with {result.Errors.Count} error(s); see {Path.ChangeExtension(target, ".err")}.");
        }
    }

    /// <summary>CREATE REPORT file FROM table [FORM | COLUMN] [FIELDS list] [NOOVERWRITE]: writes a quick report without a designer.</summary>
    private void QuickReportCommand(string rest)
    {
        var words = TokenizeDesignerArgs(rest);
        var fromAt = words.FindIndex(w => w.Equals("FROM", StringComparison.OrdinalIgnoreCase));
        if (words.Count == 0 || fromAt <= 0 || fromAt + 1 >= words.Count) throw VfpException.Syntax("CREATE REPORT file FROM table");
        var file = words[0];
        var full = Path.IsPathRooted(file) ? file : Path.Combine(Options.Default_, file);
        if (!Path.HasExtension(full)) full += ".jpreport";
        if (File.Exists(full) && words.Any(w => w.Equals("NOOVERWRITE", StringComparison.OrdinalIgnoreCase))) return;
        var table = words[fromAt + 1];
        var wa = Session.FindAlias(Path.GetFileNameWithoutExtension(table));
        var opened = false;
        if (wa == null)
        {
            ExecuteCommand($"USE \"{table}\" IN 0 AGAIN NOUPDATE");
            wa = Session.FindAlias(Path.GetFileNameWithoutExtension(table)) ?? throw VfpException.FileNotFound(table);
            opened = true;
        }
        try
        {
            var fieldsAt = words.FindIndex(w => w.Equals("FIELDS", StringComparison.OrdinalIgnoreCase));
            var wanted = fieldsAt >= 0 ? string.Join(" ", words.Skip(fieldsAt + 1).TakeWhile(w => !IsQuickReportClause(w))).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null;
            var fields = wa.Table.Fields
                .Where(f => wanted == null || wanted.Any(w => w.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(f => (f.Name, f.Type, f.Width)).ToList();
            var columnar = !words.Any(w => w.Equals("FORM", StringComparison.OrdinalIgnoreCase));
            var doc = JoePro.Documents.Reports.ReportDesignSession.QuickReport(wa.Alias.ToLowerInvariant(), fields, columnar);
            doc.Save(full);
        }
        finally { if (opened) wa.Close(); }
    }

    private static bool IsQuickReportClause(string w) => w.ToUpperInvariant() is "FORM" or "COLUMN" or "ALIAS" or "NOOVERWRITE" or "WIDTH";

    private static bool IsDesignerClause(string word) =>
        word.ToUpperInvariant() is "OF" or "AS" or "FROM" or "NOWAIT" or "SAVE" or "WINDOW" or "IN" or "METHOD" or "NOENVIRONMENT";

    /// <summary>Splits designer command arguments on blanks, keeping quoted names and parenthesized expressions whole.</summary>
    private static List<string> TokenizeDesignerArgs(string text)
    {
        var words = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var start = i;
            if (text[i] is '"' or '\'' or '[')
            {
                var close = text[i] == '[' ? ']' : text[i];
                var end = text.IndexOf(close, i + 1);
                if (end < 0) end = text.Length - 1;
                words.Add(text[(i + 1)..end]);
                i = end + 1;
                continue;
            }
            if (text[i] == '(')
            {
                var depth = 0;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '(') depth++;
                    else if (text[i] == ')' && --depth == 0) { i++; break; }
                }
                words.Add(text[start..i]);
                continue;
            }
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            words.Add(text[start..i]);
        }
        return words;
    }

    // ---- CREATE / ALTER TABLE ---------------------------------------------------------------

    internal static FieldDef ToFieldDef(FieldSpec f)
    {
        var width = f.Width;
        if (width == 0 && f.Type is 'C' or 'V' or 'Q') width = 1;
        if (width == 0 && f.Type is 'N' or 'F') width = 10;
        return new FieldDef(f.Name, f.Type, width, f.Decimals)
        {
            Nullable = f.Null && !f.NotNull,
            DefaultExpr = f.Default != null ? ExprPrinter.Print(f.Default) : null,
            RuleExpr = f.Check != null ? ExprPrinter.Print(f.Check) : null,
            RuleText = f.Error,
            AutoIncNext = f.AutoInc ? f.AutoIncNext : null,
            AutoIncStep = f.AutoInc ? f.AutoIncStep : 1,
        };
    }

    private FieldSpec ResolveFieldName(FieldSpec f) => f.NameExpr == null ? f : f with { Name = Eval(f.NameExpr).AsString.Trim(), NameExpr = null };

    private void ExecCreateTable(CreateTableStmt ct)
    {
        if (ct.Fields.Any(f => f.NameExpr != null)) ct = ct with { Fields = ct.Fields.Select(ResolveFieldName).ToList() };
        var name = NameValue(ct.Name);
        List<FieldDef> fields;
        var tags = new List<TagDef>();
        if (ct.FromArray != null)
        {
            var arr = FindVariable(ct.FromArray)?.Array ?? throw VfpException.VariableNotFound(ct.FromArray);
            fields = [];
            for (int r = 1; r <= arr.Rows; r++)
                fields.Add(new FieldDef(arr[r, 1].AsString.Trim(), arr[r, 2].AsString.Trim()[0], (int)arr[r, 3].AsNumber, (int)arr[r, 4].AsNumber)
                    { Nullable = arr.Cols >= 5 && arr[r, 5].Kind == ValueKind.Logical && arr[r, 5].AsBool });
        }
        else
        {
            fields = ct.Fields.Where(f => !f.Name.StartsWith("__CONSTRAINT:")).Select(ToFieldDef).ToList();
            var inDb = Session.CurrentDatabase != null && !ct.Free && !ct.Cursor;
            foreach (var f in ct.Fields.Where(f => !f.Name.StartsWith("__CONSTRAINT:")))
            {
                if (f.PrimaryKey) tags.Add(new TagDef(f.Name, f.Name, Kind: inDb ? TagKind.Primary : TagKind.Candidate));
                else if (f.Unique) tags.Add(new TagDef(f.Name, f.Name, Kind: TagKind.Candidate));
            }
            foreach (var c in ct.Fields.Where(f => f.Name.StartsWith("__CONSTRAINT:")))
            {
                var text = c.Name["__CONSTRAINT:".Length..];
                var m = System.Text.RegularExpressions.Regex.Match(text, @"^(PRIMARY\s+KEY|UNIQUE)\s+(.+?)\s+TAG\s+(\w+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                    tags.Add(new TagDef(m.Groups[3].Value, m.Groups[2].Value,
                        Kind: m.Groups[1].Value.StartsWith("P", StringComparison.OrdinalIgnoreCase) && inDb ? TagKind.Primary : TagKind.Candidate));
                else Notify($"Table constraint not applied: {text}");
            }
        }
        var schema = new TableSchema(Path.GetFileNameWithoutExtension(name), fields);
        schema.Tags.AddRange(tags);
        if (ct.Cursor)
        {
            Session.CreateCursor(schema, Session.Current.InUse ? Session.FreeArea() : Session.CurrentAreaNumber);
            return;
        }
        var free = ct.Free || Session.CurrentDatabase == null;
        string? path = free ? Session.ResolvePath(name, Store.FreeTableExtension) : null;
        if (path != null && File.Exists(path) && !Options.Safety) { }
        Session.CreateTable(schema, free, path);
        var area = Session.Current.InUse ? Session.FreeArea() : Session.CurrentAreaNumber;
        Session.Use(free ? path! : schema.Name, area, schema.Name, exclusive: true);
    }

    private void ExecAlterTable(AlterTableStmt at)
    {
        if (at.Field?.NameExpr != null) at = at with { Field = ResolveFieldName(at.Field) };
        if (at.DropExpr != null) at = at with { DropField = Eval(at.DropExpr).AsString.Trim(), DropExpr = null };
        if (at.RenameFromExpr != null) at = at with { RenameFrom = Eval(at.RenameFromExpr).AsString.Trim(), RenameFromExpr = null };
        if (at.RenameToExpr != null) at = at with { RenameTo = Eval(at.RenameToExpr).AsString.Trim(), RenameToExpr = null };
        var name = NameValue(at.Name);
        var wa = Session.FindAlias(Path.GetFileNameWithoutExtension(name)) ?? Session.Use(name, Session.FreeArea());
        var table = wa.Table;
        var fields = table.Fields.ToList();
        switch (at.Action)
        {
            case "ADD":
                if (fields.Any(f => f.Name.Equals(at.Field!.Name, StringComparison.OrdinalIgnoreCase))) throw new VfpException(ErrorCodes.SyntaxError, $"Field {at.Field!.Name.ToUpperInvariant()} already exists.");
                fields.Add(ToFieldDef(at.Field!));
                break;
            case "DROP":
                if (fields.RemoveAll(f => f.Name.Equals(at.DropField, StringComparison.OrdinalIgnoreCase)) == 0) throw VfpException.FieldNotFound(at.DropField!);
                break;
            case "ALTER":
            {
                var i = fields.FindIndex(f => f.Name.Equals(at.Field!.Name, StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw VfpException.FieldNotFound(at.Field!.Name);
                // A new definition keeps the field's default, rule, caption and other properties unless it gives them.
                var def = ToFieldDef(at.Field!);
                var old = fields[i];
                fields[i] = def with
                {
                    DefaultExpr = def.DefaultExpr ?? old.DefaultExpr, RuleExpr = def.RuleExpr ?? old.RuleExpr, RuleText = def.RuleExpr != null ? def.RuleText : old.RuleText,
                    Caption = old.Caption, Comment = old.Comment, Format = old.Format, InputMask = old.InputMask,
                    DisplayClass = old.DisplayClass, DisplayClassLibrary = old.DisplayClassLibrary,
                    AutoIncNext = def.AutoIncNext ?? (at.Field!.Type == old.Type ? old.AutoIncNext : null),
                    AutoIncStep = def.AutoIncNext != null ? def.AutoIncStep : old.AutoIncStep,
                };
                break;
            }
            case "RENAME":
            {
                var i = fields.FindIndex(f => f.Name.Equals(at.RenameFrom, StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw VfpException.FieldNotFound(at.RenameFrom!);
                if (fields.Any(f => f.Name.Equals(at.RenameTo, StringComparison.OrdinalIgnoreCase))) throw new VfpException(ErrorCodes.SyntaxError, $"Field {at.RenameTo!.ToUpperInvariant()} already exists.");
                fields[i] = fields[i] with { Name = at.RenameTo! };
                break;
            }
        }
        // Rebuild: copy rows into a new table with the new structure (same approach as VFP's temporary copy).
        var schema = CopySchemaWith(table.Schema, fields);
        var dropped = at.Action == "DROP" ? at.DropField! : null;
        schema.Tags.AddRange(table.Schema.Tags
            .Where(t => dropped == null || !System.Text.RegularExpressions.Regex.IsMatch(t.Expression + " " + t.ForExpression, $@"\b{System.Text.RegularExpressions.Regex.Escape(dropped)}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(t => at.Action == "RENAME" ? t with { Expression = RenameInExpression(t.Expression, at.RenameFrom!, at.RenameTo!), ForExpression = t.ForExpression == null ? null : RenameInExpression(t.ForExpression, at.RenameFrom!, at.RenameTo!) } : t));
        RebuildTable(table, schema, f => at.Action == "RENAME" && f.Name.Equals(at.RenameTo, StringComparison.OrdinalIgnoreCase) ? at.RenameFrom : f.Name);
    }

    private static TableSchema CopySchemaWith(TableSchema s, List<FieldDef> fields) => new(s.Name, fields)
    {
        Comment = s.Comment, RuleExpr = s.RuleExpr, RuleText = s.RuleText,
        InsertTrigger = s.InsertTrigger, UpdateTrigger = s.UpdateTrigger, DeleteTrigger = s.DeleteTrigger,
    };

    private static string RenameInExpression(string expr, string from, string to) =>
        System.Text.RegularExpressions.Regex.Replace(expr, $@"(?<![\w.]){System.Text.RegularExpressions.Regex.Escape(from)}\b", to.ToUpperInvariant(), System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ---- LIST / DISPLAY ------------------------------------------------------------------

    private void ExecList(ListStmt ls)
    {
        var wa = AreaOf(ls.Scope.In);
        if (!wa.InUse) throw VfpException.NoTableOpen();
        if (ls.Structure)
        {
            ListStructure(wa);
            return;
        }
        var cols = ls.Fields != null
            ? ls.Fields.Select(f => (Header: ExprPrinter.Print(f).ToUpperInvariant(), Expr: f)).ToList()
            : wa.Table.Fields.Select(f => (Header: f.Name, Expr: (Expr)new MemberExpr(new NameExpr(wa.Alias), f.Name))).ToList();
        var fieldWidths = ls.Fields == null ? wa.Table.Fields.Select(f => f.Type switch { 'M' or 'G' or 'W' => 4, 'D' => Options.Century ? 10 : 8, 'T' => 22, 'L' => 3, 'Y' => 12, 'I' => 11, 'B' => 20, _ => f.Width }).ToList() : null;
        var rows = new List<(int RecNo, bool Deleted, List<string> Cells)>();
        var saved = Session.CurrentAreaNumber;
        ForEachInScope(wa, ls.Scope, ls.Display ? "NEXT1" : "ALL", () =>
        {
            Session.Select(wa.Number);
            var cells = new List<string>();
            for (int i = 0; i < cols.Count; i++)
            {
                if (ls.Fields == null && wa.Table.Fields[i].Type is 'M' or 'G' or 'W')
                {
                    var mv = wa.Get(i);
                    cells.Add(mv.IsEmpty ? "memo" : "Memo");
                    continue;
                }
                cells.Add(Formatter.ToDisplay(Eval(cols[i].Expr), Options).Trim(ls.Fields == null && wa.Table.Fields[i].Type is 'N' or 'F' or 'I' or 'B' or 'Y' ? ' ' : '\0'));
            }
            rows.Add((wa.RecNo, wa.Deleted, cells));
        });
        Session.Select(saved);
        var widths = cols.Select((c, i) => Math.Max(c.Header.Length, Math.Max(fieldWidths?[i] ?? 0, rows.Count == 0 ? 0 : rows.Max(r => r.Cells[i].Length)))).ToList();
        var numeric = ls.Fields == null ? wa.Table.Fields.Select(f => f.Type is 'N' or 'F' or 'I' or 'B' or 'Y').ToList() : cols.Select(_ => false).ToList();
        string Row(IEnumerable<string> cells) => string.Join(" ", cells.Select((c, i) => numeric[i] ? c.PadLeft(widths[i]) : c.PadRight(widths[i]))).TrimEnd();
        if (Options.IsOn("HEADINGS", true)) WriteLine((ls.Off ? "" : "Record# ") + Row(cols.Select(c => c.Header)));
        foreach (var r in rows)
            WriteLine((ls.Off ? "" : r.RecNo.ToString().PadLeft(7) + (r.Deleted ? "*" : " ")) + Row(r.Cells));
    }

    private void ListStructure(WorkArea wa)
    {
        var t = wa.Table;
        WriteLine($"Structure for table:    {wa.Source}");
        WriteLine($"Number of data records: {t.RecordCount}");
        WriteLine($"Code Page:              Unicode (UTF-16)");
        WriteLine("Field  Field Name      Type                Width    Dec   Index   Collate Nulls");
        int total = 1;
        for (int i = 0; i < t.Fields.Count; i++)
        {
            var f = t.Fields[i];
            var typeName = f.Type switch
            {
                'C' => "Character", 'V' => "Varchar", 'M' => "Memo", 'N' => "Numeric", 'F' => "Float", 'B' => "Double",
                'I' => "Integer", 'Y' => "Currency", 'D' => "Date", 'T' => "DateTime", 'L' => "Logical", 'G' => "General",
                'W' => "Blob", 'Q' => "Varbinary", _ => f.Type.ToString(),
            };
            var tag = t.Schema.Tags.FirstOrDefault(x => x.Expression.Trim().Equals(f.Name, StringComparison.OrdinalIgnoreCase));
            var idx = tag == null ? "" : tag.Descending ? "Desc" : "Asc";
            WriteLine($"{i + 1,5}  {f.Name,-15} {typeName,-19} {f.Width,5} {(f.Type is 'N' or 'F' or 'B' ? f.Decimals.ToString() : ""),6}   {idx,-7} {(tag != null ? tag.Collation : ""),-7} {(f.Nullable ? "Yes" : "No")}");
            total += f.Width;
        }
        WriteLine($"** Total **{total,30}");
    }

    // ---- COUNT / SUM / AVERAGE / CALCULATE -------------------------------------------------

    private void ExecAggregate(AggregateStmt ag)
    {
        var wa = AreaOf(ag.Scope.In);
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var saved = Session.CurrentAreaNumber;
        Session.Select(wa.Number);
        try
        {
            if (ag.Kind == "COUNT")
            {
                var n = ForEachInScope(wa, ag.Scope, "ALL", () => { });
                if (ag.To.Count > 0) Assign(ag.To[0], Value.Number(n, 0));
                SetPublic("_TALLY", Value.Number(n, 0));
                Talk($"{n} records");
                return;
            }
            if (ag.Kind == "CALCULATE")
            {
                var aggs = ag.Exprs.Cast<CallExpr>().ToList();
                var acc = aggs.Select(_ => new Builtins.AggregateAccumulator()).ToList();
                ForEachInScope(wa, ag.Scope, "ALL", () =>
                {
                    for (int i = 0; i < aggs.Count; i++)
                        acc[i].Add(aggs[i].Args.Count == 0 ? Value.Number(1) : Eval(aggs[i].Args[0]), this);
                });
                for (int i = 0; i < aggs.Count; i++)
                {
                    var v = acc[i].Result(aggs[i].Name.ToUpperInvariant(), Options);
                    if (i < ag.To.Count) Assign(ag.To[i], v);
                }
                return;
            }
            var exprs = ag.Exprs.Count > 0 ? ag.Exprs : wa.Table.Fields.Select((f, i) => (f, i)).Where(x => x.f.Type is 'N' or 'F' or 'B' or 'I' or 'Y').Select(x => (Expr)new NameExpr(x.f.Name)).ToList();
            var sums = new double[exprs.Count];
            var decs = new int[exprs.Count];
            var money = new decimal[exprs.Count];
            var isMoney = Enumerable.Repeat(true, exprs.Count).ToArray();
            int count = ForEachInScope(wa, ag.Scope, "ALL", () =>
            {
                for (int i = 0; i < exprs.Count; i++)
                {
                    var v = Eval(exprs[i]);
                    if (v.IsNull) continue;
                    sums[i] += v.AsNumber;
                    decs[i] = Math.Max(decs[i], v.Decimals);
                    if (v.Kind == ValueKind.Currency) money[i] += v.AsCurrency; else isMoney[i] = false;
                }
            });
            var results = sums.Select((s, i) => isMoney[i] && count > 0
                ? Value.Currency(ag.Kind == "AVERAGE" ? Math.Round(money[i] / count, 4, MidpointRounding.AwayFromZero) : money[i])
                : ag.Kind == "AVERAGE" ? Value.Number(count == 0 ? 0 : s / count, Math.Max(decs[i], Options.Decimals)) : Value.Number(s, decs[i])).ToList();
            if (ag.ToArray != null)
            {
                var v = Declare("DIMENSION", ag.ToArray);
                v.Array = new VfpArray(results.Count);
                for (int i = 0; i < results.Count; i++) v.Array[i + 1] = results[i];
            }
            for (int i = 0; i < ag.To.Count && i < results.Count; i++) Assign(ag.To[i], results[i]);
            if (ag.To.Count == 0 && ag.ToArray == null)
                WriteLine(string.Join(" ", results.Select(r => Formatter.ToDisplay(r, Options))));
        }
        finally
        {
            Session.Select(saved);
        }
    }

    // ---- SCATTER / GATHER ------------------------------------------------------------------

    private IEnumerable<int> FieldIndexes(WorkArea wa, List<string>? names, bool memo)
    {
        var fields = wa.Table.Fields;
        if (names != null)
        {
            foreach (var n in names)
            {
                var i = wa.FieldIndex(n);
                if (i < 0) throw VfpException.FieldNotFound(n);
                yield return i;
            }
            yield break;
        }
        for (int i = 0; i < fields.Count; i++)
            if (memo || fields[i].Type is not ('M' or 'G' or 'W')) yield return i;
    }

    private void ExecScatter(ScatterStmt sc)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var idx = FieldIndexes(wa, sc.Fields, sc.Memo || sc.Name != null).ToList();
        Value V(int i) => sc.Blank ? wa.Table.Fields[i].BlankValue() : wa.Get(i);
        if (sc.ToArray != null)
        {
            var v = Declare("DIMENSION", sc.ToArray);
            v.Array = new VfpArray(idx.Count);
            for (int k = 0; k < idx.Count; k++) v.Array[k + 1] = V(idx[k]);
            return;
        }
        if (sc.Name != null)
        {
            VfpObject o;
            if (sc.Additive && FindVariable(sc.Name)?.Value is { Kind: ValueKind.Object } existing) o = (VfpObject)existing.AsObject;
            else o = CreateObjectByName("Empty", []);
            foreach (var i in idx) o.Set(wa.Table.Fields[i].Name, V(i));
            SetVariable(sc.Name, Value.Object(o));
            return;
        }
        foreach (var i in idx) SetVariable(wa.Table.Fields[i].Name, V(i));
    }

    private void ExecGather(GatherStmt ga)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var idx = FieldIndexes(wa, ga.Fields, ga.Memo || ga.Name != null).ToList();
        var assignments = new List<(int, Value)>();
        if (ga.FromArray != null)
        {
            var arr = FindVariable(ga.FromArray)?.Array ?? throw VfpException.VariableNotFound(ga.FromArray);
            for (int k = 0; k < idx.Count && k < arr.Length; k++) assignments.Add((idx[k], arr[k + 1]));
        }
        else if (ga.Name != null)
        {
            var o = GetVariable(ga.Name);
            if (o.Kind != ValueKind.Object) throw VfpException.TypeMismatch();
            var obj = (VfpObject)o.AsObject;
            foreach (var i in idx)
                if (obj.FindProperty(wa.Table.Fields[i].Name) is { } p) assignments.Add((i, p.Value));
        }
        else
        {
            foreach (var i in idx)
                if (FindVariable(wa.Table.Fields[i].Name) is { IsArray: false } v) assignments.Add((i, v.Value));
        }
        if (assignments.Count > 0) ReplaceWithRules(wa, assignments);
    }

    // ---- COPY TO / IMPORT ------------------------------------------------------------------

    private void ExecCopyTo(CopyToStmt cp)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
        var idx = FieldIndexes(wa, cp.Fields, memo: true).ToList();
        var fields = idx.Select(i => wa.Table.Fields[i]).ToList();
        if (cp.Array)
        {
            var name = NameValue(cp.Target);
            var rows = new List<Value[]>();
            ForEachInScope(wa, cp.Scope, "ALL", () => rows.Add(idx.Where(i => wa.Table.Fields[i].Type is not ('M' or 'G' or 'W')).Select(i => wa.Get(i)).ToArray()));
            if (rows.Count == 0) return;
            var v = Declare("DIMENSION", name);
            v.Array = new VfpArray(rows.Count, rows[0].Length);
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Length; c++) v.Array[r + 1, c + 1] = rows[r][c];
            return;
        }
        var target = NameValue(cp.Target);
        var type = cp.Type;
        if (cp.Extended)
        {
            // COPY STRUCTURE EXTENDED: one record per field, with VFP's column layout.
            var extFields = new List<FieldDef>
            {
                new("FIELD_NAME", 'C', 128), new("FIELD_TYPE", 'C', 1), new("FIELD_LEN", 'N', 3), new("FIELD_DEC", 'N', 3),
                new("FIELD_NULL", 'L'), new("FIELD_NOCP", 'L'), new("FIELD_DEFA", 'M'), new("FIELD_RULE", 'M'), new("FIELD_ERR", 'M'),
                new("TABLE_RULE", 'M'), new("TABLE_ERR", 'M'), new("TABLE_NAME", 'C', 128), new("INS_TRIG", 'M'), new("UPD_TRIG", 'M'),
                new("DEL_TRIG", 'M'), new("TABLE_CMT", 'M'), new("FIELD_NEXT", 'N', 10), new("FIELD_STEP", 'N', 10),
            };
            var sch = wa.Table.Schema;
            var extRows = sch.Fields.Select((f, i) => new[]
            {
                Value.String(f.Name), Value.String(f.Type.ToString()), Value.Number(f.Width), Value.Number(f.Decimals),
                Value.Logical(f.Nullable), Value.Logical(f.Binary), Value.String(f.DefaultExpr ?? ""), Value.String(f.RuleExpr ?? ""),
                Value.String(f.RuleText ?? ""), Value.String(i == 0 ? sch.RuleExpr ?? "" : ""), Value.String(i == 0 ? sch.RuleText ?? "" : ""),
                Value.String(i == 0 && sch.Name.Length > 0 ? sch.Name : ""), Value.String(i == 0 ? sch.InsertTrigger ?? "" : ""),
                Value.String(i == 0 ? sch.UpdateTrigger ?? "" : ""), Value.String(i == 0 ? sch.DeleteTrigger ?? "" : ""),
                Value.String(i == 0 ? sch.Comment ?? "" : ""), Value.Number(f.AutoIncNext ?? 0), Value.Number(f.AutoIncNext != null ? f.AutoIncStep : 0),
            }).ToList();
            var extPath = Session.ResolvePath(target, Store.FreeTableExtension);
            if (File.Exists(extPath)) File.Delete(extPath);
            using var extSession = new DataSession(Options.Clone(), this);
            var extTable = extSession.CreateTable(new TableSchema(Path.GetFileNameWithoutExtension(extPath), extFields), free: true, extPath);
            extTable.Store.Batch(() => { foreach (var r in extRows) extTable.Append(r); });
            return;
        }
        var ext = Path.GetExtension(target).ToLowerInvariant();
        var records = new List<(bool, Value[])>();
        if (!cp.Structure)
            ForEachInScope(wa, cp.Scope, "ALL", () => records.Add((wa.Deleted, idx.Select(i => wa.Get(i)).ToArray())));
        if (type is "CSV" or "DELIMITED" or "SDF")
        {
            var path = Path.Combine(Options.Default_, Path.HasExtension(target) ? target : target + (type == "SDF" ? ".txt" : ".csv"));
            using var w = new StreamWriter(path, false, new UTF8Encoding(false));
            if (type == "CSV") w.WriteLine(string.Join(",", fields.Select(f => f.Name.ToLowerInvariant())));
            foreach (var (_, vals) in records)
            {
                if (type == "SDF")
                    w.WriteLine(string.Concat(vals.Select((v, i) => CsvText(v).PadRight(fields[i].Width)[..Math.Max(fields[i].Width, 1)])));
                else
                    w.WriteLine(string.Join(",", vals.Select((v, i) => fields[i].Type is 'C' or 'V' or 'M' ? "\"" + CsvText(v).TrimEnd().Replace("\"", "\"\"") + "\"" : CsvText(v))));
            }
            Talk($"{records.Count} records copied.");
            return;
        }
        if (type is "XLS" or "XL5" or "XL8")
        {
            // Excel opens SpreadsheetML (the Excel 2003 XML format) saved with an .xls extension.
            var path = Path.Combine(Options.Default_, Path.HasExtension(target) ? target : target + ".xls");
            WriteSpreadsheet(path, fields, records.Select(r => r.Item2));
            Talk($"{records.Count} records copied.");
            return;
        }
        if (type is "DIF" or "MOD" or "SYLK" or "WK1" or "WKS" or "WR1" or "WRK")
            throw VfpException.NotSupported($"COPY TO/EXPORT TYPE {type} (use XLS, CSV, SDF or DELIMITED)");
        if (ext == ".dbf" || type is "FOXPLUS" or "FOX2X")
        {
            var path = Path.Combine(Options.Default_, Path.HasExtension(target) ? target : target + ".dbf");
            DbfWriter.Write(path, fields.Select(f => f with { AutoIncNext = null }).ToList(), records.Select(r => (r.Item1, r.Item2)));
            Talk($"{records.Count} records copied to FoxPro table {Path.GetFileName(path)}.");
            return;
        }
        var jpt = Session.ResolvePath(target, Store.FreeTableExtension);
        var schema = new TableSchema(Path.GetFileNameWithoutExtension(jpt), fields.Select(f => f with { AutoIncNext = null }));
        using var other = new DataSession(Options.Clone(), this);
        var t = other.CreateTable(schema, free: true, path: jpt);
        foreach (var (del, vals) in records) t.Append(vals, del);
        Talk($"{records.Count} records copied.");
    }

    private static void WriteSpreadsheet(string path, IReadOnlyList<FieldDef> fields, IEnumerable<Value[]> rows)
    {
        XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";
        XElement Cell(Value v)
        {
            var (type, text) = v.Kind switch
            {
                ValueKind.Number or ValueKind.Currency => ("Number", v.AsNumber.ToString("R", CultureInfo.InvariantCulture)),
                ValueKind.Logical => ("Boolean", v.AsBool ? "1" : "0"),
                ValueKind.Date when !v.IsEmptyDate => ("DateTime", Julian.ToDate(v.JulianDay).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00.000"),
                ValueKind.DateTime when !v.IsEmptyDate => ("DateTime", Julian.ToDateTime(v.JulianMs).ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)),
                ValueKind.Character => ("String", v.AsString.TrimEnd()),
                _ => ("String", ""),
            };
            var cell = new XElement(ss + "Cell", new XElement(ss + "Data", new XAttribute(ss + "Type", type), text));
            if (type == "DateTime") cell.Add(new XAttribute(ss + "StyleID", "d"));
            return cell;
        }
        var table = new XElement(ss + "Table", new XElement(ss + "Row", fields.Select(f => new XElement(ss + "Cell", new XElement(ss + "Data", new XAttribute(ss + "Type", "String"), f.Name.ToLowerInvariant())))));
        foreach (var r in rows) table.Add(new XElement(ss + "Row", r.Select(Cell)));
        var doc = new XDocument(new XProcessingInstruction("mso-application", "progid=\"Excel.Sheet\""),
            new XElement(ss + "Workbook", new XAttribute(XNamespace.Xmlns + "ss", ss.NamespaceName),
                new XElement(ss + "Styles", new XElement(ss + "Style", new XAttribute(ss + "ID", "d"), new XElement(ss + "NumberFormat", new XAttribute(ss + "Format", "Short Date")))),
                new XElement(ss + "Worksheet", new XAttribute(ss + "Name", Path.GetFileNameWithoutExtension(path)), table)));
        doc.Save(path);
    }

    private string CsvText(Value v) => v.Kind switch
    {
        ValueKind.Date => v.IsEmptyDate ? "" : Julian.ToDate(v.JulianDay).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        ValueKind.Logical => v.AsBool ? "T" : "F",
        ValueKind.Null => "",
        ValueKind.Number => Formatter.FormatNumber(v.AsNumber, v.Decimals, Options),
        _ => Formatter.ToDisplay(v, Options),
    };

    /// <summary>Hook for IMPORT; the migration layer registers the real implementation.</summary>
    public static Action<Interpreter, string, string?, bool>? ImportHandler { get; set; }

    private void ExecImport(ImportStmt im)
    {
        if (ImportHandler == null) throw VfpException.NotSupported("IMPORT (migration module not loaded)");
        ImportHandler(this, NameValue(im.Source), im.To != null ? NameValue(im.To) : null, im.Database);
    }
}
