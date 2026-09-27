using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>
/// Schema changes: table rebuilds (ALTER TABLE, the Table Designer), table and field rules, triggers, primary,
/// candidate and foreign keys (persistent relations), RENAME TABLE, and applying a <see cref="TableDesign"/>.
/// </summary>
public sealed partial class Interpreter
{
    /// <summary>The work area of the table a schema command names, opening the table when it is not open.</summary>
    private WorkArea SchemaArea(Expr nameExpr)
    {
        var name = NameValue(nameExpr);
        return Session.FindAlias(Path.GetFileNameWithoutExtension(name)) ?? Session.Use(name, Session.FreeArea());
    }

    private static TableSchema CopySchema(TableSchema s)
    {
        var copy = new TableSchema(s.Name, s.Fields)
        {
            Comment = s.Comment, RuleExpr = s.RuleExpr, RuleText = s.RuleText,
            InsertTrigger = s.InsertTrigger, UpdateTrigger = s.UpdateTrigger, DeleteTrigger = s.DeleteTrigger,
        };
        copy.Tags.AddRange(s.Tags);
        return copy;
    }

    private static void RequireDatabase(Table table, string what)
    {
        if (table.Store.Kind != StoreKind.Database)
            throw new VfpException(ErrorCodes.InvalidArgument, $"{table.Name} is a free table; {what} need a table in a database.");
    }

    /// <summary>Changes properties that need no rebuild (rules, defaults, triggers, comments) of a database table.</summary>
    private static void SaveTableProperties(Table table, Action<TableSchema> change)
    {
        RequireDatabase(table, "rules, defaults and triggers");
        var schema = CopySchema(table.Schema);
        change(schema);
        table.Store.UpdateTableProperties(schema);
    }

    private void ExecTrigger(TriggerStmt t)
    {
        var table = SchemaArea(t.Table).Table;
        SaveTableProperties(table, s =>
        {
            switch (t.Kind)
            {
                case "INSERT": s.InsertTrigger = t.Expression; break;
                case "UPDATE": s.UpdateTrigger = t.Expression; break;
                default: s.DeleteTrigger = t.Expression; break;
            }
        });
    }

