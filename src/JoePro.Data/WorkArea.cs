using JoePro.Core;

namespace JoePro.Data;

/// <summary>
/// A FoxPro work area: an open table (or cursor) with a record pointer, a controlling order,
/// an optional filter, relations and a change buffer.
/// </summary>
public sealed class WorkArea : IRecord
{
    private RowData? _current;
    // Buffered rows keyed by record number (appended rows use negative numbers, as in VFP).
    private readonly SortedDictionary<int, (RowData Original, RowData Current)> _buffer = new();
    /// <summary>Buffered fields SETFLDSTATE() marked as changed although their value is the original one (−1 = deletion status).</summary>
    private readonly HashSet<(int RecNo, int Field)> _forcedChanged = new();
    private int _nextNewRecNo = -1;
    private int _lastLocatePos;

    internal WorkArea(DataSession session, int number)
    {
        Session = session;
        Number = number;
    }

    public DataSession Session { get; }
    public int Number { get; }
    public string Alias { get; private set; } = "";
    public Table? TableOrNull { get; private set; }
    public Table Table => TableOrNull ?? throw VfpException.NoTableOpen();
    public bool InUse => TableOrNull != null;
    public bool Exclusive { get; private set; }
    public bool ReadOnly { get; set; }
    /// <summary>The source file or "database!table" name, for DBF().</summary>
    public string Source { get; private set; } = "";
    public TagDef? Order { get; private set; }
    public ICompiledExpression? Filter { get; private set; }
    public List<(ICompiledExpression Expr, WorkArea Child)> Relations { get; } = new();
    /// <summary>CURSORSETPROP("Buffering"): 1 none, 2/3 row (pessimistic/optimistic), 4/5 table.</summary>
    public int BufferMode { get; private set; } = 1;
    public bool IsCursor { get; private set; }
    /// <summary>Runtime state for a cursor opened from a view (definition, update properties, connection).</summary>
    public object? ViewState { get; set; }
    /// <summary>
    /// For views: sends buffered changes to the base tables before TABLEUPDATE commits them to the cursor.
    /// Receives the changes and the force flag; returns false on an update conflict.
    /// </summary>
    public Func<IReadOnlyList<BufferedChange>, bool, bool>? UpdateHandler { get; set; }

    public int RecNo { get; private set; }
    public bool Eof { get; private set; } = true;
    public bool Bof { get; private set; } = true;
    public bool Found { get; set; }
    public RowData? Current => _current;

    Table IRecord.Table => Table;
    bool IRecord.Deleted => _current?.Deleted ?? false;
    public bool Deleted => _current?.Deleted ?? false;

    private SetOptions Options => Session.Options;

    // ---- Opening and closing ----------------------------------------------------------

    internal void Open(Table table, string alias, string source, bool exclusive, bool isCursor)
    {
        Close();
        TableOrNull = table;
        Alias = alias.ToUpperInvariant();
        Source = source;
        Exclusive = exclusive;
        IsCursor = isCursor;
        GoTop();
    }

    public void Close()
    {
        if (TableOrNull == null) return;
        _buffer.Clear();
        _forcedChanged.Clear();
        Session.Locks.ReleaseAll(this);
        foreach (var other in Session.OpenWorkAreas()) other.Relations.RemoveAll(r => r.Child == this);
        TableOrNull = null;
        Alias = "";
        Order = null;
        Filter = null;
        Relations.Clear();
        BufferMode = 1;
        (ViewState as IDisposable)?.Dispose();
        ViewState = null;
        UpdateHandler = null;
        _current = null;
        RecNo = 0;
        Eof = Bof = true;
        Found = false;
    }

    // ---- Field access -----------------------------------------------------------------

    public int FieldIndex(string name) => TableOrNull?.Schema.FieldIndex(name) ?? -1;

    /// <summary>Field value of the current record. At end of file every field reads as a blank value.</summary>
    public Value Get(int fieldIndex)
    {
        var t = Table;
        if (_current == null) return t.Fields[fieldIndex].BlankValue();
        return _current.Values[fieldIndex];
    }

    public Value Get(string field)
    {
        var i = FieldIndex(field);
        if (i < 0) throw VfpException.FieldNotFound(field);
        return Get(i);
    }

