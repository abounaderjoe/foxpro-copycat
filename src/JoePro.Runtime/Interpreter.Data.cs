using System.Globalization;
using System.Text;
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
                var kind = ix.Kind switch { "UNIQUE" => TagKind.Unique, "CANDIDATE" => TagKind.Candidate, _ => TagKind.Regular };
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
            case BrowseStmt br:
                ExecList(new ListStmt(false, br.Fields, br.Scope.Kind == "DEFAULT" ? br.Scope with { Kind = "ALL" } : br.Scope, false, false));
                return true;
            case CopyToStmt cp: ExecCopyTo(cp); return true;
            case ImportStmt im: ExecImport(im); return true;
            case SqlSelectStmt sq:
                SqlEngine.Execute(this, sq.Query, materialize: true);
                return true;
            case SqlInsertStmt si: SqlEngine.Insert(this, si); return true;
            case SqlUpdateStmt su: SqlEngine.Update(this, su); return true;
            case SqlDeleteStmt sdel: SqlEngine.Delete(this, sdel); return true;
        }
        return false;
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

    // ---- Scope iteration -----------------------------------------------------------------

    /// <summary>Visits records in a command scope, positioning the work area on each record that passes FOR/WHILE.</summary>
    internal int ForEachInScope(WorkArea wa, Scope scope, string defaultKind, Action action, bool snapshot = false)
    {
        var kind = scope.Kind == "DEFAULT" ? (scope.For != null || scope.While != null ? (defaultKind == "NEXT1" ? "ALL" : defaultKind) : defaultKind) : scope.Kind;
        if (scope.Kind == "DEFAULT" && scope.While != null && defaultKind != "ALL") kind = "REST";
        int count = 0;
        bool Passes() => scope.For == null || Truthy(Eval(scope.For));
        bool While() => scope.While == null || Truthy(Eval(scope.While));
        var saved = Session.CurrentAreaNumber;
        void Use() => Session.Select(wa.Number);

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
            Use();
            if (!While()) break;
            if (Passes()) { action(); count++; }
            seen++;
            if (seen >= limit) break;
            wa.Skip();
        }
        _ = saved;
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
        wa.Replace(assignments);
        try
        {
            foreach (var (idx, _) in assignments) CheckFieldRule(wa, idx);
            CheckRecordRule(wa);
            RunTrigger(wa, wa.Table.Schema.UpdateTrigger, "Update");
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

    internal Value EvalInArea(WorkArea wa, string expr)
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
            wa.Delete(!d.Recall);
            if (!d.Recall)
            {
                try { RunTrigger(wa, wa.Table.Schema.DeleteTrigger, "Delete"); }
                catch { wa.Delete(false); throw; }
            }
        }, snapshot: true);
        Talk($"{count} record{(count == 1 ? "" : "s")} {(d.Recall ? "recalled" : "deleted")}.");
    }

    private void ExecAppendFrom(AppendFromStmt af)
    {
        var wa = Session.Current;
        if (!wa.InUse) throw VfpException.NoTableOpen();
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
        switch (st.Option)
        {
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
            case "MULTILOCKS" or "REPROCESS" or "STATUS" or "ECHO" or "STEP" or "CONSOLE" or "ESCAPE" or "BELL" or "NOTIFY"
                or "CPDIALOG" or "STRICTDATE" or "FIXED" or "UDFPARMS" or "COMPATIBLE" or "MEMOWIDTH" or "TEXTMERGE" or "HELP"
                or "RESOURCE" or "SYSMENU" or "CURSOR" or "TYPEAHEAD" or "CARRY" or "CONFIRM" or "FULLPATH" or "UNIQUE" or "LOCK"
                or "REFRESH" or "CURRENCY" or "CLOCK" or "ROLLOVER" or "BLOCKSIZE" or "NULLDISPLAY" or "VARCHARMAPPING"
                or "TABLEVALIDATE" or "LIBRARY" or "CLASSLIB" or "DATASESSION" or "COVERAGE" or "EVENTTRACKING" or "ASSERTS"
                or "AUTOINCERROR" or "INDEX" or "KEY" or "SKIP" or "DEBUG" or "ALTERNATE" or "PRINTER" or "DEVICE" or "LOGERRORS"
                or "MESSAGE" or "FDOW" or "FWEEK" or "SYSFORMATS" or "NOCPTRANS" or "OLEOBJECT" or "SQLBUFFERING" or "":
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
            {
                var p = Session.ResolvePath(rest.Trim('"', '\''), "");
                if (File.Exists(p)) File.Delete(p);
                break;
            }
            case "RENAME":
            {
                var parts = rest.Split(" TO ", 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2) throw VfpException.Syntax();
                File.Move(Session.ResolvePath(parts[0].Trim('"', '\''), ""), Path.Combine(Options.Default_, parts[1].Trim('"', '\'')));
                break;
            }
            case "MD" or "MKDIR":
                Directory.CreateDirectory(Path.Combine(Options.Default_, rest.Trim('"', '\'')));
                break;
            case "RD" or "RMDIR":
                Directory.Delete(Path.Combine(Options.Default_, rest.Trim('"', '\'')));
                break;
            case "FLUSH" or "UNLOCK" or "DOEVENTS" or "EXTERNAL" or "SLEEP" or "LOCK" or "VALIDATE" or "ASSERT":
                if (verb == "UNLOCK") foreach (var w in Session.OpenWorkAreas()) w.Unlock();
                break;
            case "DEBUGOUT":
                Notify("DEBUGOUT " + rest);
                break;
            default:
                Notify($"{verb} {rest}: this designer/command is not available in this build (see roadmap).");
                break;
        }
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

    private void ExecCreateTable(CreateTableStmt ct)
    {
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
        var name = NameValue(at.Name);
        var wa = Session.FindAlias(Path.GetFileNameWithoutExtension(name)) ?? Session.Use(name, Session.FreeArea());
        var table = wa.Table;
        var fields = table.Fields.ToList();
        switch (at.Action)
        {
            case "ADD": fields.Add(ToFieldDef(at.Field!)); break;
            case "DROP":
                if (fields.RemoveAll(f => f.Name.Equals(at.DropField, StringComparison.OrdinalIgnoreCase)) == 0) throw VfpException.FieldNotFound(at.DropField!);
                break;
            case "ALTER":
            {
                var i = fields.FindIndex(f => f.Name.Equals(at.Field!.Name, StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw VfpException.FieldNotFound(at.Field!.Name);
                fields[i] = ToFieldDef(at.Field!);
                break;
            }
            case "RENAME":
            {
                var i = fields.FindIndex(f => f.Name.Equals(at.RenameFrom, StringComparison.OrdinalIgnoreCase));
                if (i < 0) throw VfpException.FieldNotFound(at.RenameFrom!);
                fields[i] = fields[i] with { Name = at.RenameTo! };
                break;
            }
        }
        // Rebuild: copy rows into a new table with the new structure (same approach as VFP's temporary copy).
        var rows = new List<(Dictionary<string, Value>, bool)>();
        foreach (var r in table.Scan(null, true))
            rows.Add((table.Fields.Select((f, i) => (at.Action == "RENAME" && f.Name.Equals(at.RenameFrom, StringComparison.OrdinalIgnoreCase) ? at.RenameTo! : f.Name, r.Values[i]))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase), r.Deleted));
        var tags = table.Schema.Tags.Where(t => at.Action != "DROP" || !t.Expression.Contains(at.DropField!, StringComparison.OrdinalIgnoreCase)).ToList();
        var store = table.Store;
        var alias = wa.Alias;
        var source = wa.Source;
        var number = wa.Number;
        wa.Close();
        var schema = new TableSchema(table.Name, fields);
        if (store.Kind == StoreKind.FreeTable)
        {
            var path = source;
            Session.CloseAll();
            File.Delete(path);
            var t = Session.CreateTable(schema, free: true, path);
            foreach (var (row, del) in rows) t.Append(fields.Select(f => row.TryGetValue(f.Name, out var v) ? ConvertForField(f.Normalize(), v) : f.Normalize().BlankValue()).ToArray(), del);
            foreach (var tag in tags) t.CreateTag(tag with { KeyColumn = "" });
            Session.Use(path, number, alias);
        }
        else
        {
            store.DropTable(table.Name);
            var t = store.CreateTable(schema, this);
            foreach (var (row, del) in rows) t.Append(fields.Select(f => row.TryGetValue(f.Name, out var v) ? ConvertForField(f.Normalize(), v) : f.Normalize().BlankValue()).ToArray(), del);
            foreach (var tag in tags) t.CreateTag(tag with { KeyColumn = "" });
            Session.Use(store.Name + "!" + table.Name, number, alias);
        }
    }

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
        WriteLine((ls.Off ? "" : "Record# ") + Row(cols.Select(c => c.Header)));
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