    private void ExecAlterTableRule(AlterTableRuleStmt a)
    {
        var wa = SchemaArea(a.Table);
        var table = wa.Table;
        int FieldAt(string name) => table.Schema.FieldIndex(name) is var i and >= 0 ? i : throw VfpException.FieldNotFound(name);
        void SetField(string column, Func<FieldDef, FieldDef> change) =>
            SaveTableProperties(table, s => { var i = FieldAt(column); s.Fields[i] = change(s.Fields[i]); });
        switch (a.Action)
        {
            case "SETCHECK": SaveTableProperties(table, s => { s.RuleExpr = a.Expression; s.RuleText = a.ErrorText; }); break;
            case "DROPCHECK": SaveTableProperties(table, s => { s.RuleExpr = null; s.RuleText = null; }); break;
            case "SETDEFAULT": SetField(a.Column!, f => f with { DefaultExpr = a.Expression }); break;
            case "DROPDEFAULT": SetField(a.Column!, f => f with { DefaultExpr = null }); break;
            case "SETCOLUMNCHECK": SetField(a.Column!, f => f with { RuleExpr = a.Expression, RuleText = a.ErrorText }); break;
            case "DROPCOLUMNCHECK": SetField(a.Column!, f => f with { RuleExpr = null, RuleText = null }); break;
            case "NULL" or "NOTNULL":
            {
                var schema = CopySchema(table.Schema);
                var i = FieldAt(a.Column!);
                schema.Fields[i] = schema.Fields[i] with { Nullable = a.Action == "NULL" };
                RebuildTable(table, schema, f => f.Name);
                break;
            }
            case "ADDPRIMARY" or "ADDUNIQUE":
            {
                var primary = a.Action == "ADDPRIMARY";
                if (primary)
                {
                    RequireDatabase(table, "primary keys");
                    if (table.Schema.Tags.Any(t => t.Kind == TagKind.Primary)) throw new VfpException(1889, $"Table {table.Name} already has a primary key.");
                }
                var tag = a.Tag ?? (TableDesign.IsName(a.Expression!) ? a.Expression! : throw VfpException.Syntax("TAG name is required when the key is an expression."));
                if (table.Schema.FindTag(tag) != null) throw new VfpException(ErrorCodes.FileInUse, $"Tag {tag.ToUpperInvariant()} already exists.");
                table.CreateTag(new TagDef(tag.ToUpperInvariant(), a.Expression!, a.ForExpression, false, primary ? TagKind.Primary : TagKind.Candidate));
                break;
            }
            case "DROPPRIMARY":
            {
                var pk = table.Schema.Tags.FirstOrDefault(t => t.Kind == TagKind.Primary) ?? throw new VfpException(1890, $"Table {table.Name} has no primary key.");
                DropTagWithRelations(wa, pk.Name);
                break;
            }
            case "DROPUNIQUE":
            {
                var tag = table.Schema.FindTag(a.Tag!) is { Kind: TagKind.Candidate } t ? t : throw new VfpException(1890, $"Tag {a.Tag!.ToUpperInvariant()} is not a candidate key of {table.Name}.");
                DropTagWithRelations(wa, tag.Name);
                break;
            }
            case "ADDFOREIGN":
            {
                RequireDatabase(table, "persistent relations");
                var db = table.Store;
                if (!db.HasTable(a.References!)) throw VfpException.FileNotFound(a.References!);
                var parent = db.OpenTable(a.References!, this);
                var parentTag = a.ReferencesTag != null
                    ? parent.Schema.FindTag(a.ReferencesTag) ?? throw new VfpException(1890, $"Tag {a.ReferencesTag.ToUpperInvariant()} is not found in {parent.Name}.")
                    : parent.Schema.Tags.FirstOrDefault(t => t.Kind == TagKind.Primary) ?? throw new VfpException(1890, $"Table {parent.Name} has no primary key; name the TAG to reference.");
                if (parentTag.Kind is not (TagKind.Primary or TagKind.Candidate))
                    throw new VfpException(1890, $"A relation needs a primary or candidate index in the parent table; {parentTag.Name} is not one.");
                if (table.Schema.FindTag(a.Tag!) == null)
                    table.CreateTag(new TagDef(a.Tag!.ToUpperInvariant(), a.Expression ?? throw VfpException.Syntax($"Tag {a.Tag.ToUpperInvariant()} does not exist; give its key expression."), a.ForExpression));
                db.AddRelation(new RelationDef(parent.Name, parentTag.Name, table.Name, a.Tag!, a.RiUpdate ?? "IGNORE", a.RiDelete ?? "IGNORE", a.RiInsert ?? "IGNORE"));
                break;
            }
            case "DROPFOREIGN":
            {
                RequireDatabase(table, "persistent relations");
                var rels = table.Store.Relations().Where(r => r.ChildTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.ChildTag.Equals(a.Tag, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rels.Count == 0) throw new VfpException(1890, $"Tag {a.Tag!.ToUpperInvariant()} of {table.Name} is not a foreign key.");
                foreach (var r in rels) table.Store.RemoveRelation(r);
                if (!a.Save) DropTag(wa, a.Tag!);
                break;
            }
            default:
                throw VfpException.Syntax($"ALTER TABLE {a.Action} is not supported.");
        }
    }

    private static void DropTag(WorkArea wa, string tag)
    {
        if (wa.Order?.Name.Equals(tag, StringComparison.OrdinalIgnoreCase) == true) wa.SetOrder(null);
        wa.Table.DropTag(tag);
    }

    /// <summary>Drops a key tag and the persistent relations that use it.</summary>
    private static void DropTagWithRelations(WorkArea wa, string tag)
    {
        var table = wa.Table;
        if (table.Store.Kind == StoreKind.Database)
            foreach (var r in table.Store.Relations().Where(r =>
                         (r.ParentTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.ParentTag.Equals(tag, StringComparison.OrdinalIgnoreCase))
                         || (r.ChildTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.ChildTag.Equals(tag, StringComparison.OrdinalIgnoreCase))).ToList())
                table.Store.RemoveRelation(r);
        DropTag(wa, tag);
    }

    /// <summary>APPEND PROCEDURES FROM file [OVERWRITE] / COPY PROCEDURES TO file [ADDITIVE].</summary>
    private void ExecProceduresFile(ProceduresFileStmt p)
    {
        var db = Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
        var name = NameValue(p.File);
        if (p.Append)
        {
            var path = Session.ResolvePath(name, ".prg");
            if (!File.Exists(path)) throw VfpException.FileNotFound(name);
            var code = File.ReadAllText(path);
            db.StoredProcedures = p.Replace || db.StoredProcedures.Length == 0 ? code : db.StoredProcedures.TrimEnd() + "\n\n" + code;
        }
        else
        {
            var path = Path.IsPathRooted(name) ? name : Path.Combine(Options.Default_, Path.HasExtension(name) ? name : name + ".prg");
            if (p.Replace && File.Exists(path)) File.AppendAllText(path, db.StoredProcedures);
            else File.WriteAllText(path, db.StoredProcedures);
        }
    }

    /// <summary>RENAME TABLE old TO new (a table in the current database).</summary>
    private void RenameTableCommand(string rest)
    {
        var parts = rest.Split(" TO ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) throw VfpException.Syntax("RENAME TABLE name TO newname");
        var db = Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
        var oldName = parts[0].Trim('"', '\'');
        var newName = parts[1].Trim('"', '\'');
        if (!TableDesign.IsName(newName)) throw VfpException.Syntax($"'{newName}' is not a valid table name.");
        var areas = CloseAreasOf(db.OpenTable(oldName, this));
        db.RenameTable(oldName, newName);
        foreach (var (number, alias, order) in areas) Reopen(db.Name + "!" + newName, number, alias.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? newName : alias, order);
    }

    private List<(int Number, string Alias, string? Order)> CloseAreasOf(Table table)
    {
        var list = new List<(int, string, string?)>();
        foreach (var w in Session.OpenWorkAreas().Where(w => w.Table == table).ToList())
        {
            list.Add((w.Number, w.Alias, w.Order?.Name));
            w.Close();
        }
        return list;
    }

    private void Reopen(string source, int number, string alias, string? order)
    {
        var saved = Session.CurrentAreaNumber;
        var wa = Session.Use(source, number, alias, again: true);
        if (order != null && wa.Table.Schema.FindTag(order) is { } tag) wa.SetOrder(tag.Name);
        Session.Select(saved);
    }

    /// <summary>
    /// Rebuilds a table with a new structure: its rows are copied into the new fields (by <paramref name="sourceOf"/>,
    /// which names each new field's old field or null for a new one), keeping deleted flags, autoincrement values,
    /// index tags whose fields still exist and persistent relations. Work areas using the table are reopened.
    /// </summary>
    internal Table RebuildTable(Table table, TableSchema schema, Func<FieldDef, string?> sourceOf)
    {
        var store = table.Store;
        var oldFields = table.Fields.ToList();
        var fields = schema.Fields.Select(f => f.Normalize()).ToList();
        var from = fields.Select(f => sourceOf(f) is { } src ? oldFields.FindIndex(o => o.Name.Equals(src, StringComparison.OrdinalIgnoreCase)) : -1).ToArray();
        var rows = new List<(Value[] Values, bool Deleted)>();
        foreach (var r in table.Scan(null, true))
            rows.Add((fields.Select((f, i) => from[i] >= 0 ? ConvertForField(f, r.Values[from[i]]) : f.BlankValue()).ToArray(), r.Deleted));
        var relations = store.Kind == StoreKind.Database
            ? store.Relations().Where(r => r.ParentTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) || r.ChildTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        var areas = CloseAreasOf(table);
        // Rows are copied with their autoincrement values; the counters are set afterwards.
        var plain = new TableSchema(schema.Name, fields.Select(f => f with { AutoIncNext = null }))
        {
            Comment = schema.Comment, RuleExpr = schema.RuleExpr, RuleText = schema.RuleText,
            InsertTrigger = schema.InsertTrigger, UpdateTrigger = schema.UpdateTrigger, DeleteTrigger = schema.DeleteTrigger,
        };
        Table rebuilt;
        string source;
        if (store.Kind == StoreKind.FreeTable)
        {
            var path = store.Path;
            Session.ReleaseStore(path);
            File.Delete(path);
            foreach (var extra in new[] { path + "-wal", path + "-shm" }) if (File.Exists(extra)) File.Delete(extra);
            rebuilt = Session.StoreOf(CreateFreeTableFile(plain, path)).OpenTable(Path.GetFileNameWithoutExtension(path), this);
            source = path;
        }
        else
        {
            store.DropTable(table.Name);
            rebuilt = store.CreateTable(plain, this);
            source = store.Name + "!" + rebuilt.Name;
        }
        store = rebuilt.Store;
        store.Batch(() => { foreach (var (values, deleted) in rows) rebuilt.Append(values, deleted); });
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[i].AutoIncNext is not { } next) continue;
            var max = rows.Count == 0 ? (long?)null : rows.Max(r => r.Values[i].IsNull ? long.MinValue : (long)r.Values[i].AsNumber);
            var step = fields[i].AutoIncStep == 0 ? 1 : fields[i].AutoIncStep;
            var old = from[i] >= 0 ? oldFields[from[i]].AutoIncNext : null;
            rebuilt.EnableAutoIncrement(fields[i].Name, Math.Max(Math.Max(next, old ?? next), max is { } m and > long.MinValue ? m + step : next), step);
        }
        foreach (var tag in schema.Tags)
        {
            try { rebuilt.CreateTag(tag with { KeyColumn = "" }); }
            catch (VfpException ex) { Notify($"Index {tag.Name} was not rebuilt: {ex.Message}"); }
        }
        foreach (var r in relations)
        {
            var renamedParent = r.ParentTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) ? rebuilt.Name : r.ParentTable;
            var renamedChild = r.ChildTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) ? rebuilt.Name : r.ChildTable;
            var parentTable = renamedParent == rebuilt.Name ? rebuilt : store.OpenTable(renamedParent, this);
            var childTable = renamedChild == rebuilt.Name ? rebuilt : store.OpenTable(renamedChild, this);
            if (parentTable.Schema.FindTag(r.ParentTag) != null && childTable.Schema.FindTag(r.ChildTag) != null)
                store.AddRelation(r with { ParentTable = renamedParent, ChildTable = renamedChild });
            else Notify($"The relation {r.ParentTable}.{r.ParentTag} → {r.ChildTable}.{r.ChildTag} was removed: its index no longer exists.");
        }
        foreach (var (number, alias, order) in areas) Reopen(source, number, alias, order);
        return rebuilt;
    }

    private string CreateFreeTableFile(TableSchema schema, string path)
    {
        Session.CreateTable(schema, free: true, path);
        return path;
    }

    // ---- The Table Designer ---------------------------------------------------------------------

    /// <summary>
    /// Saves a Table Designer design: creates the table (in <paramref name="database"/>, or as the free table
    /// <paramref name="freeTablePath"/>), or changes the stored one: a structure change rebuilds it once, other changes
    /// (rules, triggers, comments, captions, indexes) are made in place. Returns the table.
    /// </summary>
    public Table ApplyTableDesign(TableDesign design, Store? database, string? freeTablePath = null)
    {
        if (design.Validate() is { Count: > 0 } errors) throw new VfpException(ErrorCodes.SyntaxError, errors[0]);
        var schema = design.ToSchema();
        if (database == null && freeTablePath == null) throw new ArgumentException("A table design needs a database or a free table path.");
        if (design.IsNew)
        {
            if (database != null)
            {
                if (database.HasTable(design.Name)) throw new VfpException(ErrorCodes.FileInUse, $"Table {design.Name.ToUpperInvariant()} already exists.");
                return database.CreateTable(schema, this);
            }
            var path = Path.GetFullPath(freeTablePath!);
            if (File.Exists(path)) throw new VfpException(ErrorCodes.FileInUse, $"File '{Path.GetFileName(path)}' already exists.");
            return Session.StoreOf(CreateFreeTableFile(schema, path)).OpenTable(Path.GetFileNameWithoutExtension(path), this);
        }
        var store = database ?? Session.StoreOf(freeTablePath!);
        var table = store.OpenTable(database != null ? design.OriginalName! : store.TableNames()[0], this);
        var original = table.Schema;
        var changes = design.Changes(original);
        if (changes.Count == 0) return table;
        if (changes.Any(c => c.Kind == SchemaChangeKind.RenameTable))
        {
            if (database == null) throw new VfpException(ErrorCodes.InvalidArgument, "A free table is renamed by renaming its file.");
            var areas = CloseAreasOf(table);
            database.RenameTable(table.Name, design.Name);
            table = database.OpenTable(design.Name, this);
            foreach (var (number, alias, order) in areas) Reopen(database.Name + "!" + table.Name, number, alias.Equals(original.Name, StringComparison.OrdinalIgnoreCase) ? table.Name : alias, order);
            original = table.Schema;
        }
        if (database == null && (N(schema.RuleExpr) != N(original.RuleExpr) || N(schema.InsertTrigger) != N(original.InsertTrigger) || N(schema.UpdateTrigger) != N(original.UpdateTrigger)
                                 || N(schema.DeleteTrigger) != N(original.DeleteTrigger) || schema.Fields.Any(f => N(f.DefaultExpr) != "" || N(f.RuleExpr) != "")))
            throw new VfpException(ErrorCodes.InvalidArgument, $"{table.Name} is a free table; rules, defaults and triggers need a table in a database.");
        if (changes.Any(c => c.NeedsRebuild))
        {
            var byName = design.Fields.ToDictionary(f => f.Field.Name.ToUpperInvariant(), f => f.OriginalName);
            return RebuildTable(table, schema, f => byName.GetValueOrDefault(f.Name.ToUpperInvariant()));
        }
        if (changes.Any(c => c.Kind is SchemaChangeKind.FieldProperties or SchemaChangeKind.TableProperties))
            store.UpdateTableProperties(schema);
        foreach (var c in changes.Where(c => c.Kind == SchemaChangeKind.DropTag))
            foreach (var w in Session.OpenWorkAreas().Where(w => w.Table == table).ToList())
                if (w.Order?.Name.Equals(c.Object, StringComparison.OrdinalIgnoreCase) == true) w.SetOrder(null);
        foreach (var c in changes.Where(c => c.Kind == SchemaChangeKind.DropTag)) table.DropTag(c.Object);
        foreach (var c in changes.Where(c => c.Kind == SchemaChangeKind.AddTag))
            table.CreateTag(design.Tags.First(t => t.Name.Equals(c.Object, StringComparison.OrdinalIgnoreCase)) with { KeyColumn = "" });
        return table;

        static string N(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
    }
}

