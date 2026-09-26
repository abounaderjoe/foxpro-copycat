using System.Text;
using System.Text.RegularExpressions;
using JoePro.Core;
using Microsoft.Data.Sqlite;

namespace JoePro.Data;

/// <summary>A position in an index order: (key bytes, record number).</summary>
public readonly record struct Position(byte[]? Key, int RecNo);

/// <summary>
/// An open table in a <see cref="Store"/>. Each row carries hidden system columns:
/// _recno (dense physical record number, FoxPro natural order), _deleted and _rowver.
/// Each index tag is a key column holding order-preserving key bytes (see <see cref="KeyEncoder"/>),
/// computed by the engine from the tag's FoxPro expression and indexed by SQLite.
/// </summary>
public sealed partial class Table
{
    private const int BatchSize = 64;
    private readonly Dictionary<string, (ICompiledExpression Key, ICompiledExpression? For)> _compiled = new(StringComparer.OrdinalIgnoreCase);

    internal Table(Store store, TableSchema schema, string sqlName)
    {
        Store = store;
        Schema = schema;
        SqlName = sqlName;
    }

    public Store Store { get; }
    public TableSchema Schema { get; }
    public string Name => Schema.Name;
    public IReadOnlyList<FieldDef> Fields => Schema.Fields;
    internal string SqlName { get; }
    public IExpressionHost? ExpressionHost { get; set; }

    private string Col(FieldDef f) => $"\"c_{f.Name.ToLowerInvariant()}\"";
    private string SelectList => "_recno, _deleted, _rowver" + string.Concat(Fields.Select(f => ", " + Col(f)));

    public int RecordCount => (int)Store.ScalarLong($"SELECT MAX(_recno) FROM \"{SqlName}\"");

    internal void InvalidateCaches() { }

    // ---- Reading ----------------------------------------------------------------------

    private RowData ReadRow(SqliteDataReader r)
    {
        var values = new Value[Fields.Count];
        for (int i = 0; i < Fields.Count; i++) values[i] = ValueCodec.FromDb(Fields[i], r.GetValue(3 + i));
        return new RowData(this, r.GetInt32(0), r.GetInt64(1) != 0, values, r.GetInt64(2));
    }

    public RowData? Read(int recNo)
    {
        using var cmd = Store.Command($"SELECT {SelectList} FROM \"{SqlName}\" WHERE _recno=$r", ("$r", recNo));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRow(r) : null;
    }

    public long RowVersion(int recNo) =>
        Store.ScalarLong($"SELECT _rowver FROM \"{SqlName}\" WHERE _recno=$r", ("$r", recNo));

    public byte[]? ReadKey(TagDef tag, int recNo)
    {
        using var cmd = Store.Command($"SELECT \"{tag.KeyColumn}\" FROM \"{SqlName}\" WHERE _recno=$r", ("$r", recNo));
        return cmd.ExecuteScalar() as byte[];
    }

    /// <summary>
    /// Streams rows in the given order, starting after (or at, if <paramref name="inclusive"/>) <paramref name="from"/>.
    /// A null <paramref name="from"/> starts at the beginning (forward) or end (backward) of the order.
    /// </summary>
    public IEnumerable<RowData> Scan(TagDef? order, bool forward, Position? from = null, bool inclusive = false, bool skipDeleted = false)
    {
        var pos = from;
        var incl = inclusive;
        while (true)
        {
            var batch = FetchBatch(order, forward, pos, incl, skipDeleted);
            foreach (var (row, _) in batch) yield return row;
            if (batch.Count < BatchSize) yield break;
            var last = batch[^1];
            pos = new Position(last.Key, last.Row.RecNo);
            incl = false;
        }
    }