    /// <summary>REPLACE on the current record (buffered or written through, depending on the buffer mode).</summary>
    public void Replace(IReadOnlyList<(int Field, Value Value)> assignments)
    {
        if (_current == null) throw VfpException.EndOfFile();
        if (ReadOnly) throw new VfpException(111, "Cannot update the cursor " + Alias + ", since it is read-only.");
        var t = Table;
        var row = BeginEdit();
        foreach (var (f, v) in assignments)
        {
            var fd = t.Fields[f];
            if (v.IsNull && !fd.Nullable && !Options.Null && !IsCursor)
                throw new VfpException(ErrorCodes.NullNotAllowed, $"Field {fd.Name} does not accept null values.");
            row.Values[f] = v.IsNull ? v : fd.Coerce(v);
        }
        EndEdit(row);
    }

    public void Replace(int field, Value value) => Replace([(field, value)]);

    private RowData BeginEdit()
    {
        if (BufferMode == 1) return _current!.Clone();
        if (!_buffer.TryGetValue(_current!.RecNo, out var entry))
        {
            if (BufferMode is 2 or 4 && !Session.Locks.TryLock(this, _current.RecNo))
                throw new VfpException(109, "Record is in use by another user.");
            entry = (_current.Clone(), _current.Clone());
            _buffer[_current.RecNo] = entry;
        }
        return entry.Current;
    }

    private void EndEdit(RowData row)
    {
        if (BufferMode == 1)
        {
            Table.Update(row);
            _current = row;
        }
        else
        {
            _current = row;
        }
    }

    public void AppendBlank(Value[]? values = null)
    {
        if (ReadOnly) throw new VfpException(111, "Cannot update the cursor " + Alias + ", since it is read-only.");
        CommitRowBufferIfLeaving();
        var t = Table;
        if (BufferMode == 1)
        {
            _current = t.Append(values);
        }
        else
        {
            var vals = new Value[t.Fields.Count];
            for (int i = 0; i < vals.Length; i++)
                vals[i] = values != null && i < values.Length ? t.Fields[i].Coerce(values[i]) : t.Fields[i].BlankValue();
            var row = new RowData(t, _nextNewRecNo--, false, vals);
            _buffer[row.RecNo] = (row.Clone(), row);
            _current = row;
        }
        RecNo = _current.RecNo;
        Eof = Bof = false;
        OnMoved();
    }

    public void Delete(bool deleted = true)
    {
        if (_current == null) return;
        var row = BeginEdit();
        row.Deleted = deleted;
        EndEdit(row);
    }

    // ---- Order, filter, buffering -----------------------------------------------------

    public void SetOrder(string? tagName, bool? descending = null)
    {
        if (tagName == null)
        {
            Order = null;
            return;
        }
        var tag = Table.Schema.FindTag(tagName) ?? throw new VfpException(1683, $"Index tag is not found.", tagName);
        if (descending is { } d) tag = tag with { Descending = d };
        Order = tag;
    }

    public void SetFilter(ICompiledExpression? filter) => Filter = filter;

    public void SetBuffering(int mode)
    {
        if (mode is < 1 or > 5) throw VfpException.InvalidArgument();
        if (_buffer.Count > 0 && mode != BufferMode)
            throw new VfpException(1545, "Table buffer contains uncommitted changes.");
        BufferMode = mode;
    }

    public bool HasPendingChanges => _buffer.Count > 0;

    /// <summary>TABLEUPDATE(): commits buffered changes. Returns false on an update conflict unless <paramref name="force"/>.</summary>
    public bool TableUpdate(bool allRows = true, bool force = false)
    {
        if (_buffer.Count == 0) return true;
        var t = Table;
        var entries = allRows || BufferMode is 2 or 3
            ? _buffer.ToList()
            : _buffer.Where(kv => kv.Key == _current?.RecNo).ToList();
        if (!force)
        {
            foreach (var (recNo, (orig, _)) in entries)
            {
                if (recNo > 0 && t.RowVersion(recNo) != orig.RowVersion) return false;
            }
        }
        if (UpdateHandler != null
            && !UpdateHandler(entries.Select(e => new BufferedChange(e.Key, e.Value.Original, e.Value.Current)).ToList(), force))
            return false;
        var renumbered = new Dictionary<int, int>();
        t.Store.InTransaction(() =>
        {
            foreach (var (recNo, (_, cur)) in entries)
            {
                if (recNo < 0)
                {
                    var added = t.Append(cur.Values, cur.Deleted);
                    renumbered[recNo] = added.RecNo;
                }
                else
                {
                    var toWrite = cur.Clone();
                    toWrite.RowVersion = t.RowVersion(recNo);
                    t.Update(toWrite);
                }
            }
        });
        foreach (var (recNo, _) in entries)
        {
            _buffer.Remove(recNo);
            _forcedChanged.RemoveWhere(f => f.RecNo == recNo);
            if (recNo > 0) Session.Locks.Release(this, recNo);
        }
        if (_current != null)
        {
            var rn = renumbered.TryGetValue(_current.RecNo, out var n) ? n : _current.RecNo;
            if (rn > 0) { _current = t.Read(rn); RecNo = rn; }
        }
        return true;
    }