/// <summary>
/// Referential integrity of persistent relations, as VFP's RI Builder triggers enforce it: deleting or changing the
/// key of a parent record cascades to, or is restricted by, its child records; inserting a child (or changing its
/// foreign key) can require a parent. A failure raises "Trigger failed" (1539) and undoes the whole change.
/// </summary>
public sealed partial class Interpreter
{
    private static IEnumerable<RelationDef> RelationsOf(Table t) =>
        t.Store.Kind == StoreKind.Database ? t.Store.Relations() : [];

    private static bool HasRiAsParent(Table t, Func<RelationDef, string> rule) =>
        RelationsOf(t).Any(r => r.ParentTable.Equals(t.Name, StringComparison.OrdinalIgnoreCase) && rule(r) != "IGNORE");

    private static bool HasRiAsChild(Table t) =>
        RelationsOf(t).Any(r => r.ChildTable.Equals(t.Name, StringComparison.OrdinalIgnoreCase) && r.RiInsert == "RESTRICT");

    /// <summary>DELETE of the current record: the delete trigger and referential integrity, undone together on failure.</summary>
    internal void DeleteWithRules(WorkArea wa)
    {
        var table = wa.Table;
        var ri = wa.BufferMode <= 1 && HasRiAsParent(table, r => r.RiDelete);
        if (!ri)
        {
            wa.Delete(true);
            try { RunTrigger(wa, table.Schema.DeleteTrigger, "Delete"); }
            catch { wa.Delete(false); throw; }
            return;
        }
        var recNo = wa.RecNo;
        try
        {
            table.Store.Atomic(() =>
            {
                wa.Delete(true);
                RunTrigger(wa, table.Schema.DeleteTrigger, "Delete");
                EnforceRiDelete(wa);
            });
        }
        catch
        {
            wa.Go(recNo);
            throw;
        }
    }

