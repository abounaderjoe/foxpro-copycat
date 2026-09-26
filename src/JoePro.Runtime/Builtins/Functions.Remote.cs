using JoePro.Core;

namespace JoePro.Runtime.Builtins;

public static partial class Library
{
    /// <summary>
    /// Runs a pass-through call. Connectivity failures (error 1526) return -1 and are kept for AERROR(),
    /// as in VFP; other errors (bad handle, bad arguments) are raised.
    /// </summary>
    private static Value Remote(CallContext c, Func<SqlPassThrough, int> body, int handle = 0)
    {
        try { return N(body(c.Rt.Remote)); }
        catch (VfpException ex) when (ex.Number == 1526)
        {
            if (handle > 0) ex.Data["Handle"] = handle;
            c.Rt.RecordError(ex);
            return N(-1);
        }
    }

    private static void RegisterRemote()
    {
        Add("SQLSTRINGCONNECT", c => c.Has(0) && c[0].Kind == ValueKind.Character
            ? Remote(c, r => r.Connect(c.Str(0)))
            : throw VfpException.NotSupported("SQLSTRINGCONNECT() without a connection string (the Select Data Source dialog)"));
        Add("SQLCONNECT", c =>
        {
            if (!c.Has(0)) throw VfpException.NotSupported("SQLCONNECT() without a data source (the Select Connection dialog)");
            var name = c.Str(0).Trim();
            if (!c.Has(1) && c.Rt.Session.NamedConnection(name) is { } cs) return Remote(c, r => r.Connect(cs));
            var connect = $"DSN={name}";
            if (c.Has(1)) connect += $";UID={c.Str(1)}";
            if (c.Has(2)) connect += $";PWD={c.Str(2)}";
            return Remote(c, r => r.Connect(connect));
        });
        Add("SQLDISCONNECT", c => N(c.Rt.Remote.Disconnect(c.Int(0))));
        Add("SQLEXEC", c =>
        {
            var handle = c.Int(0);
            string? sql = c.Has(1) ? c.Str(1) : null;
            string? cursor = c.Has(2) ? c.Str(2) : null;
            Action<List<(string Alias, int Count)>>? info = null;
            if (c.Has(3))
                info = counts =>
                {
                    var arr = c.NewArray(3, Math.Max(1, counts.Count), 2);
                    for (int i = 0; i < counts.Count; i++) { arr[i + 1, 1] = S(counts[i].Alias); arr[i + 1, 2] = N(counts[i].Count); }
                };
            return Remote(c, r => r.Exec(handle, sql, cursor, info), handle);
        });
        Add("SQLPREPARE", c =>
        {
            var rc = c.Rt.Remote.Get(c.Int(0));
            rc.Prepared = (c.Str(1), c.Has(2) ? c.Str(2) : null);
            return N(1);
        });
        Add("SQLMORERESULTS", c => { var h = c.Int(0); return Remote(c, r => r.MoreResults(h), h); });
        Add("SQLCOMMIT", c => { var h = c.Int(0); return Remote(c, r => r.Commit(h), h); });
        Add("SQLROLLBACK", c => { var h = c.Int(0); return Remote(c, r => r.Rollback(h), h); });
        Add("SQLCANCEL", c => { c.Rt.Remote.Get(c.Int(0)); return N(1); });
        Add("SQLIDLEDISCONNECT", c => N(1));
        Add("SQLTABLES", c =>
        {
            var h = c.Int(0);
            return Remote(c, r => r.Tables(h, c.Has(1) ? c.Str(1) : null, c.Has(2) ? c.Str(2) : null), h);
        });
        Add("SQLCOLUMNS", c =>
        {
            var h = c.Int(0);
            var native = c.Has(2) && c.Str(2).Trim().Equals("NATIVE", StringComparison.OrdinalIgnoreCase);
            return Remote(c, r => r.Columns(h, c.Str(1), native, c.Has(3) ? c.Str(3) : null), h);
        });
        Add("SQLGETPROP", c =>
        {
            var h = c.Int(0);
            var prop = c.Str(1).Trim();
            var props = h == 0 ? c.Rt.Remote.Defaults : c.Rt.Remote.Get(h).Properties;
            if (prop.Equals("ODBChdbc", StringComparison.OrdinalIgnoreCase) || prop.Equals("ODBChstmt", StringComparison.OrdinalIgnoreCase)) return N(0);
            if (h != 0 && prop.Equals("DataSource", StringComparison.OrdinalIgnoreCase)) return S(c.Rt.Remote.Get(h).Connection.DataSource);
            return props.TryGetValue(prop, out var v) ? v : throw new VfpException(1466, $"Connection property {prop.ToUpperInvariant()} is invalid.");
        });
        Add("SQLSETPROP", c =>
        {
            var h = c.Int(0);
            var prop = c.Str(1).Trim();
            var props = h == 0 ? c.Rt.Remote.Defaults : c.Rt.Remote.Get(h).Properties;
            if (prop.Equals("ConnectString", StringComparison.OrdinalIgnoreCase) && h != 0) throw new VfpException(1466, "ConnectString is read-only.");
            props[prop] = c.Has(2) ? c[2] : Value.False;
            return N(1);
        });
        Add("ASQLHANDLES", c =>
        {
            var handles = c.Rt.Remote.Handles.ToList();
            var arr = c.NewArray(0, Math.Max(1, handles.Count), 0);
            for (int i = 0; i < handles.Count; i++) arr[i + 1] = N(handles[i]);
            return N(handles.Count);
        });
    }
}