    private List<(RowData Row, byte[]? Key)> FetchBatch(TagDef? order, bool forward, Position? from, bool inclusive, bool skipDeleted)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        string orderBy;
        string keySel = "";
        if (order == null)
        {
            if (from is { } p)
            {
                where.Add(forward ? (inclusive ? "_recno >= $r" : "_recno > $r") : (inclusive ? "_recno <= $r" : "_recno < $r"));
                args.Add(("$r", p.RecNo));
            }
            orderBy = forward ? "_recno" : "_recno DESC";
        }
        else
        {
            var k = $"\"{order.KeyColumn}\"";
            keySel = ", " + k;
            where.Add($"{k} IS NOT NULL");
            // Ascending key direction in the scan (independent of recno tie-break direction).
            bool keyAsc = forward != order.Descending;
            if (from is { } p)
            {
                var gt = keyAsc ? ">" : "<";
                var rop = forward ? (inclusive ? ">=" : ">") : (inclusive ? "<=" : "<");
                if (p.Key == null)
                {
                    where.Add($"_recno {rop} $r");
                }
                else
                {
                    where.Add($"({k} {gt} $k OR ({k} = $k AND _recno {rop} $r))");
                    args.Add(("$k", p.Key));
                }
                args.Add(("$r", p.RecNo));
            }
            if (order.Kind == TagKind.Unique)
                where.Add($"_recno = (SELECT MIN(t2._recno) FROM \"{SqlName}\" t2 WHERE t2.{k} = \"{SqlName}\".{k})");
            orderBy = $"{k} {(keyAsc ? "ASC" : "DESC")}, _recno {(forward ? "ASC" : "DESC")}";
        }
        if (skipDeleted) where.Add("_deleted = 0");
        var sql = $"SELECT {SelectList}{keySel} FROM \"{SqlName}\"" +
                  (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                  $" ORDER BY {orderBy} LIMIT {BatchSize}";
        using var cmd = Store.Command(sql, args.ToArray());
        using var r = cmd.ExecuteReader();
        var list = new List<(RowData, byte[]?)>();
        while (r.Read())
        {
            var row = ReadRow(r);
            byte[]? key = order == null ? null : r.GetValue(3 + Fields.Count) as byte[];
            list.Add((row, key));
        }
        return list;
    }

    // ---- Writing ----------------------------------------------------------------------

    /// <summary>Appends a record. Missing values get blanks; autoincrement fields are assigned.</summary>
    public RowData Append(Value[]? values = null, bool deleted = false)
    {
        var vals = new Value[Fields.Count];
        for (int i = 0; i < Fields.Count; i++)
            vals[i] = values != null && i < values.Length ? Fields[i].Coerce(values[i]) : Fields[i].BlankValue();
        RowData row = null!;
        Store.InTransaction(() =>
        {
            for (int i = 0; i < Fields.Count; i++)
            {
                if (Fields[i].AutoIncNext is { } next)
                {
                    var current = Store.ScalarLong("SELECT autoinc_next FROM _jp_fields WHERE tbl=$t AND name=$n", ("$t", Name), ("$n", Fields[i].Name));
                    vals[i] = Value.Number(current, 0);
                    Store.Exec("UPDATE _jp_fields SET autoinc_next=autoinc_next+autoinc_step WHERE tbl=$t AND name=$n", ("$t", Name), ("$n", Fields[i].Name));
                    Schema.Fields[i] = Fields[i] with { AutoIncNext = current + Fields[i].AutoIncStep };
                }
            }
            var recNo = RecordCount + 1;
            row = new RowData(this, recNo, deleted, vals, 1);
            var cols = new StringBuilder("_recno, _deleted, _rowver");
            var ps = new StringBuilder("$r, $d, 1");
            var args = new List<(string, object?)> { ("$r", recNo), ("$d", deleted ? 1 : 0) };
            for (int i = 0; i < Fields.Count; i++)
            {
                cols.Append(", ").Append(Col(Fields[i]));
                ps.Append($", $v{i}");
                args.Add(($"$v{i}", ValueCodec.ToDb(Fields[i], vals[i])));
            }
            for (int t = 0; t < Schema.Tags.Count; t++)
            {
                cols.Append($", \"{Schema.Tags[t].KeyColumn}\"");
                ps.Append($", $k{t}");
                args.Add(($"$k{t}", ComputeKey(Schema.Tags[t], row)));
            }
            ExecWrite($"INSERT INTO \"{SqlName}\" ({cols}) VALUES ({ps})", args);
        });
        return row;
    }