    /// <summary>Runs <paramref name="action"/> with the related table open in a spare work area.</summary>
    private void WithRelatedArea(Table table, Action<WorkArea> action)
    {
        var saved = Session.CurrentAreaNumber;
        var area = Session.Use(table.Store.Name + "!" + table.Name, Session.FreeArea(), "__RI" + Session.FreeArea(), again: true, exclusive: false);
        try { action(area); }
        finally
        {
            area.Close();
            Session.Select(saved);
        }
    }

    private void EnforceRiDelete(WorkArea wa)
    {
        var table = wa.Table;
        foreach (var r in RelationsOf(table).Where(r => r.ParentTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.RiDelete != "IGNORE").ToList())
        {
            if (table.Schema.FindTag(r.ParentTag) is not { } parentTag || !table.Store.HasTable(r.ChildTable)) continue;
            var child = table.Store.OpenTable(r.ChildTable, this);
            if (child.Schema.FindTag(r.ChildTag) is not { } childTag) continue;
            var key = EvalInArea(wa, parentTag.Expression);
            var related = child.RowsWithKey(childTag, key).Where(row => child != table || row.RecNo != wa.RecNo).Select(row => row.RecNo).ToList();
            if (related.Count == 0) continue;
            if (r.RiDelete == "RESTRICT")
                throw new VfpException(ErrorCodes.TriggerFailed, $"Trigger failed in {table.Name}: {child.Name} has {related.Count} related record(s) (delete is restricted).", "Delete");
            WithRelatedArea(child, area =>
            {
                foreach (var rec in related)
                {
                    area.Go(rec);
                    if (!area.Deleted) DeleteWithRules(area);
                }
            });
        }
    }