    /// <summary>REQUERY(): replaces the cursor's rows, discarding buffered changes.</summary>
    public void ReplaceRows(IEnumerable<Value[]> rows)
    {
        _buffer.Clear();
        _forcedChanged.Clear();
        Session.Locks.ReleaseAll(this);
        var t = Table;
        t.Zap();
        foreach (var r in rows) t.Append(r);
        GoTop();
    }

    /// <summary>TABLEREVERT(): discards buffered changes and returns the number of records reverted.</summary>
    public int TableRevert(bool allRows = true)
    {
        var entries = allRows ? _buffer.Keys.ToList() : _buffer.Keys.Where(k => k == _current?.RecNo).ToList();
        foreach (var k in entries)
        {
            _buffer.Remove(k);
            _forcedChanged.RemoveWhere(f => f.RecNo == k);
            if (k > 0) Session.Locks.Release(this, k);
        }
        if (_current != null)
        {
            if (_current.RecNo < 0) GoTop();
            else _current = ReadOverlay(_current.RecNo);
        }
        return entries.Count;
    }

    /// <summary>OLDVAL(): the value before buffered edits.</summary>
    public Value OldVal(int field) =>
        _current != null && _buffer.TryGetValue(_current.RecNo, out var e) ? e.Original.Values[field] : Get(field);

    /// <summary>CURVAL(): the value currently stored on disk.</summary>
    public Value CurVal(int field)
    {
        if (_current == null || _current.RecNo < 0) return Get(field);
        return Table.Read(_current.RecNo)?.Values[field] ?? Get(field);
    }

    /// <summary>GETFLDSTATE(): 1 unchanged, 2 changed, 3 appended unchanged, 4 appended changed. Field 0 is the deletion status.</summary>
    public int GetFieldState(int field)
    {
        if (_current == null) return 1;
        if (!_buffer.TryGetValue(_current.RecNo, out var e)) return 1;
        bool changed = field < 0
            ? e.Original.Deleted != e.Current.Deleted
            : !e.Original.Values[field].Equals(e.Current.Values[field])
            || _forcedChanged.Contains((_current.RecNo, field < 0 ? -1 : field));
        return _current.RecNo < 0 ? (changed ? 4 : 3) : (changed ? 2 : 1);
    }

    /// <summary>SETFLDSTATE(): marks a buffered field (−1 = the deletion status) unchanged (1, 3) or changed (2, 4).</summary>
    public void SetFieldState(int field, int state)
    {
        if (_current == null) return;
        if (BufferMode == 1) throw new VfpException(1571, "SETFLDSTATE() requires row or table buffering.");
        var key = (_current.RecNo, field < 0 ? -1 : field);
        if (state is 1 or 3)
        {
            _forcedChanged.Remove(key);
            if (!_buffer.TryGetValue(_current.RecNo, out var e)) return;
            if (field < 0) e.Original.Deleted = e.Current.Deleted;
            else e.Original.Values[field] = e.Current.Values[field];
        }
        else if (state is 2 or 4)
        {
            BeginEdit();
            _forcedChanged.Add(key);
        }
        else throw new VfpException(ErrorCodes.InvalidArgument, "Function argument value, type, or count is invalid (SETFLDSTATE).");
    }

    public int GetNextModified(int afterRecNo)
    {
        foreach (var k in _buffer.Keys.OrderBy(k => k < 0 ? int.MaxValue + (long)k : k))
            if (afterRecNo == 0 || k > afterRecNo || k < 0) return k;
        return 0;
    }