    /// <summary>Writes all field values of <paramref name="row"/> and recomputes its index keys.</summary>
    public void Update(RowData row)
    {
        for (int i = 0; i < Fields.Count; i++) row.Values[i] = Fields[i].Coerce(row.Values[i]);
        var sets = new StringBuilder("_deleted = $d, _rowver = _rowver + 1");
        var args = new List<(string, object?)> { ("$r", row.RecNo), ("$d", row.Deleted ? 1 : 0) };
        for (int i = 0; i < Fields.Count; i++)
        {
            sets.Append($", {Col(Fields[i])} = $v{i}");
            args.Add(($"$v{i}", ValueCodec.ToDb(Fields[i], row.Values[i])));
        }
        for (int t = 0; t < Schema.Tags.Count; t++)
        {
            sets.Append($", \"{Schema.Tags[t].KeyColumn}\" = $k{t}");
            args.Add(($"$k{t}", ComputeKey(Schema.Tags[t], row)));
        }
        ExecWrite($"UPDATE \"{SqlName}\" SET {sets} WHERE _recno = $r", args);
        row.RowVersion++;
    }

    public void SetDeleted(int recNo, bool deleted)
    {
        var row = Read(recNo) ?? throw new VfpException(ErrorCodes.RecordOutOfRange, "Record is out of range.");
        if (row.Deleted == deleted) return;
        row.Deleted = deleted;
        Update(row);
    }

    private void ExecWrite(string sql, List<(string, object?)> args)
    {
        try
        {
            Store.Exec(sql, args.ToArray());
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            var tag = Schema.Tags.FirstOrDefault(t => ex.Message.Contains(t.KeyColumn, StringComparison.OrdinalIgnoreCase));
            throw VfpException.UniqueViolation(tag?.Name ?? "?");
        }
    }

    /// <summary>Physically removes deleted records and renumbers the rest (PACK).</summary>
    public void Pack()
    {
        Store.InTransaction(() =>
        {
            Store.Exec($"DELETE FROM \"{SqlName}\" WHERE _deleted = 1");
            Store.Exec("DROP TABLE IF EXISTS temp._jp_pack; CREATE TEMP TABLE _jp_pack(old INTEGER PRIMARY KEY, new INTEGER)");
            Store.Exec($"INSERT INTO temp._jp_pack SELECT _recno, ROW_NUMBER() OVER (ORDER BY _recno) FROM \"{SqlName}\"");
            Store.Exec($"UPDATE \"{SqlName}\" SET _recno = -(SELECT new FROM temp._jp_pack WHERE old = \"{SqlName}\"._recno)");
            Store.Exec($"UPDATE \"{SqlName}\" SET _recno = -_recno");
            Store.Exec("DROP TABLE temp._jp_pack");
        });
        if (Schema.Tags.Any(t => (t.Expression + t.ForExpression).Contains("RECNO", StringComparison.OrdinalIgnoreCase))) Reindex();
    }

    /// <summary>Removes all records (ZAP).</summary>
    public void Zap() => Store.Exec($"DELETE FROM \"{SqlName}\"");

    // ---- Indexes ----------------------------------------------------------------------