    /// <summary>The parent keys and child foreign keys of the current record, for relations with RI rules (before a change).</summary>
    private List<(RelationDef Relation, bool AsParent, TagDef Tag, byte[] Key, Value Value)> RiKeys(WorkArea wa)
    {
        var table = wa.Table;
        var list = new List<(RelationDef, bool, TagDef, byte[], Value)>();
        if (wa.BufferMode > 1 || wa.Current == null) return list;
        foreach (var r in RelationsOf(table))
        {
            if (r.ParentTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.RiUpdate != "IGNORE" && table.Schema.FindTag(r.ParentTag) is { } pt)
            {
                var v = EvalInArea(wa, pt.Expression);
                list.Add((r, true, pt, Table.EncodeKey(pt, v), v));
            }
            if (r.ChildTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.RiInsert == "RESTRICT" && table.Schema.FindTag(r.ChildTag) is { } ct)
            {
                var v = EvalInArea(wa, ct.Expression);
                list.Add((r, false, ct, Table.EncodeKey(ct, v), v));
            }
        }
        return list;
    }

    /// <summary>After a change of the current record: cascades or restricts changed parent keys, checks changed foreign keys.</summary>
    private void EnforceRiUpdate(WorkArea wa, List<(RelationDef Relation, bool AsParent, TagDef Tag, byte[] Key, Value Value)> before)
    {
        var table = wa.Table;
        foreach (var (r, asParent, tag, oldKey, oldValue) in before)
        {
            var newValue = EvalInArea(wa, tag.Expression);
            if (Table.EncodeKey(tag, newValue).AsSpan().SequenceEqual(oldKey)) continue;
            if (!asParent)
            {
                RequireParent(wa, r, newValue);
                continue;
            }
            if (!table.Store.HasTable(r.ChildTable)) continue;
            var child = table.Store.OpenTable(r.ChildTable, this);
            if (child.Schema.FindTag(r.ChildTag) is not { } childTag) continue;
            var related = child.RowsWithKey(childTag, oldValue).Where(row => child != table || row.RecNo != wa.RecNo).Select(row => row.RecNo).ToList();
            if (related.Count == 0) continue;
            if (r.RiUpdate == "RESTRICT")
                throw new VfpException(ErrorCodes.TriggerFailed, $"Trigger failed in {table.Name}: {child.Name} has {related.Count} related record(s) (changing the key is restricted).", "Update");
            var field = child.Schema.FieldIndex(childTag.Expression.Trim());
            if (field < 0)
                throw new VfpException(ErrorCodes.TriggerFailed, $"Trigger failed in {table.Name}: the key of {child.Name}.{childTag.Name} is an expression, so the change cannot cascade.", "Update");
            WithRelatedArea(child, area =>
            {
                foreach (var rec in related)
                {
                    area.Go(rec);
                    ReplaceWithRules(area, [(field, newValue)]);
                }
            });
        }
    }

