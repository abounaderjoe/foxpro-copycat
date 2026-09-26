using System.Data.Common;
using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>
/// The state of a cursor opened from a view: where its rows come from and how TABLEUPDATE sends
/// changes back (CURSORGETPROP/CURSORSETPROP read and change these per cursor).
/// </summary>
public sealed class ViewCursor : IDisposable
{
    public required string ViewName { get; init; }
    public required Store Database { get; init; }
    public required ViewDefinition Definition { get; init; }
    public bool Remote => Definition.Remote;
    public int? ConnectionHandle { get; set; }
    internal bool OwnsConnection { get; set; }
    internal Interpreter? Runtime { get; set; }

    public bool SendUpdates { get; set; }
    public List<string> Tables { get; set; } = new();
    public HashSet<string> KeyFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UpdatableFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>View field → "table.field" on the base table.</summary>
    public Dictionary<string, string> UpdateNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int WhereType { get; set; } = 3;
    public int UpdateType { get; set; } = 1;

    public void Dispose()
    {
        if (OwnsConnection && ConnectionHandle is { } h && Runtime != null)
        {
            try { Runtime.Remote.Disconnect(h); } catch (VfpException) { }
        }
        ConnectionHandle = null;
    }
}

/// <summary>Local and remote views: CREATE SQL VIEW, USE view, REQUERY() and updates to base tables.</summary>
internal static class Views
{
    // ---- Definition ------------------------------------------------------------------------------

