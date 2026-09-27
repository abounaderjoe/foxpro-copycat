using System.Text.Json;
using JoePro.Core;

namespace JoePro.Data;

/// <summary>One committed change: the table, record, operation (I insert, U update, D delete, R recall), who made it
/// (the origin tag: "local", a Data Server user, or the sync agent), and the old and new field values.</summary>
public sealed record JournalEntry(long Seq, DateTime TimeUtc, string Table, int RecNo, char Op, string Origin,
    IReadOnlyDictionary<string, Value>? Old, IReadOnlyDictionary<string, Value>? New)
{
    /// <summary>The fields an update changed.</summary>
    public IEnumerable<string> ChangedFields => Old == null || New == null ? New?.Keys ?? Old?.Keys ?? [] :
        New.Keys.Where(k => !Old.TryGetValue(k, out var o) || !Same(o, New[k]));

    private static bool Same(Value a, Value b) => a.IsNull ? b.IsNull : !b.IsNull && a.Kind == b.Kind && Equals(a.ToString(), b.ToString());
}

/// <summary>
/// The change journal: when it is on (DBSETPROP(db, "DATABASE", "Journal", .T.)), every insert, update, delete and
/// recall of a table in the database is recorded in the same transaction as the change, with an origin tag. The Data
/// Server's change notifications and two-way sync read it.
/// </summary>
public sealed partial class Store
{
    private bool? _journal;

    /// <summary>Tags the changes this connection makes (loop prevention in sync: the applier's changes are not captured again).</summary>
    public string Origin { get; set; } = "local";

    public bool JournalEnabled
    {
        get => _journal ??= GetMeta("journal") == "on";
        set
        {
            if (value)
                Exec("""
                    CREATE TABLE IF NOT EXISTS _jp_journal(seq INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, tbl TEXT NOT NULL COLLATE NOCASE,
                        recno INTEGER NOT NULL, op TEXT NOT NULL, origin TEXT NOT NULL, old TEXT, new TEXT)
                    """);
            SetMeta("journal", value ? "on" : "off");
            _journal = value;
        }
    }

    private static string? Encode(Table t, Value[]? values)
    {
        if (values == null) return null;
        var d = new Dictionary<string, object?>();
        for (int i = 0; i < t.Fields.Count; i++)
        {
            var o = values[i].IsNull ? null : ValueCodec.ToDb(t.Fields[i], values[i]);
            d[t.Fields[i].Name] = o is byte[] b ? "b64:" + Convert.ToBase64String(b) : o is DBNull ? null : o;
        }
        return JsonSerializer.Serialize(d);
    }

    private static Dictionary<string, Value>? Decode(Table? t, string? json)
    {
        if (json == null) return null;
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
        var result = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, e) in raw)
        {
            object? o = e.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => e.GetString() is { } s && s.StartsWith("b64:") ? Convert.FromBase64String(s[4..]) : e.GetString(),
                JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
                _ => e.ToString(),
            };
            var field = t?.Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            result[name] = field != null ? ValueCodec.FromDb(field, o) : o switch
            {
                null => Value.Null, string s => Value.String(s), long l => Value.Number(l, 0), double d => Value.Number(d), byte[] b => Value.Binary(b), _ => Value.Null,
            };
        }
        return result;
    }

    internal void Journal(Table t, char op, int recNo, Value[]? old, Value[]? @new)
    {
        if (Kind != StoreKind.Database || !JournalEnabled) return;
        Link.Execute("INSERT INTO _jp_journal(ts, tbl, recno, op, origin, old, new) VALUES ($ts,$t,$r,$op,$o,$old,$new)",
            [("$ts", DateTime.UtcNow.Ticks), ("$t", t.Name), ("$r", recNo), ("$op", op.ToString()), ("$o", Origin), ("$old", Encode(t, old)), ("$new", Encode(t, @new))], prepared: true);
    }

    /// <summary>The journal entries after <paramref name="afterSeq"/>, oldest first.</summary>
    public List<JournalEntry> ReadJournal(long afterSeq = 0, int max = 1000)
    {
        if (!JournalEnabled) return new();
        return Query("SELECT seq, ts, tbl, recno, op, origin, old, new FROM _jp_journal WHERE seq > $s ORDER BY seq LIMIT $m", ("$s", afterSeq), ("$m", max))
            .Select(r =>
            {
                var table = r[2] as string ?? "";
                var t = HasTable(table) ? OpenTable(table) : null;
                return new JournalEntry(Convert.ToInt64(r[0]), new DateTime(Convert.ToInt64(r[1]), DateTimeKind.Utc), table, Convert.ToInt32(r[3]),
                    ((string)r[4]!)[0], (string)r[5]!, Decode(t, r[6] as string), Decode(t, r[7] as string));
            }).ToList();
    }

    /// <summary>The newest journal sequence number (0 when empty).</summary>
    public long JournalHead => JournalEnabled ? ScalarLong("SELECT MAX(seq) FROM _jp_journal") : 0;

    /// <summary>Removes entries up to <paramref name="seq"/> (after every reader has consumed them).</summary>
    public int TrimJournal(long seq) => JournalEnabled ? ExecCount("DELETE FROM _jp_journal WHERE seq <= $s", ("$s", seq)) : 0;
}