    private void EnforceRiInsert(WorkArea wa)
    {
        if (wa.BufferMode > 1) return;
        var table = wa.Table;
        foreach (var r in RelationsOf(table).Where(r => r.ChildTable.Equals(table.Name, StringComparison.OrdinalIgnoreCase) && r.RiInsert == "RESTRICT").ToList())
            if (table.Schema.FindTag(r.ChildTag) is { } childTag)
                RequireParent(wa, r, EvalInArea(wa, childTag.Expression));
    }

    private void RequireParent(WorkArea wa, RelationDef r, Value key)
    {
        var table = wa.Table;
        if (!table.Store.HasTable(r.ParentTable)) return;
        var parent = table.Store.OpenTable(r.ParentTable, this);
        if (parent.Schema.FindTag(r.ParentTag) is not { } parentTag) return;
        if (parent.RowsWithKey(parentTag, key).Count == 0)
            throw new VfpException(ErrorCodes.TriggerFailed, $"Trigger failed in {table.Name}: no {parent.Name} record has the key {Formatter.ToDisplay(key, Options).Trim()} (insert is restricted).", "Insert");
    }
}

public sealed partial class Interpreter
{
    /// <summary>
    /// Creates or replaces a view of a database from its SELECT (the View Designer's save): the query is checked and
    /// each column's source recorded, as CREATE SQL VIEW does. Returns the new definition for its update criteria.
    /// </summary>
    public ViewDefinition DefineView(Store db, string name, string sql, bool remote = false, string? connection = null, bool share = false)
    {
        var saved = Session.CurrentDatabase;
        Session.SetDatabase(db.Name);
        try
        {
            Views.Create(this, new CreateViewStmt(new LiteralExpr(Value.String(name)), remote,
                connection == null ? null : new LiteralExpr(Value.String(connection)), share, sql));
        }
        finally { Session.SetDatabase(saved?.Name); }
        return db.GetView(name)!;
    }
}
