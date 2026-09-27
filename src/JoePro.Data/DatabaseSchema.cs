using System.Globalization;
using System.Text;
using JoePro.Core;

namespace JoePro.Data;

/// <summary>
/// A database's definitions (tables, persistent relations, views, connections, stored procedures) without its data,
/// and the FoxPro script that changes one version of a database into another (schema-diff migration scripts).
/// </summary>
public sealed class DatabaseSchema
{
    public string Name { get; init; } = "";
    public List<TableSchema> Tables { get; } = new();
    public List<RelationDef> Relations { get; } = new();
    public List<ViewDefinition> Views { get; } = new();
    public List<ConnectionDefinition> Connections { get; } = new();
    public string StoredProcedures { get; set; } = "";

    public TableSchema? FindTable(string name) => Tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static DatabaseSchema Read(Store store)
    {
        var db = new DatabaseSchema { Name = store.Name, StoredProcedures = store.StoredProcedures.Replace("\r\n", "\n") };
        foreach (var name in store.TableNames()) db.Tables.Add(store.OpenTable(name).Schema);
        db.Relations.AddRange(store.Relations());
        foreach (var name in store.ObjectNames(DbObjectStore.ViewKind)) if (store.GetView(name) is { } v) db.Views.Add(v);
        foreach (var name in store.ObjectNames(DbObjectStore.ConnectionKind)) if (store.GetConnection(name) is { } c) db.Connections.Add(c);
        return db;
    }

    /// <summary>The script that creates this database's definitions in an empty database.</summary>
    public string CreateScript() => DiffScript(new DatabaseSchema { Name = Name }, this);

    private static string Literal(Value v) => v.Kind switch
    {
        ValueKind.Logical => v.AsBool ? ".T." : ".F.",
        ValueKind.Number => v.AsNumber.ToString(CultureInfo.InvariantCulture),
        ValueKind.Null => ".NULL.",
        _ => TableDesign.Quote(v.AsString),
    };

    private static string RelationKey(RelationDef r) => $"{r.ChildTable}.{r.ChildTag}>{r.ParentTable}.{r.ParentTag}".ToUpperInvariant();

    private static bool SameView(ViewDefinition a, ViewDefinition b) =>
        a.Sql.Trim() == b.Sql.Trim() && a.Remote == b.Remote && (a.Connection ?? "") == (b.Connection ?? "") && a.ShareConnection == b.ShareConnection
        && Props(a) == Props(b);

