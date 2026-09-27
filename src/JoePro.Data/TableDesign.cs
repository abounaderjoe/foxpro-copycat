using System.Globalization;
using System.Text;
using JoePro.Core;

namespace JoePro.Data;

/// <summary>A field in a <see cref="TableDesign"/>, remembering the stored field it came from (null for a new field).</summary>
public sealed class FieldDesign
{
    public FieldDesign(FieldDef field, string? originalName = null)
    {
        Field = field;
        OriginalName = originalName;
    }

    public FieldDef Field { get; set; }
    public string? OriginalName { get; }
}

public enum SchemaChangeKind { CreateTable, RenameTable, AddField, DropField, RenameField, AlterField, FieldProperties, DropTag, AddTag, TableProperties }

/// <summary>One difference between a stored table and its design.</summary>
public sealed record SchemaChange(SchemaChangeKind Kind, string Object, string Description)
{
    /// <summary>Changes of the record structure: the table is rebuilt (its rows copied into the new structure).</summary>
    public bool NeedsRebuild => Kind is SchemaChangeKind.AddField or SchemaChangeKind.DropField or SchemaChangeKind.RenameField or SchemaChangeKind.AlterField;
}

/// <summary>
/// An editable copy of a table's structure (the Table Designer's model): fields, index tags, the record rule,
/// triggers and comments. It lists what changed against the stored table and writes the FoxPro commands that make
/// the change (ALTER TABLE, INDEX ON, CREATE TRIGGER, DBSETPROP), which is also how schema-diff scripts are made.
/// </summary>
public sealed class TableDesign
{
    public string Name { get; set; } = "";
    /// <summary>The stored table's name; null for a new table.</summary>
    public string? OriginalName { get; init; }
    public List<FieldDesign> Fields { get; } = new();
    public List<TagDef> Tags { get; } = new();
    public string? Comment { get; set; }
    public string? RuleExpr { get; set; }
    public string? RuleText { get; set; }
    public string? InsertTrigger { get; set; }
    public string? UpdateTrigger { get; set; }
    public string? DeleteTrigger { get; set; }

    public bool IsNew => OriginalName == null;

    public static TableDesign From(TableSchema s)
    {
        var d = new TableDesign
        {
            Name = s.Name, OriginalName = s.Name, Comment = s.Comment, RuleExpr = s.RuleExpr, RuleText = s.RuleText,
            InsertTrigger = s.InsertTrigger, UpdateTrigger = s.UpdateTrigger, DeleteTrigger = s.DeleteTrigger,
        };
        d.Fields.AddRange(s.Fields.Select(f => new FieldDesign(f, f.Name)));
        d.Tags.AddRange(s.Tags.Select(t => t with { KeyColumn = "" }));
        return d;
    }

    public static TableDesign New(string name) => new() { Name = name.ToUpperInvariant() };

