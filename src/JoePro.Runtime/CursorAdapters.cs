using JoePro.Core;
using JoePro.Data;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>
/// The CursorAdapter class (VFP 9): CursorFill, CursorRefresh, CursorAttach and CursorDetach. Its cursors
/// use the view machinery, so TABLEUPDATE() sends changes with the adapter's current update properties.
/// DataSourceType "NATIVE" (or empty) runs VFP SQL; "ODBC" runs native SQL on the SQL pass-through handle in
/// DataSource. ADO and XML need COM and are not supported.
/// </summary>
internal static class CursorAdapters
{
    private static string Str(VfpObject o, string p) => o.FindProperty(p)?.Value is { Kind: ValueKind.Character } v ? v.AsString.Trim() : "";
    private static bool Bool(VfpObject o, string p) => o.FindProperty(p)?.Value is { Kind: ValueKind.Logical } v && v.AsBool;
    private static int Int(VfpObject o, string p, int fallback) => o.FindProperty(p)?.Value is { Kind: ValueKind.Number } v ? (int)v.AsNumber : fallback;

    private static string AliasOf(VfpObject ca) => Str(ca, "Alias") is { Length: > 0 } a ? a : ca.Name;

    private static bool IsOdbc(VfpObject ca) => Str(ca, "DataSourceType").Equals("ODBC", StringComparison.OrdinalIgnoreCase);

    private static void CheckSourceType(VfpObject ca)
    {
        var t = Str(ca, "DataSourceType").ToUpperInvariant();
        if (t is not ("" or "NATIVE" or "ODBC"))
            throw VfpException.NotSupported($"CursorAdapter DataSourceType \"{t}\" (ADO and XML need COM)");
    }

    private static ViewCursor NewState(Interpreter rt, VfpObject ca)
    {
        CheckSourceType(ca);
        var def = new ViewDefinition { Name = AliasOf(ca), Sql = Str(ca, "SelectCmd"), Remote = IsOdbc(ca) };
        def.Set("MaxRecords", Value.Number(Int(ca, "MaxRecords", -1) is var m and > 0 ? m : -1));
        var state = new ViewCursor
        {
            ViewName = AliasOf(ca),
            Database = rt.Session.CurrentDatabase ?? rt.Session.CursorStore,
            Definition = def,
            Runtime = rt,
            Adapter = ca,
        };
        if (IsOdbc(ca))
        {
            if (ca.FindProperty("DataSource")?.Value is not { Kind: ValueKind.Number } h)
                throw new VfpException(1466, "CursorAdapter DataSource must be a SQL connection handle when DataSourceType is ODBC.");
            state.ConnectionHandle = (int)h.AsNumber;
        }
        SyncUpdateProperties(state, ca);
        return state;
    }

    /// <summary>Copies the adapter's update properties to its cursor (they may change after CursorFill).</summary>
    internal static void SyncUpdateProperties(ViewCursor state, VfpObject ca)
    {
        state.SendUpdates = Bool(ca, "SendUpdates");
        state.Tables = Views.SplitList(Str(ca, "Tables"));
        state.KeyFields = Views.SplitList(Str(ca, "KeyFieldList")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        state.UpdatableFields = Views.SplitList(Str(ca, "UpdatableFieldList")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        state.UpdateNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Views.SplitList(Str(ca, "UpdateNameList")))
        {
            var parts = pair.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) state.UpdateNames[parts[0]] = parts[1];
        }
        state.WhereType = Int(ca, "WhereType", 3);
        state.UpdateType = Int(ca, "UpdateType", 1);
    }

    internal static void SyncSelect(ViewCursor state, VfpObject ca) => state.Definition.Sql = Str(ca, "SelectCmd");

    internal static void CheckAllowed(VfpObject ca, IReadOnlyList<BufferedChange> changes)
    {
        foreach (var c in changes)
        {
            if (c.IsInsert && !c.Current.Deleted && !Bool(ca, "AllowInsert")) throw new VfpException(1491, "The CursorAdapter does not allow inserts (AllowInsert = .F.).");
            if (c.IsDelete && !c.IsInsert && !Bool(ca, "AllowDelete")) throw new VfpException(1491, "The CursorAdapter does not allow deletes (AllowDelete = .F.).");
            if (!c.IsInsert && !c.IsDelete && !Bool(ca, "AllowUpdate")) throw new VfpException(1491, "The CursorAdapter does not allow updates (AllowUpdate = .F.).");
        }
    }

    /// <summary>CursorFill([lUseCursorSchema] [, lNoData] [, nOptions] [, Source]).</summary>
    public static Value CursorFill(Interpreter rt, VfpObject ca, List<Interpreter.Arg> args)
    {
        Value A(int i) => i < args.Count ? args[i].Value : Value.False;
        bool useSchema = args.Count > 0 && A(0).Kind == ValueKind.Logical ? A(0).AsBool : Bool(ca, "UseCursorSchema");
        bool noData = args.Count > 1 && A(1).Kind == ValueKind.Logical ? A(1).AsBool : Bool(ca, "NoData");
        var select = Str(ca, "SelectCmd");
        var eventArgs = new List<Interpreter.Arg> { new(Value.Logical(useSchema), null), new(Value.Logical(noData), null), new(Value.String(select), null) };
        if (rt.RaiseEvent(ca, "BeforeCursorFill", eventArgs) is { Kind: ValueKind.Logical } go && !go.AsBool) return Value.False;
        bool ok;
        try
        {
            Fill(rt, ca, useSchema, noData);
            ok = true;
        }
        catch (VfpException ex)
        {
            rt.RecordError(ex);
            ok = false;
        }
        eventArgs.Add(new Interpreter.Arg(Value.Logical(ok), null));
        rt.RaiseEvent(ca, "AfterCursorFill", eventArgs);
        return Value.Logical(ok);
    }

