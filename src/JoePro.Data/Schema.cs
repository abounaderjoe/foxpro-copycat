using JoePro.Core;

namespace JoePro.Data;

public enum TagKind { Regular, Unique, Candidate, Primary }

/// <summary>An index tag: a FoxPro key expression plus an optional FOR filter.</summary>
public sealed record TagDef(string Name, string Expression, string? ForExpression = null, bool Descending = false,
    TagKind Kind = TagKind.Regular, string Collation = "MACHINE")
{
    public string KeyColumn { get; init; } = "";
}

public sealed class TableSchema
{
    public TableSchema(string name, IEnumerable<FieldDef> fields)
    {
        Name = name.ToUpperInvariant();
        Fields = fields.Select(f => f.Normalize()).ToList();
        var dup = Fields.GroupBy(f => f.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new VfpException(ErrorCodes.SyntaxError, $"Field {dup.Key} is duplicated.");
        if (Fields.Count == 0) throw new VfpException(ErrorCodes.SyntaxError, "A table must have at least one field.");
    }

    public string Name { get; }
    public List<FieldDef> Fields { get; }
    public List<TagDef> Tags { get; } = new();
    public string? RuleExpr { get; set; }
    public string? RuleText { get; set; }
    public string? InsertTrigger { get; set; }
    public string? UpdateTrigger { get; set; }
    public string? DeleteTrigger { get; set; }
    public string? Comment { get; set; }

    public int FieldIndex(string name)
    {
        for (int i = 0; i < Fields.Count; i++)
            if (string.Equals(Fields[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public TagDef? FindTag(string name) =>
        Tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A persistent relation between two tables of a database (drawn in the Database Designer), with its referential
/// integrity rules: CASCADE, RESTRICT or IGNORE for updates and deletes of the parent and inserts into the child.
/// </summary>
public sealed record RelationDef(string ParentTable, string ParentTag, string ChildTable, string ChildTag,
    string RiUpdate = "IGNORE", string RiDelete = "IGNORE", string RiInsert = "IGNORE")
{
    public static readonly string[] Rules = ["IGNORE", "CASCADE", "RESTRICT"];

    /// <summary>Upper-case names and full rule words ("C" → CASCADE, "R" → RESTRICT, anything else → IGNORE).</summary>
    public RelationDef Normalize() => new(ParentTable.ToUpperInvariant(), ParentTag.ToUpperInvariant(), ChildTable.ToUpperInvariant(), ChildTag.ToUpperInvariant(),
        Rule(RiUpdate), Rule(RiDelete), Rule(RiInsert));

    public static string Rule(string? r) => (r ?? "").Trim().ToUpperInvariant() switch
    {
        "C" or "CASCADE" => "CASCADE",
        "R" or "RESTRICT" => "RESTRICT",
        _ => "IGNORE",
    };

    /// <summary>The three-letter RI code ADBOBJECTS() reports (update, delete, insert), e.g. "CRI".</summary>
    public string RiCode => $"{Rule(RiUpdate)[0]}{Rule(RiDelete)[0]}{Rule(RiInsert)[0]}";

    public bool SameLink(RelationDef o) =>
        ParentTable.Equals(o.ParentTable, StringComparison.OrdinalIgnoreCase) && ParentTag.Equals(o.ParentTag, StringComparison.OrdinalIgnoreCase)
        && ChildTable.Equals(o.ChildTable, StringComparison.OrdinalIgnoreCase) && ChildTag.Equals(o.ChildTag, StringComparison.OrdinalIgnoreCase);
}