    public static void Create(Interpreter rt, CreateViewStmt cv)
    {
        var db = rt.Session.CurrentDatabase ?? throw new VfpException(1520, "No database is open or set as the current database.");
        var name = rt.NameValue(cv.Name);
        var view = new ViewDefinition { Name = name, Sql = cv.Sql.Trim(), Remote = cv.Remote, ShareConnection = cv.Share };
        if (cv.Connection != null) view.Connection = rt.NameValue(cv.Connection);
        if (cv.Share) view.Set("ShareConnection", Value.True);

        // Validate the query and record where each column comes from, as the View Designer does.
        if (!view.Remote)
        {
            var query = ParseSelect(view.Sql);
            var tables = new List<string>();
            foreach (var (colName, table, field) in SqlEngine.ColumnSources(rt, query))
            {
                if (table == null || field == null) continue;
                view.SetField(colName, "UpdateName", Value.String(table + "." + field.ToLowerInvariant()));
                if (!tables.Contains(table, StringComparer.OrdinalIgnoreCase)) tables.Add(table);
            }
            view.Set("Tables", Value.String(string.Join(",", tables)));
        }
        else
        {
            var handle = rt.Remote.Connect(ConnectString(rt, view));
            try
            {
                var rc = rt.Remote.Get(handle);
                using var cmd = rc.Connection.CreateCommand();
                cmd.CommandText = rt.Remote.BindParameters(rc, view.Sql, cmd, missingAsNull: true);
                using var reader = cmd.ExecuteReader(System.Data.CommandBehavior.SchemaOnly);
                var schema = reader.GetSchemaTable();
                var tables = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string? baseTable = null, baseColumn = null;
                    if (schema != null && i < schema.Rows.Count)
                    {
                        var row = schema.Rows[i];
                        baseTable = schema.Columns.Contains("BaseTableName") ? row["BaseTableName"] as string : null;
                        baseColumn = schema.Columns.Contains("BaseColumnName") ? row["BaseColumnName"] as string : null;
                    }
                    if (string.IsNullOrEmpty(baseTable) || string.IsNullOrEmpty(baseColumn)) continue;
                    view.SetField(reader.GetName(i), "UpdateName", Value.String(baseTable + "." + baseColumn));
                    if (!tables.Contains(baseTable, StringComparer.OrdinalIgnoreCase)) tables.Add(baseTable);
                }
                view.Set("Tables", Value.String(string.Join(",", tables)));
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException) { throw SqlPassThrough.Connectivity(ex); }
            finally { rt.Remote.Disconnect(handle); }
        }
        db.SaveView(view);
    }

    public static SqlSelect ParseSelect(string sql)
    {
        var unit = Parser.ParseInteractive(sql);
        if (unit.Main.Count != 1 || unit.Main[0] is not SqlSelectStmt s)
            throw new VfpException(ErrorCodes.SyntaxError, "A view must be defined by a single SELECT statement.");
        if (s.Query.IntoKind != null) throw new VfpException(ErrorCodes.SyntaxError, "A view's SELECT cannot have an INTO clause.");
        return s.Query;
    }

    private static string ConnectString(Interpreter rt, ViewDefinition view)
    {
        if (string.IsNullOrEmpty(view.Connection)) throw new VfpException(1526, "Connectivity error: the view has no connection.");
        return rt.Session.NamedConnection(view.Connection) ?? "DSN=" + view.Connection;
    }

    // ---- Opening ---------------------------------------------------------------------------------

    public static WorkArea Open(Interpreter rt, Store db, ViewDefinition view, int area, string? alias, bool noData)
    {
        var state = new ViewCursor { ViewName = view.Name, Database = db, Definition = view, Runtime = rt };
        state.SendUpdates = view.Get("SendUpdates").AsBool;
        state.Tables = SplitList(view.Get("Tables").AsString);
        state.WhereType = (int)view.Get("WhereType").AsNumber;
        state.UpdateType = (int)view.Get("UpdateType").AsNumber;
        foreach (var (field, _) in view.FieldProperties)
        {
            if (view.GetField(field, "KeyField").AsBool) state.KeyFields.Add(field);
            if (view.GetField(field, "Updatable").AsBool) state.UpdatableFields.Add(field);
            var un = view.GetField(field, "UpdateName").AsString;
            if (un.Length > 0) state.UpdateNames[field] = un;
        }

        var cursorAlias = alias ?? view.Name;
        var session = rt.Session;
        session.Select(area);
        ResultSet set;
        try
        {
            set = Fetch(rt, state, noData);
        }
        catch
        {
            state.Dispose();
            throw;
        }
        var schema = new TableSchema(cursorAlias, set.Fields.Select(f => f with { Nullable = true }));
        var wa = session.CreateCursor(schema, area);
        foreach (var r in set.Rows) wa.Table.Append(Coerce(schema, r));
        wa.GoTop();
        wa.ViewState = state;
        wa.SetBuffering(3); // views use optimistic row buffering by default
        wa.UpdateHandler = (changes, force) => SendChanges(rt, wa, state, changes, force);
        return wa;
    }

    private static Value[] Coerce(TableSchema schema, Value[] r)
    {
        var row = new Value[schema.Fields.Count];
        for (int i = 0; i < row.Length; i++) row[i] = i < r.Length && !r[i].IsNull ? schema.Fields[i].Coerce(r[i]) : Value.Null;
        return row;
    }

    private static ResultSet Fetch(Interpreter rt, ViewCursor state, bool noData)
    {
        var view = state.Definition;
        if (!view.Remote)
        {
            var query = ParseSelect(view.Sql);
            if (noData) query = query with { Where = query.Where == null ? new LiteralExpr(Value.False) : new BinaryExpr("AND", new LiteralExpr(Value.False), query.Where) };
            var saved = rt.Session.CurrentAreaNumber;
            var result = SqlEngine.Execute(rt, query, materialize: false);
            rt.Session.Select(saved);
            var maxRecords = (int)view.Get("MaxRecords").AsNumber;
            var rows = maxRecords >= 0 ? result.Rows.Take(maxRecords).ToList() : result.Rows;
            rt.SetPublic("_TALLY", Value.Number(rows.Count));
            return new ResultSet(result.Fields, rows, 0);
        }

        if (state.ConnectionHandle == null)
        {
            var share = view.Get("ShareConnection").AsBool;
            if (share && rt.SharedViewConnections.TryGetValue(view.Connection ?? "", out var shared) && rt.Remote.Handles.Contains(shared))
                state.ConnectionHandle = shared;
            else
            {
                state.ConnectionHandle = rt.Remote.Connect(ConnectString(rt, view));
                if (share) rt.SharedViewConnections[view.Connection ?? ""] = state.ConnectionHandle.Value;
                else state.OwnsConnection = true;
            }
        }
        var rc = rt.Remote.Get(state.ConnectionHandle.Value);
        try
        {
            using var cmd = rc.Connection.CreateCommand();
            cmd.CommandText = rt.Remote.BindParameters(rc, view.Sql, cmd);
            cmd.Transaction = rc.Transaction;
            using var reader = cmd.ExecuteReader();
            var set = SqlPassThrough.Read(reader, noData ? 0 : (int)view.Get("MaxRecords").AsNumber, SqlPassThrough.DeclaredTypes(rc));
            rt.SetPublic("_TALLY", Value.Number(set.Rows.Count));
            return set;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { throw SqlPassThrough.Connectivity(ex); }
    }

    /// <summary>REQUERY(): runs the view's query again (picking up new parameter values) into the same cursor.</summary>
    public static int Requery(Interpreter rt, WorkArea wa)
    {
        if (wa.ViewState is not ViewCursor state) throw new VfpException(1491, "REQUERY() can only be used with a view.");
        if (wa.HasPendingChanges) throw new VfpException(1545, "Table buffer contains uncommitted changes.");
        var set = Fetch(rt, state, noData: false);
        var schema = wa.Table.Schema;
        wa.ReplaceRows(set.Rows.Select(r => Coerce(schema, r)));
        return 1;
    }

    // ---- Updates ---------------------------------------------------------------------------------

    private static List<string> SplitList(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static (string Table, string Field) SplitUpdateName(string un)
    {
        var dot = un.LastIndexOf('.');
        return dot > 0 ? (un[..dot].Trim(), un[(dot + 1)..].Trim()) : ("", un.Trim());
    }

    private static bool SameTable(string a, string b)
    {
        static string Bare(string t) => t.Contains('!') ? t[(t.IndexOf('!') + 1)..] : t;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || Bare(a).Equals(Bare(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sends buffered view changes to the base tables. Returns false on an update conflict.</summary>
    private static bool SendChanges(Interpreter rt, WorkArea wa, ViewCursor state, IReadOnlyList<BufferedChange> changes, bool force)
    {
        if (!state.SendUpdates) return true;
        if (state.Tables.Count == 0) throw new VfpException(1492, "No update tables are specified. Use the Tables cursor property.");
        if (state.KeyFields.Count == 0) throw new VfpException(1491, "No key columns are specified for the update table. Use the KeyFieldList cursor property.");
        var fields = wa.Table.Fields;

        // One plan per update table: which view columns map to which base columns.
        var plans = new List<(string Table, List<(int Index, string Column, bool Key, bool Updatable)> Cols)>();
        foreach (var table in state.Tables)
        {
            var cols = new List<(int, string, bool, bool)>();
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i].Name;
                var (t, col) = state.UpdateNames.TryGetValue(f, out var un) ? SplitUpdateName(un) : (table, f);
                if (t.Length > 0 && !SameTable(t, table)) continue;
                bool key = state.KeyFields.Contains(f), upd = state.UpdatableFields.Contains(f);
                if (key || upd) cols.Add((i, col, key, upd));
            }
            if (cols.Any(c => c.Item3)) plans.Add((table, cols));
        }
        if (plans.Count == 0) throw new VfpException(1491, "No key columns are specified for the update table. Use the KeyFieldList cursor property.");

        return state.Remote ? SendRemote(rt, state, plans, changes, force) : SendLocal(rt, state, plans, changes, force);
    }

    private static bool Changed(BufferedChange c, int i) => !VfpEquals(c.Original.Values[i], c.Current.Values[i]);

    private static bool VfpEquals(Value a, Value b) =>
        a.IsNull || b.IsNull ? a.IsNull == b.IsNull
            : a.Kind == ValueKind.Character && b.Kind == ValueKind.Character ? a.AsString.TrimEnd() == b.AsString.TrimEnd()
            : a.Equals(b);

    /// <summary>The columns compared in WHERE: keys, plus updatable (2) or modified (3) columns unless forced.</summary>
    private static IEnumerable<(int Index, string Column, bool Key, bool Updatable)> WhereColumns(
        List<(int Index, string Column, bool Key, bool Updatable)> cols, BufferedChange c, int whereType, bool force)
    {
        foreach (var col in cols)
        {
            if (col.Key) yield return col;
            else if (!force && col.Updatable && (whereType == 2 || (whereType == 3 && Changed(c, col.Index)))) yield return col;
        }
    }

    // Local views: generate UPDATE/INSERT/DELETE statements so base-table rules, triggers and RI apply.
    private static bool SendLocal(Interpreter rt, ViewCursor state, List<(string Table, List<(int Index, string Column, bool Key, bool Updatable)> Cols)> plans,
        IReadOnlyList<BufferedChange> changes, bool force)
    {
        var session = rt.Session;
        var tally = rt.GetVariable("_TALLY");
        var current = session.CurrentAreaNumber;
        session.BeginTransaction();
        bool ok = false;
        try
        {
            foreach (var c in changes)
            {
                if (c.IsInsert && c.Current.Deleted) continue; // appended then deleted: nothing to send
                foreach (var (table, cols) in plans)
                {
                    var tableExpr = new LiteralExpr(Value.String(table));
                    var where = BuildWhere(WhereColumns(cols, c, state.WhereType, force), c);
                    bool insert = c.IsInsert;
                    if (!insert && !c.IsDelete && state.UpdateType == 2 && cols.Any(x => x.Updatable && Changed(c, x.Index)))
                    {
                        // UpdateType 2: delete the old row, then insert the new one.
                        SqlEngine.Delete(rt, new SqlDeleteStmt(tableExpr, where));
                        if (!force && Tally(rt) == 0) return false;
                        insert = true;
                    }
                    if (insert)
                    {
                        var ins = cols.Where(x => x.Updatable || x.Key).ToList();
                        SqlEngine.Insert(rt, new SqlInsertStmt(tableExpr, ins.Select(x => x.Column).ToList(),
                            ins.Select(x => (Expr)new LiteralExpr(c.Current.Values[x.Index])).ToList(), null, null, null, null));
                        continue;
                    }
                    if (c.IsDelete)
                    {
                        SqlEngine.Delete(rt, new SqlDeleteStmt(tableExpr, where));
                        if (!force && Tally(rt) == 0) return false;
                        continue;
                    }
                    var sets = cols.Where(x => x.Updatable && Changed(c, x.Index))
                        .Select(x => (x.Column, (Expr)new LiteralExpr(c.Current.Values[x.Index]))).ToList();
                    if (sets.Count == 0) continue;
                    SqlEngine.Update(rt, new SqlUpdateStmt(tableExpr, sets, where));
                    if (!force && Tally(rt) == 0) return false;
                }
            }
            ok = true;
            return true;
        }
        finally
        {
            if (ok) session.EndTransaction(); else session.Rollback();
            session.Select(current);
            rt.SetPublic("_TALLY", tally);
        }
    }

    private static int Tally(Interpreter rt) => (int)rt.GetVariable("_TALLY").AsNumber;

    private static Expr? BuildWhere(IEnumerable<(int Index, string Column, bool Key, bool Updatable)> cols, BufferedChange c)
    {
        Expr? where = null;
        foreach (var col in cols)
        {
            var orig = c.Original.Values[col.Index];
            Expr term = orig.IsNull
                ? new IsNullExpr(new NameExpr(col.Column), false)
                : orig.Kind == ValueKind.Character
                    ? new BinaryExpr("==", new CallExpr("RTRIM", [new NameExpr(col.Column)]), new LiteralExpr(Value.String(orig.AsString.TrimEnd())))
                    : new BinaryExpr("=", new NameExpr(col.Column), new LiteralExpr(orig));
            where = where == null ? term : new BinaryExpr("AND", where, term);
        }
        return where;
    }

    // Remote views: parameterized native SQL on the view's connection.
    private static bool SendRemote(Interpreter rt, ViewCursor state, List<(string Table, List<(int Index, string Column, bool Key, bool Updatable)> Cols)> plans,
        IReadOnlyList<BufferedChange> changes, bool force)
    {
        var rc = rt.Remote.Get(state.ConnectionHandle ?? throw new VfpException(1466, "Connection handle is invalid."));
        var ownTx = rc.Transaction == null;
        var tx = rc.Transaction ?? rc.Connection.BeginTransaction();
        bool ok = false;
        try
        {
            foreach (var c in changes)
            {
                if (c.IsInsert && c.Current.Deleted) continue;
                foreach (var (table, cols) in plans)
                {
                    using var cmd = rc.Connection.CreateCommand();
                    cmd.Transaction = tx;
                    int n = 0;
                    string P(Value v)
                    {
                        var p = cmd.CreateParameter();
                        p.ParameterName = "@p" + ++n;
                        p.Value = RemoteValue(v);
                        cmd.Parameters.Add(p);
                        return rc.Provider.NamedParameters ? p.ParameterName : "?";
                    }
                    string Where(BufferedChange ch) => string.Join(" AND ", WhereColumns(cols, ch, state.WhereType, force)
                        .Select(x => ch.Original.Values[x.Index].IsNull ? $"{x.Column} IS NULL" : $"{x.Column} = {P(ch.Original.Values[x.Index])}"));

                    bool insert = c.IsInsert, delete = c.IsDelete;
                    if (!insert && !delete && state.UpdateType == 2 && cols.Any(x => x.Updatable && Changed(c, x.Index)))
                    {
                        // UpdateType 2: delete the old row, then insert the new one.
                        cmd.CommandText = $"DELETE FROM {table} WHERE {Where(c)}";
                        if (cmd.ExecuteNonQuery() == 0 && !force) return false;
                        cmd.Parameters.Clear();
                        n = 0;
                        insert = true;
                    }
                    if (insert)
                    {
                        var ins = cols.Where(x => x.Updatable || x.Key).ToList();
                        cmd.CommandText = $"INSERT INTO {table} ({string.Join(", ", ins.Select(x => x.Column))}) VALUES ({string.Join(", ", ins.Select(x => P(c.Current.Values[x.Index])))})";
                        cmd.ExecuteNonQuery();
                        continue;
                    }
                    if (delete)
                    {
                        cmd.CommandText = $"DELETE FROM {table} WHERE {Where(c)}";
                        if (cmd.ExecuteNonQuery() == 0 && !force) return false;
                        continue;
                    }
                    var sets = cols.Where(x => x.Updatable && Changed(c, x.Index)).ToList();
                    if (sets.Count == 0) continue;
                    var setText = string.Join(", ", sets.Select(x => $"{x.Column} = {P(c.Current.Values[x.Index])}"));
                    cmd.CommandText = $"UPDATE {table} SET {setText} WHERE {Where(c)}";
                    if (cmd.ExecuteNonQuery() == 0 && !force) return false;
                }
            }
            ok = true;
            return true;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { throw SqlPassThrough.Connectivity(ex); }
        finally
        {
            if (ownTx)
            {
                if (ok) tx.Commit(); else tx.Rollback();
                tx.Dispose();
            }
        }
    }

    // Character data is sent without FoxPro's trailing padding, so it matches VARCHAR/TEXT columns.
    private static object RemoteValue(Value v) => v.Kind == ValueKind.Character
        ? v.AsString.TrimEnd()
        : v.IsNull ? DBNull.Value : SqlPassThrough.ParameterValue(v);

    // ---- Properties ------------------------------------------------------------------------------

    /// <summary>CURSORGETPROP for view cursors; null when the property is not view-specific.</summary>
    public static Value? GetCursorProp(WorkArea wa, string prop)
    {
        if (wa.ViewState is not ViewCursor s) return null;
        return prop.ToUpperInvariant() switch
        {
            "SOURCETYPE" => Value.Number(s.Remote ? 2 : 1),
            "SOURCENAME" => Value.String(s.ViewName),
            "SQL" => Value.String(s.Definition.Sql),
            "SENDUPDATES" => Value.Logical(s.SendUpdates),
            "TABLES" => Value.String(string.Join(",", s.Tables)),
            "KEYFIELDLIST" => Value.String(string.Join(",", Ordered(wa, s.KeyFields))),
            "UPDATABLEFIELDLIST" => Value.String(string.Join(",", Ordered(wa, s.UpdatableFields))),
            "UPDATENAMELIST" => Value.String(string.Join(",", Ordered(wa, s.UpdateNames.Keys).Select(f => $"{f} {s.UpdateNames[f]}"))),
            "WHERETYPE" => Value.Number(s.WhereType),
            "UPDATETYPE" => Value.Number(s.UpdateType),
            "CONNECTHANDLE" => s.ConnectionHandle is { } h ? Value.Number(h) : Value.Number(0),
            "CONNECTNAME" => Value.String(s.Definition.Connection ?? ""),
            "DATABASE" => Value.String(s.Database.Path),
            "SHARECONNECTION" => Value.Logical(s.Definition.Get("ShareConnection").AsBool),
            "MAXRECORDS" => s.Definition.Get("MaxRecords"),
            "FETCHSIZE" => s.Definition.Get("FetchSize"),
            "BATCHUPDATECOUNT" => s.Definition.Get("BatchUpdateCount"),
            _ => null,
        };
    }

    private static IEnumerable<string> Ordered(WorkArea wa, IEnumerable<string> names)
    {
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return wa.Table.Fields.Select(f => f.Name).Where(set.Contains);
    }

    /// <summary>CURSORSETPROP for view cursors; false when the property is not view-specific.</summary>
    public static bool SetCursorProp(WorkArea wa, string prop, Value v)
    {
        if (wa.ViewState is not ViewCursor s) return false;
        switch (prop.ToUpperInvariant())
        {
            case "SENDUPDATES": s.SendUpdates = v.AsBool; return true;
            case "TABLES": s.Tables = SplitList(v.AsString); return true;
            case "KEYFIELDLIST": s.KeyFields = SplitList(v.AsString).ToHashSet(StringComparer.OrdinalIgnoreCase); return true;
            case "UPDATABLEFIELDLIST": s.UpdatableFields = SplitList(v.AsString).ToHashSet(StringComparer.OrdinalIgnoreCase); return true;
            case "UPDATENAMELIST":
                s.UpdateNames = new(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in SplitList(v.AsString))
                {
                    var parts = pair.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2) s.UpdateNames[parts[0]] = parts[1];
                }
                return true;
            case "WHERETYPE": s.WhereType = (int)v.AsNumber; return true;
            case "UPDATETYPE": s.UpdateType = (int)v.AsNumber; return true;
            default: return false;
        }
    }
}
