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
    // Scans fetch one row first (GO TOP, SEEK usually need no more), then 16, then 256 at a time as a scan continues.
    private const int FirstBatchSize = 1;
    private const int BatchSize = 256;
    private static int NextBatchSize(int size) => size == 1 ? 16 : BatchSize;
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

    public int RecordCount => Store.Prepared($"SELECT MAX(_recno) FROM \"{SqlName}\"").ExecuteScalar() is long n ? (int)n : 0;

    internal void InvalidateCaches() => _scanCache = null;

    // The last batch a scan fetched. SKIP, SCAN and LOCATE move one record at a time, so the next rows
    // are usually already here. The cache is dropped when anything writes to the store (except this
    // table's own single-row updates, which patch it) or another connection commits.
    private sealed class ScanCache
    {
        public required string? Tag { get; init; }
        public required bool Forward { get; init; }
        public required bool SkipDeleted { get; init; }
        public required List<(RowData Row, byte[]? Key)> Rows { get; init; }
        public required bool Complete { get; init; }
        public Dictionary<int, int> Index { get; } = new();
        public long WriteVersion { get; set; }
        public long DataVersion { get; init; }
    }

    private ScanCache? _scanCache;

    private ScanCache? FreshCache()
    {
        var c = _scanCache;
        if (c == null) return null;
        if (c.WriteVersion != Store.WriteVersion || c.DataVersion != Store.DataVersion()) { _scanCache = null; return null; }
        return c;
    }

    // ---- Reading ----------------------------------------------------------------------

    private RowData ReadRow(SqliteDataReader r)
    {
        var values = new Value[Fields.Count];
        for (int i = 0; i < Fields.Count; i++) values[i] = ValueCodec.FromDb(Fields[i], r.GetValue(3 + i));
        return new RowData(this, r.GetInt32(0), r.GetInt64(1) != 0, values, r.GetInt64(2));
    }

    public RowData? Read(int recNo)
    {
        using var r = Store.Prepared($"SELECT {SelectList} FROM \"{SqlName}\" WHERE _recno=$r", ("$r", recNo)).ExecuteReader();
        return r.Read() ? ReadRow(r) : null;
    }

    public long RowVersion(int recNo) =>
        Store.Prepared($"SELECT _rowver FROM \"{SqlName}\" WHERE _recno=$r", ("$r", recNo)).ExecuteScalar() is long v ? v : 0;

    public byte[]? ReadKey(TagDef tag, int recNo)
    {
        if (_scanCache is { } c && c.Tag == tag.Name && c.Index.TryGetValue(recNo, out var i) && FreshCache() != null) return c.Rows[i].Key;
        return Store.Prepared($"SELECT \"{tag.KeyColumn}\" FROM \"{SqlName}\" WHERE _recno=$r", ("$r", recNo)).ExecuteScalar() as byte[];
    }

    /// <summary>
    /// Streams rows in the given order, starting after (or at, if <paramref name="inclusive"/>) <paramref name="from"/>.
    /// A null <paramref name="from"/> starts at the beginning (forward) or end (backward) of the order.
    /// </summary>
    public IEnumerable<RowData> Scan(TagDef? order, bool forward, Position? from = null, bool inclusive = false, bool skipDeleted = false)
    {
        var pos = from;
        var incl = inclusive;
        var size = FirstBatchSize;
        ScanCache? c = null;
        int next = 0;
        // Continue from the cached batch when the scan starts right after one of its rows.
        if (from is { } p && !inclusive && FreshCache() is { } cached && cached.Tag == order?.Name && cached.Forward == forward
            && cached.SkipDeleted == skipDeleted && cached.Index.TryGetValue(p.RecNo, out var at) && (order == null || KeysEqual(cached.Rows[at].Key, p.Key)))
        {
            c = cached;
            next = at + 1;
            size = NextBatchSize(cached.Rows.Count);
        }
        while (true)
        {
            if (c == null)
            {
                var batch = FetchBatch(order, forward, pos, incl, skipDeleted, size);
                c = new ScanCache
                {
                    Tag = order?.Name, Forward = forward, SkipDeleted = skipDeleted, Rows = batch, Complete = batch.Count < size,
                    WriteVersion = Store.WriteVersion, DataVersion = Store.DataVersion(),
                };
                for (int i = 0; i < batch.Count; i++) c.Index[batch[i].Row.RecNo] = i;
                _scanCache = c;
                next = 0;
                incl = false;
                size = NextBatchSize(size);
            }
            while (next < c.Rows.Count)
            {
                var (row, key) = c.Rows[next++];
                pos = new Position(key, row.RecNo);
                yield return row;
                // The consumer wrote to the store (or another scan replaced the cache): read on from the database.
                if (_scanCache != c) break;
            }
            if (_scanCache == c && next >= c.Rows.Count && c.Complete) yield break;
            if (c.Rows.Count == 0) yield break;
            c = null;
        }
    }

    private static bool OutsideRange(byte[]? key, byte[]? a, byte[]? b)
    {
        if (key == null) return true; // excluded from the index (FOR clause)
        if (a == null || b == null) return false;
        var lo = a.AsSpan().SequenceCompareTo(b) <= 0 ? a : b;
        var hi = ReferenceEquals(lo, a) ? b : a;
        return key.AsSpan().SequenceCompareTo(lo) < 0 || key.AsSpan().SequenceCompareTo(hi) > 0;
    }

    private static bool KeysEqual(byte[]? a, byte[]? b) => a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

    /// <summary>After a single-row update: keeps the scan cache when the row's position in the cached order is unchanged.</summary>
    private void PatchCache(RowData row, byte[]?[] keys)
    {
        var c = _scanCache;
        if (c == null) return;
        if (!c.Index.TryGetValue(row.RecNo, out var i))
        {
            // A row outside the batch can only matter if the update moves it into the batch's range.
            bool keep;
            if (c.Tag == null) keep = !c.SkipDeleted || row.Deleted || c.Rows.Count == 0
                || row.RecNo < Math.Min(c.Rows[0].Row.RecNo, c.Rows[^1].Row.RecNo) || row.RecNo > Math.Max(c.Rows[0].Row.RecNo, c.Rows[^1].Row.RecNo);
            else
            {
                var t = Schema.Tags.FindIndex(x => x.Name == c.Tag);
                keep = t >= 0 && (c.Rows.Count == 0 || OutsideRange(keys[t], c.Rows[0].Key, c.Rows[^1].Key));
            }
            if (keep) c.WriteVersion = Store.WriteVersion; else _scanCache = null;
            return;
        }
        var cached = c.Rows[i];
        byte[]? newKey = null;
        if (c.Tag != null)
        {
            var t = Schema.Tags.FindIndex(x => x.Name == c.Tag);
            if (t < 0) { _scanCache = null; return; }
            newKey = keys[t];
            if (!KeysEqual(newKey, cached.Key)) { _scanCache = null; return; }
        }
        if (c.SkipDeleted && row.Deleted) { _scanCache = null; return; }
        c.Rows[i] = (row.Clone(), c.Tag != null ? newKey : cached.Key);
        c.WriteVersion = Store.WriteVersion;
    }

    private List<(RowData Row, byte[]? Key)> FetchBatch(TagDef? order, bool forward, Position? from, bool inclusive, bool skipDeleted, int size)
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
                  $" ORDER BY {orderBy} LIMIT {size}";
        using var r = Store.Prepared(sql, args.ToArray()).ExecuteReader();
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
        void Insert()
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
            var args = new List<(string, object?)>(Fields.Count + Schema.Tags.Count + 2) { ("$d", deleted ? 1 : 0) };
            for (int i = 0; i < Fields.Count; i++) args.Add(($"$v{i}", ValueCodec.ToDb(Fields[i], vals[i])));
            if (KeysUseRecno())
            {
                // Index keys that call RECNO() need the record number before the row is written.
                var recNo = RecordCount + 1;
                row = new RowData(this, recNo, deleted, vals, 1);
                for (int t = 0; t < Schema.Tags.Count; t++) args.Add(($"$k{t}", ComputeKey(Schema.Tags[t], row)));
                args.Add(("$r", recNo));
                ExecWrite(InsertSql(withRecno: true), args);
            }
            else
            {
                // SQLite assigns the next record number (MAX(_recno) + 1) atomically.
                var provisional = new RowData(this, 0, deleted, vals, 1);
                for (int t = 0; t < Schema.Tags.Count; t++) args.Add(($"$k{t}", ComputeKey(Schema.Tags[t], provisional)));
                var recNo = (int)(long)ExecWriteScalar(InsertSql(withRecno: false) + " RETURNING _recno", args)!;
                row = new RowData(this, recNo, deleted, vals, 1);
            }
        }
        // Autoincrement counters must move with the row; otherwise the INSERT is atomic on its own.
        if (Fields.Any(f => f.AutoIncNext != null)) Store.InTransaction(Insert);
        else Insert();
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
        var keys = new byte[]?[Schema.Tags.Count];
        for (int t = 0; t < Schema.Tags.Count; t++)
        {
            sets.Append($", \"{Schema.Tags[t].KeyColumn}\" = $k{t}");
            keys[t] = ComputeKey(Schema.Tags[t], row);
            args.Add(($"$k{t}", keys[t]));
        }
        ExecWrite($"UPDATE \"{SqlName}\" SET {sets} WHERE _recno = $r", args);
        row.RowVersion++;
        PatchCache(row, keys);
    }

    public void SetDeleted(int recNo, bool deleted)
    {
        var row = Read(recNo) ?? throw new VfpException(ErrorCodes.RecordOutOfRange, "Record is out of range.");
        if (row.Deleted == deleted) return;
        row.Deleted = deleted;
        Update(row);
    }

    private string? _insertSql, _insertSqlRecno;
    private int _sqlSchemaVersion = -1;
    private int _schemaVersion;

    private string InsertSql(bool withRecno)
    {
        if (_sqlSchemaVersion != _schemaVersion) { _insertSql = _insertSqlRecno = null; _sqlSchemaVersion = _schemaVersion; }
        ref var cached = ref withRecno ? ref _insertSqlRecno : ref _insertSql;
        if (cached != null) return cached;
        var cols = new StringBuilder("_deleted, _rowver");
        var ps = new StringBuilder("$d, 1");
        for (int i = 0; i < Fields.Count; i++) { cols.Append(", ").Append(Col(Fields[i])); ps.Append($", $v{i}"); }
        for (int t = 0; t < Schema.Tags.Count; t++) { cols.Append($", \"{Schema.Tags[t].KeyColumn}\""); ps.Append($", $k{t}"); }
        if (withRecno) { cols.Append(", _recno"); ps.Append(", $r"); }
        return cached = $"INSERT INTO \"{SqlName}\" ({cols}) VALUES ({ps})";
    }

    private bool KeysUseRecno() => Schema.Tags.Any(t => (t.Expression + " " + t.ForExpression).Contains("RECNO", StringComparison.OrdinalIgnoreCase));

    private object? ExecWriteScalar(string sql, List<(string, object?)> args)
    {
        try
        {
            return Store.ScalarPrepared(sql, args.ToArray());
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            var tag = Schema.Tags.FirstOrDefault(t => ex.Message.Contains(t.KeyColumn, StringComparison.OrdinalIgnoreCase));
            throw VfpException.UniqueViolation(tag?.Name ?? "?");
        }
    }

    private void ExecWrite(string sql, List<(string, object?)> args)
    {
        try
        {
            Store.ExecPrepared(sql, args.ToArray());
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

    /// <summary>Sets the next value of an autoincrement field (used when importing legacy tables).</summary>
    public void SetAutoIncrementNext(string field, long next)
    {
        var i = Schema.FieldIndex(field);
        if (i < 0 || Fields[i].AutoIncNext == null) return;
        Store.Exec("UPDATE _jp_fields SET autoinc_next=$n WHERE tbl=$t AND name=$f", ("$n", next), ("$t", Name), ("$f", Fields[i].Name));
        Schema.Fields[i] = Fields[i] with { AutoIncNext = next };
    }

    /// <summary>The records (not deleted) whose key for <paramref name="tag"/> (one of this table's tags) equals <paramref name="key"/>.</summary>
    public List<RowData> RowsWithKey(TagDef tag, Value key)
    {
        var keyBytes = EncodeKey(tag, key);
        var list = new List<RowData>();
        foreach (var row in Scan(tag, forward: true, new Position(keyBytes, int.MinValue), inclusive: true, skipDeleted: true))
        {
            var rowKey = ReadKey(tag, row.RecNo) ?? [];
            if (!rowKey.AsSpan().SequenceEqual(keyBytes)) break;
            if (!row.Deleted) list.Add(row);
        }
        return list;
    }

    /// <summary>Makes a field autoincrementing from <paramref name="next"/> (a rebuilt table gets its rows first, then its counters).</summary>
    public void EnableAutoIncrement(string field, long next, int step)
    {
        var i = Schema.FieldIndex(field);
        if (i < 0) return;
        Store.Exec("UPDATE _jp_fields SET autoinc_next=$n, autoinc_step=$s WHERE tbl=$t AND name=$f", ("$n", next), ("$s", step), ("$t", Name), ("$f", Fields[i].Name));
        Schema.Fields[i] = Fields[i] with { AutoIncNext = next, AutoIncStep = step };
    }

    /// <summary>Removes all records (ZAP).</summary>
    public void Zap() => Store.Exec($"DELETE FROM \"{SqlName}\"");

    // ---- Indexes ----------------------------------------------------------------------

    public void CreateTag(TagDef tag)
    {
        _schemaVersion++;
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
        _schemaVersion++;
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