    public void CreateTag(TagDef tag)
    {
        if (!Regex.IsMatch(tag.Name, @"^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
            throw new VfpException(ErrorCodes.InvalidArgument, $"Invalid tag name '{tag.Name}'.");
        var existing = Schema.FindTag(tag.Name);
        if (existing != null) DropTag(existing.Name);
        var keyCol = "k_" + tag.Name.ToLowerInvariant();
        tag = tag with { Name = tag.Name.ToUpperInvariant(), KeyColumn = keyCol };
        Compile(tag); // validate expressions before changing anything
        Store.InTransaction(() =>
        {
            Store.Exec($"ALTER TABLE \"{SqlName}\" ADD COLUMN \"{keyCol}\" BLOB");
            Store.Exec("INSERT INTO _jp_tags(tbl, name, expr, for_expr, descending, kind, collation, keycol) VALUES ($t,$n,$e,$f,$d,$k,$c,$kc)",
                ("$t", Name), ("$n", tag.Name), ("$e", tag.Expression), ("$f", tag.ForExpression), ("$d", tag.Descending ? 1 : 0),
                ("$k", tag.Kind.ToString()), ("$c", tag.Collation), ("$kc", keyCol));
            Schema.Tags.Add(tag);
            ComputeKeys(tag);
            var unique = tag.Kind is TagKind.Candidate or TagKind.Primary ? "UNIQUE " : "";
            var cols = unique.Length > 0 ? $"\"{keyCol}\"" : $"\"{keyCol}\", _recno";
            try
            {
                Store.Exec($"CREATE {unique}INDEX \"ix_{SqlName}_{keyCol}\" ON \"{SqlName}\" ({cols}) WHERE \"{keyCol}\" IS NOT NULL");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                Schema.Tags.Remove(tag);
                throw VfpException.UniqueViolation(tag.Name);
            }
        });
    }

    public void DropTag(string name)
    {
        var tag = Schema.FindTag(name) ?? throw new VfpException(ErrorCodes.InvalidArgument, $"Tag {name} is not found.");
        Store.InTransaction(() =>
        {
            Store.Exec($"DROP INDEX IF EXISTS \"ix_{SqlName}_{tag.KeyColumn}\"");
            Store.Exec($"ALTER TABLE \"{SqlName}\" DROP COLUMN \"{tag.KeyColumn}\"");
            Store.Exec("DELETE FROM _jp_tags WHERE tbl=$t AND name=$n", ("$t", Name), ("$n", tag.Name));
        });
        Schema.Tags.Remove(tag);
        _compiled.Remove(tag.Name);
    }

    /// <summary>Recomputes every key of every tag (REINDEX).</summary>
    public void Reindex()
    {
        _compiled.Clear();
        Store.InTransaction(() =>
        {
            foreach (var tag in Schema.Tags) ComputeKeys(tag);
        });
    }

    private void ComputeKeys(TagDef tag)
    {
        foreach (var row in Scan(null, forward: true).ToList())
        {
            try
            {
                Store.Exec($"UPDATE \"{SqlName}\" SET \"{tag.KeyColumn}\" = $k WHERE _recno = $r", ("$k", ComputeKey(tag, row)), ("$r", row.RecNo));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw VfpException.UniqueViolation(tag.Name);
            }
        }
    }

    private (ICompiledExpression Key, ICompiledExpression? For) Compile(TagDef tag)
    {
        if (_compiled.TryGetValue(tag.Name, out var c)) return c;
        var key = CompileExpression(tag.Expression);
        var @for = string.IsNullOrWhiteSpace(tag.ForExpression) ? null : CompileExpression(tag.ForExpression!);
        c = (key, @for);
        _compiled[tag.Name] = c;
        return c;
    }

    internal ICompiledExpression CompileExpression(string expr)
    {
        if (ExpressionHost != null) return ExpressionHost.Compile(expr);
        // Without a language runtime, only plain field references are supported.
        var idx = Schema.FieldIndex(expr.Trim());
        if (idx < 0) throw new VfpException(ErrorCodes.FeatureNotAvailable, $"Cannot evaluate '{expr}' without a language runtime.");
        return new FieldReference(expr.Trim(), idx);
    }

    /// <summary>Computes the key bytes of a row for a tag, or null when its FOR clause excludes the row.</summary>
    public byte[]? ComputeKey(TagDef tag, IRecord row)
    {
        var (key, @for) = Compile(tag);
        if (@for != null)
        {
            var f = @for.Evaluate(row);
            if (f.Kind != ValueKind.Logical) throw VfpException.TypeMismatch();
            if (!f.AsBool) return null;
        }
        return EncodeKey(tag, key.Evaluate(row));
    }

    /// <summary>Encodes a value as a key for this tag (used by SEEK).</summary>
    public static byte[] EncodeKey(TagDef tag, Value v)
    {
        if (!string.Equals(tag.Collation, "MACHINE", StringComparison.OrdinalIgnoreCase) && v.Kind == ValueKind.Character)
            v = Value.String(v.AsString.ToUpperInvariant()); // TODO(oracle J1): real GENERAL collation weights.
        return KeyEncoder.Encode(v);
    }

    private sealed record FieldReference(string Source, int Index) : ICompiledExpression
    {
        public Value Evaluate(IRecord record) => record.Get(Index);
    }
}
