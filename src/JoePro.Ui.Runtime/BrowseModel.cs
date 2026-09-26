using System.Collections.ObjectModel;
using JoePro.Core;
using JoePro.Data;
using JoePro.Runtime;

namespace JoePro.Ui.Runtime;

/// <summary>One record shown in a Browse window or grid: its record number and the text of each column.</summary>
public sealed class BrowseRow : List<string>
{
    public BrowseRow(int recNo, bool deleted, IEnumerable<string> cells) : base(cells)
    {
        RecNo = recNo;
        Deleted = deleted;
    }

    public int RecNo { get; }
    public bool Deleted { get; set; }
    public string Mark => Deleted ? "✕" : "";
}

public sealed record BrowseColumn(string Header, int FieldIndex, char Type, bool ReadOnly);

/// <summary>
/// The data model behind the Browse window and the Grid control: loads the visible records of a work
/// area (respecting its order, filter and SET DELETED) and writes edits back with REPLACE.
/// </summary>
public sealed class BrowseModel
{
    private readonly Interpreter _rt;

    public BrowseModel(Interpreter rt, WorkArea area, IReadOnlyList<string>? fields = null, DataSession? session = null)
    {
        _rt = rt;
        Area = area;
        Session = session ?? area.Session;
        var t = area.Table;
        var names = fields is { Count: > 0 } ? fields : t.Fields.Select(f => f.Name).ToList();
        foreach (var n in names)
        {
            var name = n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n;
            var i = t.Schema.FieldIndex(name.Trim());
            if (i < 0) continue;
            Columns.Add(new BrowseColumn(t.Fields[i].Name, i, t.Fields[i].Type, area.ReadOnly || t.Fields[i].Type is 'G' or 'W' or 'Q'));
        }
    }

    public WorkArea Area { get; }
    public DataSession Session { get; }
    public List<BrowseColumn> Columns { get; } = new();
    public ObservableCollection<BrowseRow> Rows { get; } = new();
    /// <summary>Maximum rows loaded at once. Paging for very large tables is a later milestone.</summary>
    public int Limit { get; set; } = 100_000;

    public string Title => $"{Area.Alias} ({Area.Table.RecordCount} records)";

    public void Load()
    {
        Rows.Clear();
        if (!Area.InUse) return;
        var current = Area.Eof ? 0 : Area.RecNo;
        var o = Session.Options;
        Area.GoTop();
        while (!Area.Eof && Rows.Count < Limit)
        {
            Rows.Add(new BrowseRow(Area.RecNo, Area.Deleted, Columns.Select(c => Cell(Area.Get(c.FieldIndex), c.Type, o))));
            Area.Skip();
        }
        if (current > 0) Area.Go(current);
        else Area.GoTop();
    }

    private static string Cell(Value v, char type, SetOptions o) => type switch
    {
        'M' => v.Kind == ValueKind.Character ? v.AsString.Split('\n')[0].TrimEnd('\r') : "",
        'G' or 'W' or 'Q' => v.Kind == ValueKind.Binary && v.AsBinary.Length > 0 ? "(binary)" : "",
        _ => ValueText.ToText(v, o),
    };

    /// <summary>Moves the record pointer to a row (keeps the work area in sync with the grid selection).</summary>
    public void Select(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count) return;
        Area.Go(Rows[rowIndex].RecNo);
    }

    /// <summary>Writes an edited cell back to the table. Returns an error message, or null on success.</summary>
    public string? Commit(int rowIndex, int columnIndex, string text)
    {
        var row = Rows[rowIndex];
        var col = Columns[columnIndex];
        if (col.ReadOnly) return $"{col.Header} is read-only.";
        try
        {
            return _rt.InSession(Session, () =>
            {
                Area.Go(row.RecNo);
                var current = Area.Get(col.FieldIndex);
                if (!ValueText.TryParse(text, current, Session.Options, out var v, col.Type))
                    return $"'{text}' is not a valid {TypeName(col.Type)} value.";
                _rt.ReplaceField(Area, col.FieldIndex, v);
                row[columnIndex] = Cell(Area.Get(col.FieldIndex), col.Type, Session.Options);
                return (string?)null;
            });
        }
        catch (VfpException ex)
        {
            row[columnIndex] = Cell(Area.Get(col.FieldIndex), col.Type, Session.Options);
            return ex.Message;
        }
    }

    public void AppendRow()
    {
        _rt.InSession(Session, () => { _rt.AppendBlank(Area); return true; });
        Rows.Add(new BrowseRow(Area.RecNo, false, Columns.Select(c => Cell(Area.Get(c.FieldIndex), c.Type, Session.Options))));
    }

    /// <summary>Ctrl+T in a FoxPro Browse window: toggles the deleted mark.</summary>
    public void ToggleDelete(int rowIndex)
    {
        var row = Rows[rowIndex];
        Area.Go(row.RecNo);
        Area.Delete(!Area.Deleted);
        row.Deleted = Area.Deleted;
    }

    private static string TypeName(char t) => t switch
    {
        'N' or 'F' or 'B' or 'I' => "numeric", 'Y' => "currency", 'D' => "date", 'T' => "datetime", 'L' => "logical", _ => "character",
    };
}