    private void CommitRowBufferIfLeaving()
    {
        if (BufferMode is 2 or 3 && _buffer.Count > 0)
        {
            if (!TableUpdate(allRows: true)) throw new VfpException(1585, "Update conflict.");
        }
    }

    private RowData? ReadOverlay(int recNo)
    {
        if (_buffer.TryGetValue(recNo, out var e)) return e.Current;
        return Table.Read(recNo);
    }

    // ---- Navigation -------------------------------------------------------------------

    public int RecordCount => Table.RecordCount + _buffer.Keys.Count(k => k < 0);

    private bool IsVisible(RowData row)
    {
        if (Options.Deleted && row.Deleted) return false;
        if (Filter == null) return true;
        var f = Filter.Evaluate(row);
        return f.Kind == ValueKind.Logical && f.AsBool;
    }

    private IEnumerable<RowData> Rows(bool forward, Position? from, bool inclusive)
    {
        var t = Table;
        if (_buffer.Count == 0)
        {
            foreach (var row in t.Scan(Order, forward, from, inclusive, skipDeleted: Options.Deleted)) yield return row;
            yield break;
        }
        var appended = _buffer.Where(kv => kv.Key < 0).Select(kv => kv.Value.Current).OrderByDescending(r => r.RecNo).ToList();
        if (!forward)
        {
            // Buffered new rows sit after all stored rows in every order.
            if (from == null) foreach (var r in appended.AsEnumerable().Reverse()) yield return r;
            else if (from.Value.RecNo < 0)
            {
                foreach (var r in appended.AsEnumerable().Reverse())
                    if (inclusive ? r.RecNo >= from.Value.RecNo : r.RecNo > from.Value.RecNo) yield return r;
                from = null;
            }
        }
        if (forward && from is { RecNo: < 0 } p)
        {
            foreach (var r in appended)
                if (inclusive ? r.RecNo <= p.RecNo : r.RecNo < p.RecNo) yield return r;
            yield break;
        }
        foreach (var row in t.Scan(Order, forward, from, inclusive, skipDeleted: Options.Deleted))
        {
            yield return _buffer.TryGetValue(row.RecNo, out var e) ? e.Current : row;
        }
        if (forward) foreach (var r in appended) yield return r;
    }

    private Position? CurrentPosition()
    {
        if (_current == null) return null;
        if (_current.RecNo < 0 || Order == null) return new Position(null, _current.RecNo);
        var key = Table.ReadKey(Order, _current.RecNo);
        return key == null ? null : new Position(key, _current.RecNo);
    }

    private void SetCurrent(RowData? row, bool eof = false)
    {
        _current = row;
        if (row == null)
        {
            Eof = true;
            RecNo = (TableOrNull?.RecordCount ?? 0) + 1;
        }
        else
        {
            Eof = false;
            RecNo = row.RecNo;
        }
        OnMoved();
    }

    public void GoTop()
    {
        CommitRowBufferIfLeaving();
        var row = Rows(true, null, false).FirstOrDefault(IsVisible);
        SetCurrent(row);
        Bof = row == null;
    }

    public void GoBottom()
    {
        CommitRowBufferIfLeaving();
        var row = Rows(false, null, false).FirstOrDefault(IsVisible);
        SetCurrent(row);
        Bof = row == null;
    }

    public void Go(int recNo)
    {
        CommitRowBufferIfLeaving();
        var row = recNo is 0 ? null : ReadOverlay(recNo);
        if (row == null) throw new VfpException(ErrorCodes.RecordOutOfRange, "Record is out of range.");
        SetCurrent(row);
        Bof = false;
    }

    public void Skip(int n = 1)
    {
        if (n == 0)
        {
            if (_current != null) _current = ReadOverlay(_current.RecNo);
            return;
        }
        if (n > 0 && Eof) throw VfpException.EndOfFile();
        if (n < 0 && Bof) throw VfpException.BeginningOfFile();
        CommitRowBufferIfLeaving();
        var forward = n > 0;
        var pos = Eof ? null : CurrentPosition();
        if (!Eof && pos == null)
        {
            // Current record is not part of the controlling index (for example, excluded by a FOR clause).
            if (forward) { GoTop(); n--; if (n == 0 || Eof) return; pos = CurrentPosition(); }
            else { GoTop(); Bof = true; return; }
        }
        RowData? last = null;
        int remaining = Math.Abs(n);
        foreach (var row in Rows(forward, pos, inclusive: false))
        {
            if (!IsVisible(row)) continue;
            last = row;
            if (--remaining == 0) break;
        }
        if (remaining == 0)
        {
            SetCurrent(last);
            Bof = false;
        }
        else if (forward)
        {
            SetCurrent(null);
            Bof = false;
        }
        else
        {
            GoTop();
            Bof = true;
        }
    }