    private static string Props(ViewDefinition v) =>
        string.Join(";", v.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key.ToUpperInvariant() + "=" + p.Value))
        + "|" + string.Join(";", v.FieldProperties.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => f.Value.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => f.Key.ToUpperInvariant() + "." + p.Key.ToUpperInvariant() + "=" + p.Value)));

    private static bool SameConnection(ConnectionDefinition a, ConnectionDefinition b) =>
        (a.DataSource ?? "") == (b.DataSource ?? "") && (a.UserId ?? "") == (b.UserId ?? "") && (a.Database ?? "") == (b.Database ?? "") && (a.ConnectString ?? "") == (b.ConnectString ?? "");

    /// <summary>
    /// The FoxPro commands that turn database <paramref name="from"/> into <paramref name="to"/>: run them with the
    /// database open. Tables are matched by name (a renamed table is dropped and created, which loses its rows, so the
    /// script says so); fields are matched by name too.
    /// </summary>
    public static string DiffScript(DatabaseSchema from, DatabaseSchema to)
    {
        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');
        L($"* Joe Pro schema update: {(from.Tables.Count == 0 && from.Views.Count == 0 ? "create" : from.Name.ToUpperInvariant() + " to")} {to.Name.ToUpperInvariant()}");
        L("* Run it with the database open and current (OPEN DATABASE … EXCLUSIVE).");
        var before = sb.Length;

        // Relations that go away or change first, so tables and tags can change under them.
        var oldRels = from.Relations.ToDictionary(RelationKey);
        var newRels = to.Relations.ToDictionary(RelationKey);
        foreach (var (key, r) in oldRels.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (!newRels.TryGetValue(key, out var n) || n.RiCode != r.RiCode)
                L($"ALTER TABLE {r.ChildTable} DROP FOREIGN KEY TAG {r.ChildTag} SAVE");
        foreach (var v in from.Views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            if (to.Views.FirstOrDefault(n => n.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase)) is not { } nv || !SameView(v, nv))
                L($"DROP VIEW {v.Name.ToUpperInvariant()}");
        foreach (var t in from.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
            if (to.FindTable(t.Name) == null)
            {
                L($"* Table {t.Name} is not in the new version; its rows are lost.");
                L($"DROP TABLE {t.Name}");
            }
        foreach (var c in from.Connections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            if (to.Connections.FirstOrDefault(n => n.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is not { } nc || !SameConnection(c, nc))
                L($"DELETE CONNECTION {c.Name.ToUpperInvariant()}");

        foreach (var t in to.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var old = from.FindTable(t.Name);
            var design = TableDesign.From(t);
            TableDesign d;
            if (old == null) d = CopyAsNew(design);
            else
            {
                // Match fields by name: a field that exists in both versions keeps its rows.
                d = new TableDesign
                {
                    Name = t.Name, OriginalName = old.Name, Comment = t.Comment, RuleExpr = t.RuleExpr, RuleText = t.RuleText,
                    InsertTrigger = t.InsertTrigger, UpdateTrigger = t.UpdateTrigger, DeleteTrigger = t.DeleteTrigger,
                };
                d.Fields.AddRange(t.Fields.Select(f => new FieldDesign(f, old.FieldIndex(f.Name) >= 0 ? old.Fields[old.FieldIndex(f.Name)].Name : null)));
                d.Tags.AddRange(design.Tags);
            }
            var script = d.Script(old, inDatabase: true);
            if (script.Length > 0)
            {
                L();
                L($"* Table {t.Name}");
                sb.Append(script);
            }
        }

        foreach (var c in to.Connections.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (from.Connections.FirstOrDefault(o => o.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is { } oc && SameConnection(oc, c)) continue;
            var line = new StringBuilder($"CREATE CONNECTION {c.Name.ToUpperInvariant()}");
            if (!string.IsNullOrEmpty(c.ConnectString)) line.Append(" CONNSTRING ").Append(TableDesign.Quote(c.ConnectString));
            else
            {
                if (!string.IsNullOrEmpty(c.DataSource)) line.Append(" DATASOURCE ").Append(TableDesign.Quote(c.DataSource));
                if (!string.IsNullOrEmpty(c.UserId)) line.Append(" USERID ").Append(TableDesign.Quote(c.UserId));
                if (!string.IsNullOrEmpty(c.Database)) line.Append(" DATABASE ").Append(TableDesign.Quote(c.Database));
            }
            L();
            if (!string.IsNullOrEmpty(c.Password)) L("* The connection's password is not written to scripts; set it with DBSETPROP(…, \"CONNECTION\", \"PassWord\", …).");
            L(line.ToString());
        }

        foreach (var v in to.Views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (from.Views.FirstOrDefault(o => o.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase)) is { } ov && SameView(ov, v)) continue;
            L();
            L($"CREATE SQL VIEW {v.Name.ToUpperInvariant()}{(v.Remote ? $" REMOTE CONNECTION {v.Connection}{(v.ShareConnection ? " SHARE" : "")}" : "")} AS ;");
            L("    " + v.Sql.Trim().Replace("\r\n", " ").Replace('\n', ' '));
            foreach (var (prop, value) in v.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                L($"=DBSETPROP({TableDesign.Quote(v.Name.ToUpperInvariant())}, \"VIEW\", {TableDesign.Quote(prop)}, {Literal(PropValue.Decode(value))})");
            foreach (var (field, props) in v.FieldProperties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                foreach (var (prop, value) in props.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                    L($"=DBSETPROP({TableDesign.Quote(v.Name.ToUpperInvariant() + "." + field.ToUpperInvariant())}, \"FIELD\", {TableDesign.Quote(prop)}, {Literal(PropValue.Decode(value))})");
        }

        foreach (var (key, r) in newRels.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (oldRels.TryGetValue(key, out var o) && o.RiCode == r.RiCode) continue;
            var ri = string.Concat(
                r.RiUpdate != "IGNORE" ? " ON UPDATE " + r.RiUpdate : "",
                r.RiDelete != "IGNORE" ? " ON DELETE " + r.RiDelete : "",
                r.RiInsert != "IGNORE" ? " ON INSERT " + r.RiInsert : "");
            L();
            L($"ALTER TABLE {r.ChildTable} ADD FOREIGN KEY TAG {r.ChildTag} REFERENCES {r.ParentTable} TAG {r.ParentTag}{ri}");
        }

        if (from.StoredProcedures.Trim() != to.StoredProcedures.Trim())
        {
            L();
            L("* Stored procedures");
            if (to.StoredProcedures.Trim().Length == 0)
            {
                L("=STRTOFILE(\"\", \"joepro-procedures.prg\")");
            }
            else
            {
                L("TEXT TO m.lcJoeProProcedures NOSHOW");
                sb.Append(to.StoredProcedures.TrimEnd()).Append('\n');
                L("ENDTEXT");
                L("=STRTOFILE(m.lcJoeProProcedures, \"joepro-procedures.prg\")");
            }
            L("APPEND PROCEDURES FROM joepro-procedures.prg OVERWRITE");
            L("ERASE joepro-procedures.prg");
        }
        if (sb.Length == before) L("* The databases have the same definitions; nothing to change.");
        return sb.ToString();
    }

    private static TableDesign CopyAsNew(TableDesign d)
    {
        var n = new TableDesign
        {
            Name = d.Name, Comment = d.Comment, RuleExpr = d.RuleExpr, RuleText = d.RuleText,
            InsertTrigger = d.InsertTrigger, UpdateTrigger = d.UpdateTrigger, DeleteTrigger = d.DeleteTrigger,
        };
        n.Fields.AddRange(d.Fields.Select(f => new FieldDesign(f.Field)));
        n.Tags.AddRange(d.Tags);
        return n;
    }
}