    public FieldDesign? FindField(string name) => Fields.FirstOrDefault(f => f.Field.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The schema the table will have. Throws when the design is invalid (see <see cref="Validate"/>).</summary>
    public TableSchema ToSchema()
    {
        if (Validate() is { Count: > 0 } errors) throw new VfpException(ErrorCodes.SyntaxError, errors[0]);
        var s = new TableSchema(Name, Fields.Select(f => f.Field))
        {
            Comment = Empty(Comment), RuleExpr = Empty(RuleExpr), RuleText = Empty(RuleText),
            InsertTrigger = Empty(InsertTrigger), UpdateTrigger = Empty(UpdateTrigger), DeleteTrigger = Empty(DeleteTrigger),
        };
        s.Tags.AddRange(Tags);
        return s;
    }

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static bool IsName(string s) =>
        s.Length is > 0 and <= 128 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>What is wrong with the design (empty when it can be saved).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!IsName(Name)) errors.Add($"'{Name}' is not a valid table name.");
        if (Fields.Count == 0) errors.Add("A table needs at least one field.");
        foreach (var f in Fields)
        {
            if (!IsName(f.Field.Name)) errors.Add($"'{f.Field.Name}' is not a valid field name.");
            try { f.Field.Normalize(); }
            catch (VfpException ex) { errors.Add(ex.Message); }
        }
        foreach (var dup in Fields.GroupBy(f => f.Field.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Field {dup.Key.ToUpperInvariant()} is duplicated.");
        foreach (var t in Tags)
        {
            if (!IsName(t.Name)) errors.Add($"'{t.Name}' is not a valid tag name.");
            if (string.IsNullOrWhiteSpace(t.Expression)) errors.Add($"Index {t.Name} needs a key expression.");
        }
        foreach (var dup in Tags.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"Index {dup.Key.ToUpperInvariant()} is duplicated.");
        if (Tags.Count(t => t.Kind == TagKind.Primary) > 1) errors.Add("A table can have only one primary index.");
        return errors;
    }

    // ---- Changes -----------------------------------------------------------------------------------

    private static bool SameStructure(FieldDef a, FieldDef b)
    {
        a = a.Normalize();
        b = b.Normalize();
        return a.Type == b.Type && a.Width == b.Width && a.Decimals == b.Decimals && a.Nullable == b.Nullable && a.Binary == b.Binary
               && a.AutoIncNext.HasValue == b.AutoIncNext.HasValue;
    }

    private static bool SameProperties(FieldDef a, FieldDef b) =>
        N(a.Caption) == N(b.Caption) && N(a.Comment) == N(b.Comment) && N(a.DefaultExpr) == N(b.DefaultExpr) && N(a.RuleExpr) == N(b.RuleExpr)
        && N(a.RuleText) == N(b.RuleText) && N(a.Format) == N(b.Format) && N(a.InputMask) == N(b.InputMask)
        && N(a.DisplayClass) == N(b.DisplayClass) && N(a.DisplayClassLibrary) == N(b.DisplayClassLibrary) && a.AutoIncStep == b.AutoIncStep;

    private static string N(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

    private static bool SameTag(TagDef a, TagDef b) =>
        a.Expression.Trim().Equals(b.Expression.Trim(), StringComparison.OrdinalIgnoreCase) && N(a.ForExpression).Equals(N(b.ForExpression), StringComparison.OrdinalIgnoreCase)
        && a.Descending == b.Descending && a.Kind == b.Kind;

    /// <summary>The changes from the stored table (null or a new design: everything is created).</summary>
    public IReadOnlyList<SchemaChange> Changes(TableSchema? original)
    {
        var list = new List<SchemaChange>();
        if (original == null || IsNew)
        {
            list.Add(new(SchemaChangeKind.CreateTable, Name, $"Create table {Name.ToUpperInvariant()} with {Fields.Count} field(s)."));
            foreach (var t in Tags) list.Add(new(SchemaChangeKind.AddTag, t.Name, $"Index {t.Name.ToUpperInvariant()} on {t.Expression}."));
            return list;
        }
        if (!original.Name.Equals(Name, StringComparison.OrdinalIgnoreCase))
            list.Add(new(SchemaChangeKind.RenameTable, Name, $"Rename table {original.Name} to {Name.ToUpperInvariant()}."));
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Fields)
        {
            var old = f.OriginalName == null ? null : original.Fields.FirstOrDefault(o => o.Name.Equals(f.OriginalName, StringComparison.OrdinalIgnoreCase));
            if (old == null)
            {
                list.Add(new(SchemaChangeKind.AddField, f.Field.Name, $"Add field {f.Field.Name.ToUpperInvariant()} {TypeText(f.Field)}."));
                continue;
            }
            kept.Add(old.Name);
            if (!old.Name.Equals(f.Field.Name, StringComparison.OrdinalIgnoreCase))
                list.Add(new(SchemaChangeKind.RenameField, f.Field.Name, $"Rename field {old.Name} to {f.Field.Name.ToUpperInvariant()}."));
            if (!SameStructure(old, f.Field))
                list.Add(new(SchemaChangeKind.AlterField, f.Field.Name, $"Change field {f.Field.Name.ToUpperInvariant()} from {TypeText(old)} to {TypeText(f.Field)}."));
            if (!SameProperties(old, f.Field))
                list.Add(new(SchemaChangeKind.FieldProperties, f.Field.Name, $"Change the properties of field {f.Field.Name.ToUpperInvariant()}."));
        }
        foreach (var old in original.Fields.Where(o => !kept.Contains(o.Name)))
            list.Add(new(SchemaChangeKind.DropField, old.Name, $"Remove field {old.Name}."));
        // A field moved to another position also changes the record structure.
        var order = Fields.Where(f => f.OriginalName != null && kept.Contains(f.OriginalName)).Select(f => f.OriginalName!.ToUpperInvariant()).ToList();
        var oldOrder = original.Fields.Where(o => kept.Contains(o.Name)).Select(o => o.Name.ToUpperInvariant()).ToList();
        if (!order.SequenceEqual(oldOrder) && !list.Any(c => c.NeedsRebuild))
            list.Add(new(SchemaChangeKind.AlterField, "", "Change the order of the fields."));
        foreach (var t in original.Tags)
        {
            var now = Tags.FirstOrDefault(n => n.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
            if (now == null || !SameTag(t, now)) list.Add(new(SchemaChangeKind.DropTag, t.Name, $"Remove index {t.Name}."));
        }
        foreach (var t in Tags)
        {
            var old = original.FindTag(t.Name);
            if (old == null || !SameTag(old, t)) list.Add(new(SchemaChangeKind.AddTag, t.Name, $"Index {t.Name.ToUpperInvariant()} on {t.Expression}."));
        }
        if (N(Comment) != N(original.Comment) || N(RuleExpr) != N(original.RuleExpr) || N(RuleText) != N(original.RuleText)
            || N(InsertTrigger) != N(original.InsertTrigger) || N(UpdateTrigger) != N(original.UpdateTrigger) || N(DeleteTrigger) != N(original.DeleteTrigger))
            list.Add(new(SchemaChangeKind.TableProperties, Name, "Change the table's rule, triggers or comment."));
        return list;
    }

    // ---- Script ------------------------------------------------------------------------------------

    /// <summary>A FoxPro string literal for any text.</summary>
    public static string Quote(string s) =>
        !s.Contains('"') ? "\"" + s + "\"" : !s.Contains('\'') ? "'" + s + "'" : !s.Contains(']') ? "[" + s + "]" : "\"" + s.Replace("\"", "\" + CHR(34) + \"") + "\"";

    /// <summary>C(20), N(10,2), I, D, M, V(40) …</summary>
    public static string TypeText(FieldDef f)
    {
        f = f.Normalize();
        return f.Type switch
        {
            'C' or 'V' or 'Q' => $"{f.Type}({f.Width})",
            'N' or 'F' => f.Decimals > 0 ? $"{f.Type}({f.Width},{f.Decimals})" : $"{f.Type}({f.Width})",
            'B' => f.Decimals > 0 ? $"B({f.Decimals})" : "B",
            _ => f.Type.ToString(),
        };
    }

    /// <summary>A field clause for CREATE TABLE / ALTER TABLE: name type [NULL|NOT NULL] [AUTOINC …] [DEFAULT …] [CHECK … ERROR …].</summary>
    public static string FieldClause(FieldDef f, bool inDatabase)
    {
        var sb = new StringBuilder($"{f.Name.ToUpperInvariant()} {TypeText(f)}");
        if (f.Nullable) sb.Append(" NULL");
        if (f.Binary && f.Type is 'C' or 'V' or 'M') sb.Append(" NOCPTRANS");
        if (f.AutoIncNext is { } next)
        {
            sb.Append(" AUTOINC");
            if (next != 1) sb.Append(" NEXTVALUE ").Append(next.ToString(CultureInfo.InvariantCulture));
            if (f.AutoIncStep != 1) sb.Append(" STEP ").Append(f.AutoIncStep.ToString(CultureInfo.InvariantCulture));
        }
        if (inDatabase && !string.IsNullOrWhiteSpace(f.DefaultExpr)) sb.Append(" DEFAULT ").Append(f.DefaultExpr.Trim());
        if (inDatabase && !string.IsNullOrWhiteSpace(f.RuleExpr))
        {
            sb.Append(" CHECK ").Append(f.RuleExpr.Trim());
            if (!string.IsNullOrWhiteSpace(f.RuleText)) sb.Append(" ERROR ").Append(Quote(f.RuleText.Trim()));
        }
        return sb.ToString();
    }

    public static string TagCommand(string table, TagDef t) => t.Kind == TagKind.Primary
        ? $"ALTER TABLE {table} ADD PRIMARY KEY {t.Expression.Trim()}{(string.IsNullOrWhiteSpace(t.ForExpression) ? "" : " FOR " + t.ForExpression.Trim())} TAG {t.Name.ToUpperInvariant()}"
        : $"INDEX ON {t.Expression.Trim()} TAG {t.Name.ToUpperInvariant()}{(string.IsNullOrWhiteSpace(t.ForExpression) ? "" : " FOR " + t.ForExpression.Trim())}"
          + (t.Descending ? " DESCENDING" : "") + (t.Kind == TagKind.Candidate ? " CANDIDATE" : t.Kind == TagKind.Unique ? " UNIQUE" : "");

    /// <summary>
    /// The FoxPro commands that turn the stored table into this design (or create it when <paramref name="original"/>
    /// is null). Rules, defaults, triggers, captions and comments are database features; free tables get the structure
    /// and indexes only.
    /// </summary>
    public string Script(TableSchema? original, bool inDatabase)
    {
        var sb = new StringBuilder();
        void L(string line) => sb.Append(line).Append('\n');
        var name = Name.ToUpperInvariant();
        var fresh = original == null || IsNew;
        if (fresh)
        {
            L($"CREATE TABLE {name}{(inDatabase ? "" : " FREE")} ( ;");
            for (int i = 0; i < Fields.Count; i++)
                L($"    {FieldClause(Fields[i].Field, inDatabase)}{(i < Fields.Count - 1 ? ", ;" : ")")}");
            foreach (var t in Tags) L(TagCommand(name, t));
        }
        else
        {
            var changes = Changes(original);
            if (changes.Any(c => c.Kind == SchemaChangeKind.RenameTable)) L($"RENAME TABLE {original!.Name} TO {name}");
            foreach (var c in changes.Where(c => c.Kind == SchemaChangeKind.DropTag)) L($"DELETE TAG {c.Object.ToUpperInvariant()}");
            var kept = Fields.Where(f => f.OriginalName != null).Select(f => f.OriginalName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in original!.Fields.Where(o => !kept.Contains(o.Name))) L($"ALTER TABLE {name} DROP COLUMN {old.Name}");
            foreach (var f in Fields)
            {
                var old = f.OriginalName == null ? null : original.Fields.FirstOrDefault(o => o.Name.Equals(f.OriginalName, StringComparison.OrdinalIgnoreCase));
                if (old == null) { L($"ALTER TABLE {name} ADD COLUMN {FieldClause(f.Field, inDatabase)}"); continue; }
                var fieldName = f.Field.Name.ToUpperInvariant();
                if (!old.Name.Equals(f.Field.Name, StringComparison.OrdinalIgnoreCase)) L($"ALTER TABLE {name} RENAME COLUMN {old.Name} TO {fieldName}");
                if (!SameStructure(old, f.Field)) L($"ALTER TABLE {name} ALTER COLUMN {FieldClause(f.Field with { DefaultExpr = null, RuleExpr = null }, false)}{(f.Field.Nullable ? "" : " NOT NULL")}");
                if (!inDatabase) continue;
                if (N(old.DefaultExpr) != N(f.Field.DefaultExpr))
                    L(N(f.Field.DefaultExpr).Length == 0 ? $"ALTER TABLE {name} ALTER COLUMN {fieldName} DROP DEFAULT" : $"ALTER TABLE {name} ALTER COLUMN {fieldName} SET DEFAULT {f.Field.DefaultExpr!.Trim()}");
                if (N(old.RuleExpr) != N(f.Field.RuleExpr) || N(old.RuleText) != N(f.Field.RuleText))
                    L(N(f.Field.RuleExpr).Length == 0 ? $"ALTER TABLE {name} ALTER COLUMN {fieldName} DROP CHECK"
                        : $"ALTER TABLE {name} ALTER COLUMN {fieldName} SET CHECK {f.Field.RuleExpr!.Trim()}{(N(f.Field.RuleText).Length > 0 ? " ERROR " + Quote(f.Field.RuleText!.Trim()) : "")}");
            }
            foreach (var c in changes.Where(c => c.Kind == SchemaChangeKind.AddTag)) L(TagCommand(name, Tags.First(t => t.Name.Equals(c.Object, StringComparison.OrdinalIgnoreCase))));
        }
        if (!inDatabase) return sb.ToString();
        // Database-only properties: for a new table everything that is set, for a changed one what differs.
        var o = fresh ? null : original;
        if (N(RuleExpr) != N(o?.RuleExpr) || N(RuleText) != N(o?.RuleText))
            L(N(RuleExpr).Length == 0 ? $"ALTER TABLE {name} DROP CHECK" : $"ALTER TABLE {name} SET CHECK {RuleExpr!.Trim()}{(N(RuleText).Length > 0 ? " ERROR " + Quote(RuleText!.Trim()) : "")}");
        foreach (var (kind, now, was) in new[] { ("INSERT", InsertTrigger, o?.InsertTrigger), ("UPDATE", UpdateTrigger, o?.UpdateTrigger), ("DELETE", DeleteTrigger, o?.DeleteTrigger) })
            if (N(now) != N(was)) L(N(now).Length == 0 ? $"DELETE TRIGGER ON {name} FOR {kind}" : $"CREATE TRIGGER ON {name} FOR {kind} AS {now!.Trim()}");
        if (N(Comment) != N(o?.Comment)) L($"=DBSETPROP({Quote(name)}, \"TABLE\", \"Comment\", {Quote(N(Comment))})");
        foreach (var f in Fields)
        {
            var old = o == null || f.OriginalName == null ? null : o.Fields.FirstOrDefault(x => x.Name.Equals(f.OriginalName, StringComparison.OrdinalIgnoreCase));
            foreach (var (prop, now, was) in new[]
            {
                ("Caption", f.Field.Caption, old?.Caption), ("Comment", f.Field.Comment, old?.Comment), ("Format", f.Field.Format, old?.Format),
                ("InputMask", f.Field.InputMask, old?.InputMask), ("DisplayClass", f.Field.DisplayClass, old?.DisplayClass),
                ("DisplayClassLibrary", f.Field.DisplayClassLibrary, old?.DisplayClassLibrary),
            })
                if (N(now) != N(was)) L($"=DBSETPROP({Quote(name + "." + f.Field.Name.ToUpperInvariant())}, \"FIELD\", {Quote(prop)}, {Quote(N(now))})");
        }
        return sb.ToString();
    }
}