    /// <summary>
    /// Moves forward to the next visible record matching a SQL prefilter: from the current record when
    /// <paramref name="inclusive"/>, else after it. Reaching the end leaves the work area at EOF. Returns false, without
    /// moving, when the fast path does not apply (buffered changes pending); the caller then walks record by record.
    /// </summary>
    public bool MoveToCandidate(string where, (string, object?)[] args, bool inclusive = true)
    {
        if (_buffer.Count > 0 || TableOrNull == null) return false;
        if (Eof) return true;
        var recNo = _current!.RecNo;
        if (recNo < 0) return false;
        bool outsideIndex = false;
        Position? Where()
        {
            var p = CurrentPosition();
            outsideIndex = p == null; // not in the controlling index (a FOR-filtered tag): walk instead
            return p;
        }
        foreach (var row in Table.ScanWhere(Order, recNo, Where, inclusive, Options.Deleted, where, args))
        {
            if (!IsVisible(row)) continue;
            SetCurrent(row);
            Bof = false;
            return true;
        }
        if (outsideIndex) return false;
        SetCurrent(null);
        Bof = false;
        return true;
    }

    /// <summary>SEEK: positions on the first record whose index key matches <paramref name="key"/>.</summary>
    public bool Seek(Value key, TagDef? tag = null)
    {
        CommitRowBufferIfLeaving();
        tag ??= Order ?? throw new VfpException(26, "Table has no index order set.");
        var keyBytes = Table.EncodeKey(tag, key);
        var partial = key.Kind == ValueKind.Character && !Options.Exact;
        RowData? hit = null;
        bool found = false;
        foreach (var row in Table.Scan(tag, forward: true, new Position(keyBytes, int.MinValue), inclusive: true, skipDeleted: Options.Deleted))
        {
            var r = _buffer.TryGetValue(row.RecNo, out var e) ? e.Current : row;
            if (!IsVisible(r)) continue;
            var rowKey = Table.ReadKey(tag, r.RecNo) ?? [];
            found = partial ? KeyEncoder.StartsWith(rowKey, keyBytes) : rowKey.AsSpan().SequenceEqual(keyBytes);
            hit = r;
            break;
        }
        Found = found;
        if (found || (Options.Near && hit != null))
        {
            SetCurrent(hit);
            Bof = false;
        }
        else
        {
            SetCurrent(null);
            Bof = false;
        }
        return found;
    }

    /// <summary>Called after every record pointer move: repositions related child work areas.</summary>
    private void OnMoved()
    {
        foreach (var (expr, child) in Relations)
        {
            if (!child.InUse) continue;
            try
            {
                if (_current == null)
                {
                    child.SetCurrent(null);
                    continue;
                }
                var v = expr.Evaluate(this);
                if (child.Order != null) child.Seek(v);
                else if (v.Kind == ValueKind.Number && v.AsNumber >= 1 && v.AsNumber <= child.RecordCount) child.Go((int)v.AsNumber);
                else child.SetCurrent(null);
            }
            catch (VfpException)
            {
                child.SetCurrent(null);
            }
        }
    }

    // ---- Locking ----------------------------------------------------------------------

    public bool RLock(int? recNo = null) => Session.Locks.TryLock(this, recNo ?? RecNo);
    public bool FLock() => Session.Locks.TryLockTable(this);
    public void Unlock() => Session.Locks.ReleaseAll(this);
}

/// <summary>A buffered row change. RecNo &lt; 0 is an appended row; Current.Deleted marks a deletion.</summary>
public sealed record BufferedChange(int RecNo, RowData Original, RowData Current)
{
    public bool IsInsert => RecNo < 0;
    public bool IsDelete => Current.Deleted && !Original.Deleted;
}