    private static void Fill(Interpreter rt, VfpObject ca, bool useSchema, bool noData)
    {
        rt.InSessionOf(ca, () =>
        {
            var session = rt.Session;
            var state = NewState(rt, ca);
            var alias = AliasOf(ca);
            var existing = session.FindAlias(alias);
            var area = existing?.Number ?? (session.Current.InUse ? session.FreeArea() : session.CurrentAreaNumber);
            var set = Views.Fetch(rt, state, noData);
            var fields = set.Fields.Select(f => f with { Nullable = true }).ToList();
            if (useSchema && Str(ca, "CursorSchema") is { Length: > 0 } schemaText) fields = ParseSchema(schemaText);
            var schema = new TableSchema(alias, fields);
            session.Select(area);
            var wa = session.CreateCursor(schema, area);
            foreach (var r in set.Rows)
            {
                var row = new Value[schema.Fields.Count];
                for (int i = 0; i < row.Length; i++)
                    row[i] = i < r.Length && !r[i].IsNull ? CoerceLoose(schema.Fields[i], r[i]) : Value.Null;
                wa.Table.Append(row);
            }
            wa.GoTop();
            AttachState(rt, wa, state, ca);
        });
    }

    private static Value CoerceLoose(FieldDef f, Value v)
    {
        try { return f.Coerce(v); }
        catch (VfpException) { return f.Type is 'C' or 'M' or 'V' ? f.Coerce(Value.String(Formatter.ToDisplay(v, new SetOptions()).Trim())) : Value.Null; }
    }

    private static List<FieldDef> ParseSchema(string text)
    {
        var unit = Parser.ParseInteractive($"CREATE CURSOR __schema ({text})");
        if (unit.Main.Count != 1 || unit.Main[0] is not CreateTableStmt ct) throw new VfpException(ErrorCodes.SyntaxError, "CursorSchema is invalid.");
        return ct.Fields.Where(f => !f.Name.StartsWith("__CONSTRAINT:")).Select(f => Interpreter.ToFieldDef(f) with { Nullable = true }).ToList();
    }

    private static void AttachState(Interpreter rt, WorkArea wa, ViewCursor state, VfpObject ca)
    {
        wa.ViewState = state;
        var mode = Int(ca, "BufferModeOverride", 5);
        wa.SetBuffering(mode is >= 2 and <= 5 ? mode : 3);
        wa.UpdateHandler = (changes, force) => Views.SendChanges(rt, wa, state, changes, force);
        ca.Set("CursorStatus", Value.Number(1));
    }

    /// <summary>CursorRefresh(): runs SelectCmd again into the same cursor.</summary>
    public static Value CursorRefresh(Interpreter rt, VfpObject ca)
    {
        if (rt.RaiseEvent(ca, "BeforeCursorRefresh", [new Interpreter.Arg(Value.String(Str(ca, "SelectCmd")), null)]) is { Kind: ValueKind.Logical } go && !go.AsBool) return Value.False;
        bool ok = false;
        try
        {
            rt.InSessionOf(ca, () =>
            {
                var wa = rt.Session.FindAlias(AliasOf(ca)) ?? throw VfpException.AliasNotFound(AliasOf(ca));
                Views.Requery(rt, wa);
            });
            ok = true;
        }
        catch (VfpException ex) { rt.RecordError(ex); }
        rt.RaiseEvent(ca, "AfterCursorRefresh", [new Interpreter.Arg(Value.String(Str(ca, "SelectCmd")), null), new Interpreter.Arg(Value.Logical(ok), null)]);
        return Value.Logical(ok);
    }

    /// <summary>CursorAttach([cAlias] [, lInheritCursorProperties]): links an existing cursor to the adapter.</summary>
    public static Value CursorAttach(Interpreter rt, VfpObject ca, List<Interpreter.Arg> args)
    {
        var alias = args.Count > 0 && args[0].Value.Kind == ValueKind.Character ? args[0].Value.AsString.Trim() : AliasOf(ca);
        var ok = false;
        rt.InSessionOf(ca, () =>
        {
            if (rt.Session.FindAlias(alias) is not { } wa) return;
            ca.Set("Alias", Value.String(wa.Alias));
            AttachState(rt, wa, NewState(rt, ca), ca);
            ok = true;
        });
        return Value.Logical(ok);
    }

    /// <summary>CursorDetach(): the cursor stays open but no longer belongs to the adapter.</summary>
    public static Value CursorDetach(Interpreter rt, VfpObject ca)
    {
        rt.InSessionOf(ca, () =>
        {
            if (rt.Session.FindAlias(AliasOf(ca)) is { } wa && wa.ViewState is ViewCursor vc && vc.Adapter == ca)
            {
                wa.ViewState = null;
                wa.UpdateHandler = null;
            }
        });
        ca.Set("CursorStatus", Value.Number(0));
        return Value.True;
    }
}
