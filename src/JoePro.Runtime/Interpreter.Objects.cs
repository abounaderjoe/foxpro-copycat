using JoePro.Data;
using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime;

public sealed partial class Interpreter
{
    private VfpObject? _screen;
    private VfpObject? _application;
    private readonly HashSet<(int, string)> _inAccessor = new();

    // ================================================================================
    // Classes
    // ================================================================================

    public ClassInfo ResolveClass(string name, ProgramUnit? module = null)
    {
        if (module == null && _classes.TryGetValue(name, out var cached)) return cached;
        IEnumerable<ProgramUnit> units = module != null ? [module, .. SearchUnits()] : SearchUnits();
        foreach (var u in units)
        {
            if (!u.Classes.TryGetValue(name, out var def)) continue;
            if (string.Equals(def.Parent, def.Name, StringComparison.OrdinalIgnoreCase))
                throw new VfpException(1733, $"Class definition {name.ToUpperInvariant()} is recursive.");
            var parentModule = module;
            if (def.ParentLib != null)
                parentModule = LoadClassFile(ResolveClassFile(def.ParentLib, ".jpclass", ".vcx", u.File)
                    ?? throw VfpException.FileNotFound(def.ParentLib));
            var parent = ResolveClass(def.Parent, parentModule);
            var info = new ClassInfo(def.Name, parent.BaseClass, parent, def, u) { Library = u.File };
            _classes[name] = info;
            return info;
        }
        if (BaseClasses.Exists(name))
        {
            var canonical = BaseClasses.Canonical(name);
            var info = new ClassInfo(canonical, canonical, null, null, null);
            _classes[name] = info;
            return info;
        }
        throw new VfpException(1733, $"Class definition {name.ToUpperInvariant()} is not found.", name);
    }

    /// <summary>True if <paramref name="name"/> is a user class or a native base class (otherwise CREATEOBJECT tries COM/.NET).</summary>
    internal bool ClassExists(string name)
    {
        if (_classes.ContainsKey(name) || BaseClasses.Exists(name)) return true;
        return SearchUnits().Any(u => u.Classes.ContainsKey(name));
    }

    internal VfpObject CreateObjectByName(string className, List<Arg> args, ProgramUnit? module = null) =>
        CreateObject(ResolveClass(className, module), args) ?? throw new VfpException(1733, "Object could not be created.");

    /// <summary>
    /// Instantiates a class. Order follows VFP: native defaults, class-level property values (base → derived),
    /// Load (forms), member objects, then Init innermost-first with the container's Init last.
    /// </summary>
    internal VfpObject? CreateObject(ClassInfo cls, List<Arg> args, VfpObject? parent = null, string? name = null, bool noInit = false)
    {
        var o = Build(cls, parent, name);
        if (noInit) return o;
        return InitTree(o, args) ? o : null;
    }

    private VfpObject Build(ClassInfo cls, VfpObject? parent, string? name)
    {
        var o = new VfpObject(cls) { Parent = parent };
        BaseClasses.InitializeNative(o, cls.BaseClass);
        if (!cls.BaseClass.Equals("Empty", StringComparison.OrdinalIgnoreCase))
        {
            o.Set("Class", Value.String(cls.Name));
            o.Set("Name", Value.String(name ?? cls.Name));
            o.Set("ParentClass", Value.String(cls.Parent?.Name ?? ""));
            o.Set("ClassLibrary", Value.String(cls.Library ?? ""));
        }

        var saved = _frame;
        var savedSession = Session;
        _frame = new Frame(cls.Name.ToUpperInvariant() + ".INIT", saved) { This = o, Unit = cls.Unit ?? saved.Unit };
        try
        {
            var levels = cls.Hierarchy().Reverse().Where(l => l.Definition != null).ToList();
            var prebuilt = new HashSet<AddObjectDef>(ReferenceEqualityComparer.Instance);
            // Pass 1: property values.
            foreach (var level in levels)
            {
                foreach (var m in level.Definition!.Members)
                {
                    if (m.Name.Contains('.')) continue;
                    if (m.Dims != null)
                    {
                        var dims = m.Dims.Select(d => (int)Eval(d).AsNumber).ToList();
                        var v = o.FindProperty(m.Name) ?? new Variable(m.Name, Value.False);
                        v.Array = new VfpArray(dims[0], dims.Count > 1 ? dims[1] : 0);
                        o.Properties[m.Name] = v;
                    }
                    else o.Set(m.Name, Eval(m.Value!));
                }
            }
            // Forms: private data session, then Load (before any member object exists).
            if (IsFormClass(o))
            {
                if (o.FindProperty("DataSession")?.Value is { Kind: ValueKind.Number } ds && ds.AsNumber == 2)
                {
                    var session = new DataSession(Options.Clone(), this);
                    RegisterSession(session);
                    _formSessions[o] = session;
                    o.Set("DataSessionId", Value.Number(session.Id));
                    Session = session;
                }
                else o.Set("DataSessionId", Value.Number(Session.Id));
                // The data environment (and its cursors) exists and opens its tables before Load, as in VFP.
                // In design mode (Form Designer) objects are built but no event code runs and no tables open.
                var deName = levels.SelectMany(l => l.Definition!.Objects.Select(ao => (ao, l)))
                    .Where(x => !x.ao.Name.Contains('.') && MemberClass(x.ao, x.l).BaseClass.Equals("DataEnvironment", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.ao.Name).LastOrDefault();
                if (deName != null)
                {
                    foreach (var level in levels)
                        foreach (var ao in level.Definition!.Objects.Where(ao => IsUnder(ao.Name, deName)))
                        {
                            BuildMember(o, ao, level);
                            prebuilt.Add(ao);
                        }
                    var de = o.FindProperty(deName)!.Value.AsObject as VfpObject;
                    if (de != null && !DesignMode)
                    {
                        RaiseEvent(de, "BeforeOpenTables", []);
                        if (de.FindProperty("AutoOpenTables")?.Value is not { Kind: ValueKind.Logical } auto || auto.AsBool)
                            InvokeMethod(de, "OpenTables", []);
                    }
                }
                if (!DesignMode)
                {
                    var loaded = RaiseEvent(o, "Load", []);
                    if (loaded.Kind == ValueKind.Logical && !loaded.AsBool) o.Set("__LoadFailed", Value.True);
                }
            }
            SyncAutoChildren(o);
            // Pass 2: member objects (their Init runs later, in InitTree).
            foreach (var level in levels)
            {
                foreach (var ao in level.Definition!.Objects)
                    if (!prebuilt.Contains(ao)) BuildMember(o, ao, level);
                foreach (var m in level.Definition.Members.Where(m => m.Name.Contains('.'))) SetPath(o, m.Name, Eval(m.Value!));
            }
        }
        finally
        {
            _frame = saved;
            Session = savedSession;
        }
        return o;
    }

    private static bool IsUnder(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>The class of an ADD OBJECT member: from its OF library when given, else from the defining unit.</summary>
    private ClassInfo MemberClass(AddObjectDef ao, ClassInfo level)
    {
        if (ao.ClassLib == null) return ResolveClass(ao.Class, level.Unit);
        var lib = ResolveClassFile(ao.ClassLib, ".jpclass", ".vcx", level.Unit?.File) ?? throw VfpException.FileNotFound(ao.ClassLib);
        return ResolveClass(ao.Class, LoadClassFile(lib));
    }

    /// <summary>
    /// Builds one ADD OBJECT member. A member that replaces an existing (automatic) child keeps its position.
    /// Plain properties are set first, then automatic children are created (PageCount, ColumnCount…), then
    /// properties of those children (Page1.Caption).
    /// </summary>
    private void BuildMember(VfpObject o, AddObjectDef ao, ClassInfo level)
    {
        var path = ao.Name.Split('.');
        var container = o;
        for (int i = 0; i < path.Length - 1; i++)
            container = container.FindProperty(path[i])?.Value is { Kind: ValueKind.Object } cv ? (VfpObject)cv.AsObject : throw VfpObject.PropertyNotFound(path[i]);
        var childClass = MemberClass(ao, level);
        var existing = container.FindProperty(path[^1])?.Value is { Kind: ValueKind.Object } ev ? (VfpObject)ev.AsObject : null;
        int position = existing != null ? container.Members.IndexOf(existing) : -1;
        if (existing != null) container.Members.Remove(existing);
        var child = Build(childClass, container, path[^1]);
        child.SkipInit = ao.NoInit;
        foreach (var (prop, expr) in ao.Properties.Where(p => !p.Prop.Contains('.'))) SetPath(child, prop, Eval(expr));
        SyncAutoChildren(child);
        foreach (var (prop, expr) in ao.Properties.Where(p => p.Prop.Contains('.'))) SetPath(child, prop, Eval(expr));
        if (position >= 0) container.Members.Insert(position, child); else container.Members.Add(child);
        container.Set(path[^1], Value.Object(child));
    }

    /// <summary>Fires Init for member objects (innermost first), then for the object. A member whose Init returns .F. is removed.</summary>
    private bool InitTree(VfpObject o, List<Arg> args)
    {
        if (o.FindProperty("__LoadFailed") != null)
        {
            ReleaseForm(o);
            return false;
        }
        foreach (var m in o.Members.ToList())
        {
            if (m.SkipInit) continue;
            if (!InitTree(m, []))
            {
                o.Members.Remove(m);
                o.Properties.Remove(m.Name);
            }
        }
        var ok = RaiseEvent(o, "Init", args);
        if (ok.Kind == ValueKind.Logical && !ok.AsBool)
        {
            if (IsFormClass(o)) ReleaseForm(o);
            return false;
        }
        return true;
    }

    private readonly Dictionary<VfpObject, DataSession> _formSessions = new(ReferenceEqualityComparer.Instance);

    internal static bool IsToolbar(VfpObject o) => o.Class.BaseClass.Equals("Toolbar", StringComparison.OrdinalIgnoreCase);

    private sealed class HyperlinkState
    {
        public List<string> Urls { get; } = new();
        public int Index { get; set; } = -1;
    }

    private readonly Dictionary<VfpObject, HyperlinkState> _hyperlinkHistory = new(ReferenceEqualityComparer.Instance);
    private HyperlinkState HyperlinkHistory(VfpObject o)
    {
        if (!_hyperlinkHistory.TryGetValue(o, out var state)) _hyperlinkHistory[o] = state = new();
        return state;
    }

    /// <summary>Opens a URL or file in the system's default application (Hyperlink.NavigateTo). Hosts and tests may replace it.</summary>
    public Action<string> UrlLauncher { get; set; } = url =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) { }
    };

    private void OpenUrl(string url)
    {
        if (url.Length == 0) throw VfpException.InvalidArgument();
        UrlLauncher(url);
    }

    internal static bool IsFormClass(VfpObject o) =>
        o.Class.BaseClass.Equals("Form", StringComparison.OrdinalIgnoreCase) || o.Class.BaseClass.Equals("FormSet", StringComparison.OrdinalIgnoreCase);

    /// <summary>The form that contains <paramref name="o"/> (or <paramref name="o"/> itself), if any.</summary>
    public static VfpObject? OwningForm(VfpObject? o)
    {
        for (; o != null; o = o.Parent)
            if (IsFormClass(o)) return o;
        return null;
    }

    /// <summary>The data session an object's code runs in (a form's private session, or the current one).</summary>
    public DataSession SessionFor(VfpObject? o)
    {
        var form = OwningForm(o);
        return form != null && _formSessions.TryGetValue(form, out var s) ? s : Session;
    }

    /// <summary>Runs <paramref name="action"/> with the data session of the form that owns <paramref name="o"/> selected.</summary>
    internal void InSessionOf(VfpObject o, Action action)
    {
        var saved = Session;
        Session = SessionFor(o);
        try { action(); }
        finally { Session = saved; }
    }

    /// <summary>Runs <paramref name="action"/> in the data session of the form that owns <paramref name="o"/>.</summary>
    public T InObjectContext<T>(VfpObject o, Func<T> action)
    {
        var saved = Session;
        Session = SessionFor(o);
        var savedFrame = _frame;
        _frame = new Frame(o.Name.ToUpperInvariant(), savedFrame) { This = o, Unit = o.Class.Unit ?? savedFrame.Unit };
        try { return action(); }
        finally
        {
            _frame = savedFrame;
            Session = saved;
        }
    }

    /// <summary>Creates the implicit children of groups, page frames and grids (Option1…, Page1…, Column1…).</summary>
    internal void SyncAutoChildren(VfpObject o)
    {
        string? prefix = null, childClass = null, countProp = null;
        switch (o.Class.BaseClass.ToUpperInvariant())
        {
            case "PAGEFRAME": (prefix, childClass, countProp) = ("Page", "Page", "PageCount"); break;
            case "OPTIONGROUP": (prefix, childClass, countProp) = ("Option", "OptionButton", "ButtonCount"); break;
            case "COMMANDGROUP": (prefix, childClass, countProp) = ("Command", "CommandButton", "ButtonCount"); break;
            case "GRID": (prefix, childClass, countProp) = ("Column", "Column", "ColumnCount"); break;
        }
        if (prefix == null) return;
        var count = o.FindProperty(countProp!)?.Value is { Kind: ValueKind.Number } n ? (int)n.AsNumber : 0;
        var existing = o.Members.Where(m => m.Class.BaseClass.Equals(childClass, StringComparison.OrdinalIgnoreCase)).ToList();
        for (int i = existing.Count + 1; i <= count; i++)
        {
            var child = Build(ResolveClass(childClass!), o, prefix + i);
            switch (childClass)
            {
                case "Page":
                    child.Set("Caption", Value.String("Page" + i));
                    child.Set("PageOrder", Value.Number(i));
                    break;
                case "OptionButton":
                    child.Set("Caption", Value.String("Option" + i));
                    child.Set("Left", Value.Number(5));
                    child.Set("Top", Value.Number(5 + (i - 1) * 22));
                    child.Set("Width", Value.Number(80));
                    child.Set("Height", Value.Number(17));
                    child.Set("Value", Value.Number(0));
                    break;
                case "CommandButton":
                    child.Set("Caption", Value.String("Command" + i));
                    child.Set("Left", Value.Number(5));
                    child.Set("Top", Value.Number(5 + (i - 1) * 30));
                    child.Set("Width", Value.Number(84));
                    child.Set("Height", Value.Number(27));
                    break;
                case "Column":
                {
                    child.Set("Width", Value.Number(75));
                    child.Set("ColumnOrder", Value.Number(i));
                    var header = Build(ResolveClass("Header"), child, "Header1");
                    header.Set("Caption", Value.String("Header1"));
                    child.Members.Add(header);
                    child.Set("Header1", Value.Object(header));
                    var text = Build(ResolveClass("TextBox"), child, "Text1");
                    child.Members.Add(text);
                    child.Set("Text1", Value.Object(text));
                    child.Set("CurrentControl", Value.String("Text1"));
                    break;
                }
            }
            o.Members.Add(child);
            o.Set(prefix + i, Value.Object(child));
        }
        for (int i = existing.Count; i > Math.Max(count, 0); i--)
        {
            var extra = o.FindProperty(prefix + i)?.Value;
            if (extra is { Kind: ValueKind.Object } ev) { o.Members.Remove((VfpObject)ev.AsObject); o.Properties.Remove(prefix + i); }
        }
    }

    private void SetPath(VfpObject o, string path, Value value)
    {
        var parts = path.Split('.');
        for (int i = 0; i < parts.Length - 1; i++)
            o = o.FindProperty(parts[i])?.Value is { Kind: ValueKind.Object } v ? (VfpObject)v.AsObject : throw VfpObject.PropertyNotFound(parts[i]);
        o.Set(parts[^1], value);
    }

    /// <summary>Runs an event or method if the class defines it; returns .T. otherwise.</summary>
    internal Value RaiseEvent(VfpObject o, string name, List<Arg> args)
    {
        Debugger?.OnEvent(o, name);
        RunDelegates(o, name, args, after: false);
        var m = FindHandler(o, name);
        var result = m == null ? Value.True : Invoke(m.Value.Method, m.Value.Owner.Unit, args, self: o, methodClass: m.Value.Owner);
        RunDelegates(o, name, args, after: true);
        return result;
    }

    /// <summary>
    /// Finds the code for an object's event or method. Code written in a container for one of its members
    /// (PROCEDURE txtName.Valid inside a form class) overrides the member's own class code; the outermost
    /// container wins, as in VFP. DODEFAULT() from such code runs the member class's method.
    /// </summary>
    internal (ProcedureDef Method, ClassInfo Owner)? FindHandler(VfpObject o, string name)
    {
        var chain = new List<(VfpObject Container, string Path)>();
        var path = o.Name;
        for (var p = o.Parent; p != null; p = p.Parent)
        {
            chain.Add((p, path));
            path = p.Name + "." + path;
        }
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var (container, rel) = chain[i];
            var found = container.Class.FindMethod(rel + "." + name);
            if (found != null)
            {
                // A per-instance class level whose parent is the member's own class (for DODEFAULT).
                var instance = new ClassInfo(o.Class.Name, o.Class.BaseClass, o.Class, null, found.Value.Owner.Unit);
                return (found.Value.Method, instance);
            }
        }
        return o.Class.FindMethod(name);
    }

    internal void ReleaseObject(VfpObject o)
    {
        if (o.Released) return;
        if (IsFormClass(o))
        {
            // Release() skips QueryUnload; the UI host raises QueryUnload when the user closes the window.
            ReleaseForm(o);
            return;
        }
        o.Released = true;
        RaiseEvent(o, "Destroy", []);
        foreach (var m in o.Members.ToList()) ReleaseObject(m);
        if (IsToolbar(o)) Ui?.Release(o);
        _hyperlinkHistory.Remove(o);
        if (o.Parent != null)
        {
            o.Parent.Members.Remove(o);
            o.Parent.Properties.Remove(o.Name);
        }
    }

    /// <summary>Last value returned by a form's Unload event (DO FORM … TO var).</summary>
    internal readonly Dictionary<VfpObject, Value> UnloadResults = new(ReferenceEqualityComparer.Instance);

    /// <summary>Form teardown: Destroy (form, then members), Unload, close the private data session.</summary>
    internal void ReleaseForm(VfpObject form)
    {
        if (form.Released) return;
        form.Released = true;
        InObjectContext(form, () =>
        {
            RaiseEvent(form, "Destroy", []);
            var de = DataEnvironmentOf(form);
            foreach (var m in form.Members.ToList()) if (m != de) ReleaseObject(m);
            UnloadResults[form] = RaiseEvent(form, "Unload", []);
            if (de != null)
            {
                // The data environment closes its tables after Unload, then is destroyed.
                if (de.FindProperty("AutoCloseTables")?.Value is not { Kind: ValueKind.Logical } auto || auto.AsBool)
                    InvokeMethod(de, "CloseTables", []);
                RaiseEvent(de, "AfterCloseTables", []);
                ReleaseObject(de);
            }
            return true;
        });
        Ui?.Release(form);
        if (_formSessions.Remove(form, out var session))
        {
            Sessions.Remove(session);
            session.Dispose();
        }
    }

    private VfpObject ResolveSpecial(string which)
    {
        switch (which)
        {
            case "THIS":
                return _frame.This ?? throw new VfpException(1925, "THIS can only be used within a method.");
            case "THISFORM":
            {
                for (var o = _frame.This; o != null; o = o.Parent)
                    if (o.Class.BaseClass.Equals("Form", StringComparison.OrdinalIgnoreCase)) return o;
                throw new VfpException(1925, "THISFORM can only be used within a method.");
            }
            case "THISFORMSET":
            {
                for (var o = _frame.This; o != null; o = o.Parent)
                    if (o.Class.BaseClass.Equals("FormSet", StringComparison.OrdinalIgnoreCase)) return o;
                throw new VfpException(1925, "THISFORMSET can only be used within a method.");
            }
            case "WITH":
            {
                for (var f = _frame; f != null; f = f.Parent)
                    if (f.WithStack.Count > 0) return f.WithStack.Peek() as VfpObject ?? throw new VfpException(1924, "WITH target is not an object.");
                throw VfpException.Syntax("'.' member reference outside WITH … ENDWITH.");
            }
            case "_SCREEN":
                if (_screen == null)
                {
                    _screen = CreateObject(ResolveClass("Form"), [], name: "Screen")!;
                    _screen.Set("Caption", Value.String("Joe Pro"));
                    _screen.Set("Visible", Value.True);
                }
                return _screen;
            default:
                if (_application == null)
                {
                    _application = CreateObject(ResolveClass("Custom"), [], name: "Application")!;
                    _application.Set("Version", Value.String(VersionString));
                    _application.Set("Caption", Value.String("Joe Pro"));
                    _application.Set("StartMode", Value.Number(0));
                }
                return _application;
        }
    }

    public const string VersionString = "Joe Pro 0.1 (FoxPro 9 compatible)";

    // ================================================================================
    // Properties
    // ================================================================================

    internal Value GetProperty(VfpObject o, string name)
    {
        if (name.Equals("Parent", StringComparison.OrdinalIgnoreCase))
            return o.Parent != null ? Value.Object(o.Parent) : Value.Null;
        if (o.Items != null && name.Equals("Count", StringComparison.OrdinalIgnoreCase))
            return Value.Number(o.Items.Count);
        if (name.Equals("ControlCount", StringComparison.OrdinalIgnoreCase) && o.FindProperty("ControlCount") != null)
            return Value.Number(o.Members.Count);
        var accessor = o.Class.FindMethod(name + "_ACCESS");
        if (accessor != null && _inAccessor.Add((o.Id, name + "_ACCESS")))
        {
            try { return Invoke(accessor.Value.Method, accessor.Value.Owner.Unit, [], self: o, methodClass: accessor.Value.Owner); }
            finally { _inAccessor.Remove((o.Id, name + "_ACCESS")); }
        }
        var p = o.FindProperty(name);
        if (p == null)
        {
            var thisAccess = o.Class.FindMethod("THIS_ACCESS");
            if (thisAccess != null && _inAccessor.Add((o.Id, "THIS_ACCESS")))
            {
                try
                {
                    var target = Invoke(thisAccess.Value.Method, thisAccess.Value.Owner.Unit, [new Arg(Value.String(name), null)], self: o, methodClass: thisAccess.Value.Owner);
                    if (target.Kind == ValueKind.Object && target.AsObject is VfpObject t && t != o) return GetProperty(t, name);
                }
                finally { _inAccessor.Remove((o.Id, "THIS_ACCESS")); }
            }
            throw VfpObject.PropertyNotFound(name);
        }
        return p.IsArray ? p.Array![1] : p.Value;
    }

    internal void SetProperty(VfpObject o, string name, Value value)
    {
        var assign = o.Class.FindMethod(name + "_ASSIGN");
        if (assign != null && _inAccessor.Add((o.Id, name + "_ASSIGN")))
        {
            try { Invoke(assign.Value.Method, assign.Value.Owner.Unit, [new Arg(value, null)], self: o, methodClass: assign.Value.Owner); }
            finally { _inAccessor.Remove((o.Id, name + "_ASSIGN")); }
            return;
        }
        var p = o.FindProperty(name) ?? throw VfpObject.PropertyNotFound(name);
        if (p.IsArray) p.Array!.Fill(value);
        else p.Value = value;
        if (name.Equals("PageCount", StringComparison.OrdinalIgnoreCase) || name.Equals("ButtonCount", StringComparison.OrdinalIgnoreCase)
            || (name.Equals("ColumnCount", StringComparison.OrdinalIgnoreCase) && o.Class.BaseClass.Equals("Grid", StringComparison.OrdinalIgnoreCase)))
            SyncAutoChildren(o);
        Ui?.PropertyChanged(o, name);
    }

    // ================================================================================
    // Methods
    // ================================================================================

    private Value CallMethod(MethodCallExpr mc)
    {
        // alias.field(…) is never valid; objects only.
        var target = Eval(mc.Target);
        if (target.Kind != ValueKind.Object) throw new VfpException(1924, $"{ExprPrinter.Print(mc.Target).ToUpperInvariant()} is not an object.");
        if (target.AsObject is ClrObjectProxy proxy) return proxy.Call(mc.Name, mc.Args.Select(Eval).ToList());
        var o = (VfpObject)target.AsObject;
        var prop = o.FindProperty(mc.Name);
        if (prop?.Array != null) return prop.Array[ArrayIndex(prop.Array, mc.Args)];
        CheckAccess(o, mc.Name);
        return InvokeMethod(o, mc.Name, EvalArgs(mc.Args, byRefVariables: false));
    }

    // ================================================================================
    // Visibility (PROTECTED / HIDDEN)
    // ================================================================================

    internal Value GetPropertyChecked(VfpObject o, string name)
    {
        CheckAccess(o, name);
        return GetProperty(o, name);
    }

    /// <summary>
    /// PROTECTED members are visible only to the object's own methods; HIDDEN members only to methods of the
    /// class level that declared them. Outside code gets "Property … is not found", as in VFP.
    /// </summary>
    internal void CheckAccess(VfpObject o, string name)
    {
        var levels = o.Class.Hierarchy().Where(c => c.Definition != null).ToList();
        if (levels.Count == 0 && o.Parent == null) return;
        ClassInfo? hiddenAt = levels.FirstOrDefault(c => c.Definition!.Hidden.Contains(name));
        bool isProtected = hiddenAt == null && (levels.Any(c => c.Definition!.Protected.Contains(name))
            || (o.Parent != null && o.Parent.Class.Hierarchy().Any(c => c.Definition?.Objects.Any(a => a.Protected && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true))
            || levels.Any(c => c.Definition!.Objects.Any(a => a.Protected && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))));
        if (hiddenAt == null && !isProtected) return;
        // Code running in a method of this object (or while it is being constructed).
        var self = _frame.This;
        if (hiddenAt != null)
        {
            if (ReferenceEquals(self, o) && (_frame.MethodClass == hiddenAt || _frame.MethodClass == null)) return;
        }
        else if (ReferenceEquals(self, o) || (self != null && IsMemberOf(o, self))) return;
        throw VfpObject.PropertyNotFound(name);
    }

    private static bool IsMemberOf(VfpObject member, VfpObject container)
    {
        for (var p = member.Parent; p != null; p = p.Parent)
            if (ReferenceEquals(p, container)) return true;
        return false;
    }

    // ================================================================================
    // BINDEVENT
    // ================================================================================

    private readonly Dictionary<(VfpObject Source, string Event), List<(VfpObject Handler, string Method, int Flags)>> _bindings = new();

    internal void BindEvent(VfpObject source, string eventName, VfpObject handler, string method, int flags)
    {
        var key = (source, eventName.ToUpperInvariant());
        if (!_bindings.TryGetValue(key, out var list)) _bindings[key] = list = new();
        list.RemoveAll(b => ReferenceEquals(b.Handler, handler) && b.Method.Equals(method, StringComparison.OrdinalIgnoreCase));
        list.Add((handler, method, flags));
    }

    internal int UnbindEvents(VfpObject target, string? eventName, VfpObject? handler, string? method)
    {
        int removed = 0;
        foreach (var key in _bindings.Keys.ToList())
        {
            var list = _bindings[key];
            if (ReferenceEquals(key.Source, target) && (eventName == null || key.Event == eventName.ToUpperInvariant()))
            {
                removed += list.RemoveAll(b => handler == null || (ReferenceEquals(b.Handler, handler) && (method == null || b.Method.Equals(method, StringComparison.OrdinalIgnoreCase))));
            }
            else if (eventName == null && handler == null)
            {
                removed += list.RemoveAll(b => ReferenceEquals(b.Handler, target));
            }
            if (list.Count == 0) _bindings.Remove(key);
        }
        return removed;
    }

    /// <summary>
    /// Runs BINDEVENT delegates for an event. nFlags bit 0 runs the delegate after the event code (default:
    /// before); bit 1 skips the delegate when the method is simply called from code rather than raised.
    /// </summary>
    private void RunDelegates(VfpObject source, string eventName, List<Arg> args, bool after, bool methodCall = false)
    {
        if (_bindings.Count == 0 || !_bindings.TryGetValue((source, eventName.ToUpperInvariant()), out var list)) return;
        foreach (var (handler, method, flags) in list.ToList())
        {
            if (((flags & 1) != 0) != after || handler.Released || (methodCall && (flags & 2) != 0)) continue;
            _eventSources.Push((source, eventName));
            try { InvokeMethodCore(handler, method, args); }
            finally { _eventSources.Pop(); }
        }
    }

    private readonly Stack<(VfpObject Source, string Event)> _eventSources = new();

    /// <summary>The object and event that triggered the running delegate, for AEVENTS(a, 0).</summary>
    internal (VfpObject Source, string Event)? CurrentEventSource => _eventSources.Count > 0 ? _eventSources.Peek() : null;

    /// <summary>Bindings for AEVENTS(a, oObject): (source, event, handler, method, flags).</summary>
    internal IEnumerable<(VfpObject Source, string Event, VfpObject Handler, string Method, int Flags)> BindingsFor(VfpObject o) =>
        _bindings.SelectMany(kv => kv.Value.Select(b => (kv.Key.Source, kv.Key.Event, b.Handler, b.Method, b.Flags)))
            .Where(b => ReferenceEquals(b.Source, o) || ReferenceEquals(b.Handler, o));

    internal Value InvokeMethod(VfpObject o, string name, List<Arg> args)
    {
        RunDelegates(o, name, args, after: false, methodCall: true);
        var result = InvokeMethodCore(o, name, args);
        RunDelegates(o, name, args, after: true, methodCall: true);
        return result;
    }

    private Value InvokeMethodCore(VfpObject o, string name, List<Arg> args)
    {
        var m = FindHandler(o, name);
        if (m != null) return Invoke(m.Value.Method, m.Value.Owner.Unit, args, self: o, methodClass: m.Value.Owner);
        return NativeMethod(o, name, args);
    }

    /// <summary>DODEFAULT(): runs the parent class's version of the executing method.</summary>
    internal Value DoDefault(List<Arg> args)
    {
        var f = _frame;
        if (f.This == null || f.MethodClass == null || f.MethodName == null)
            throw new VfpException(1925, "DODEFAULT() can only be used within a method.");
        var parent = f.MethodClass.Parent;
        var m = parent?.FindMethod(f.MethodName);
        if (m != null) return Invoke(m.Value.Method, m.Value.Owner.Unit, args, self: f.This, methodClass: m.Value.Owner);
        return NativeMethod(f.This, f.MethodName, args, isDefault: true);
    }

    private Value NativeMethod(VfpObject o, string name, List<Arg> args, bool isDefault = false)
    {
        Value A(int i) => i < args.Count ? args[i].Value : Value.False;
        switch (name.ToUpperInvariant())
        {
            case "OPENTABLES" when o.Class.BaseClass == "DataEnvironment":
                OpenDataEnvironmentTables(o);
                return Value.True;
            case "CLOSETABLES" when o.Class.BaseClass == "DataEnvironment":
                CloseDataEnvironmentTables(o);
                return Value.True;
            case "INIT" or "DESTROY" or "ERROR" or "LOAD" or "UNLOAD" or "ACTIVATE" or "DEACTIVATE" or "CLICK" or "DBLCLICK"
                or "GOTFOCUS" or "LOSTFOCUS" or "VALID" or "WHEN" or "INTERACTIVECHANGE" or "PROGRAMMATICCHANGE" or "QUERYUNLOAD"
                or "RESIZE" or "TIMER" or "KEYPRESS" or "MOUSEDOWN" or "MOUSEUP" or "MOUSEMOVE" or "DRAW" or "MOVED"
                or "BEFOREOPENTABLES" or "AFTERCLOSETABLES" or "OPENTABLES" or "CLOSETABLES"
                or "BEFORECURSORFILL" or "AFTERCURSORFILL" or "BEFORECURSORREFRESH" or "AFTERCURSORREFRESH":
                return Value.True;
            case "ADDPROPERTY":
            {
                var pn = A(0).AsString;
                var paren = pn.IndexOf('(') is var ip and >= 0 ? ip : pn.IndexOf('[');
                if (paren > 0)
                {
                    var dims = pn[(paren + 1)..].TrimEnd(')', ']').Split(',').Select(s => int.Parse(s.Trim())).ToList();
                    var v = new Variable(pn[..paren], Value.False) { Array = new VfpArray(dims[0], dims.Count > 1 ? dims[1] : 0) };
                    o.Properties[pn[..paren]] = v;
                }
                else o.Set(pn, args.Count > 1 ? A(1) : Value.False);
                return Value.True;
            }
            case "REMOVEPROPERTY":
                return Value.Logical(o.Properties.Remove(A(0).AsString));
            case "ADDOBJECT":
            case "NEWOBJECT":
            {
                var objName = A(0).AsString;
                var className = A(1).AsString;
                ProgramUnit? module = null;
                int argStart = 2;
                if (name.Equals("NEWOBJECT", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Count > 2 && A(2).Kind == ValueKind.Character && A(2).AsString.Length > 0) module = LoadLibrary(A(2).AsString);
                    argStart = 4;
                }
                var child = CreateObject(ResolveClass(className, module), args.Skip(argStart).ToList(), o, objName)
                    ?? throw new VfpException(1733, "Object could not be created.");
                o.Members.Add(child);
                o.Set(objName, Value.Object(child));
                return Value.True;
            }
            case "REMOVEOBJECT":
            {
                var child = o.FindProperty(A(0).AsString)?.Value;
                if (child is { Kind: ValueKind.Object } c) ReleaseObject((VfpObject)c.AsObject);
                return Value.True;
            }
            case "RELEASE":
                ReleaseObject(o);
                return Value.True;
            case "RESETTODEFAULT":
                o.Set(A(0).AsString, BaseClasses.DefaultValue(A(0).AsString));
                return Value.True;
            case "SHOW":
                if (o.FindProperty("Visible") != null) o.Set("Visible", Value.True);
                if (IsFormClass(o) || IsToolbar(o))
                {
                    if (Ui == null) Notify($"{o.Name}.Show(): no UI runtime is attached.");
                    else Ui.Show(o, A(0).Kind == ValueKind.Number && A(0).AsNumber == 1 || (o.FindProperty("WindowType")?.Value is { Kind: ValueKind.Number } wt && wt.AsNumber == 1));
                }
                else Ui?.PropertyChanged(o, "Visible");
                return Value.True;
            case "HIDE":
                if (o.FindProperty("Visible") != null) o.Set("Visible", Value.False);
                if (IsFormClass(o) || IsToolbar(o)) Ui?.Hide(o); else Ui?.PropertyChanged(o, "Visible");
                return Value.True;
            case "DOCK" when IsToolbar(o):
            {
                // Dock(nLocation [, nX, nY]): -1 undocked (floating), 0 top, 1 left, 2 right, 3 bottom.
                var position = args.Count > 0 && A(0).Kind == ValueKind.Number ? (int)A(0).AsNumber : 0;
                o.Set("DockPosition", Value.Number(position));
                o.Set("Docked", Value.Logical(position >= 0));
                if (args.Count > 2) { o.Set("Left", A(1)); o.Set("Top", A(2)); }
                RaiseEvent(o, position >= 0 ? "AfterDock" : "UnDock", []);
                Ui?.PropertyChanged(o, "DockPosition");
                return Value.True;
            }
            case "NAVIGATETO" when o.Class.BaseClass == "Hyperlink":
            {
                var url = A(0).AsString.Trim();
                var state = HyperlinkHistory(o);
                state.Urls.RemoveRange(state.Index + 1, state.Urls.Count - state.Index - 1);
                state.Urls.Add(url);
                state.Index = state.Urls.Count - 1;
                OpenUrl(url);
                return Value.True;
            }
            case "GOBACK" or "GOFORWARD" when o.Class.BaseClass == "Hyperlink":
            {
                var state = HyperlinkHistory(o);
                var at = state.Index + (name.Equals("GOBACK", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                if (at < 0 || at >= state.Urls.Count) return Value.False;
                state.Index = at;
                OpenUrl(state.Urls[at]);
                return Value.True;
            }
            case "REFRESH":
                Ui?.Refresh(o);
                return Value.True;
            case "CURSORFILL" when o.Class.BaseClass == "CursorAdapter": return CursorAdapters.CursorFill(this, o, args);
            case "CURSORREFRESH" when o.Class.BaseClass == "CursorAdapter": return CursorAdapters.CursorRefresh(this, o);
            case "CURSORATTACH" when o.Class.BaseClass == "CursorAdapter": return CursorAdapters.CursorAttach(this, o, args);
            case "CURSORDETACH" when o.Class.BaseClass == "CursorAdapter": return CursorAdapters.CursorDetach(this, o);
            case "SETFOCUS":
                Ui?.SetFocus(o);
                return Value.True;
            case "ADDITEM" or "ADDLISTITEM":
            {
                o.ListItems ??= new();
                var text = A(0).Kind == ValueKind.Character ? A(0).AsString : Builtins.Library.TransformDefault(A(0), Options);
                var at = args.Count > 1 && A(1).Kind == ValueKind.Number ? (int)A(1).AsNumber : 0;
                if (at >= 1 && at <= o.ListItems.Count) o.ListItems.Insert(at - 1, text); else o.ListItems.Add(text);
                if (o.FindProperty("ListCount") != null) o.Set("ListCount", Value.Number(o.ListItems.Count));
                Ui?.PropertyChanged(o, "RowSource");
                return Value.True;
            }
            case "REMOVEITEM" or "REMOVELISTITEM":
            {
                var at = (int)A(0).AsNumber;
                if (o.ListItems != null && at >= 1 && at <= o.ListItems.Count) o.ListItems.RemoveAt(at - 1);
                if (o.FindProperty("ListCount") != null) o.Set("ListCount", Value.Number(o.ListItems?.Count ?? 0));
                Ui?.PropertyChanged(o, "RowSource");
                return Value.True;
            }
            case "CLEAR" when o.Class.BaseClass is "ListBox" or "ComboBox":
                o.ListItems?.Clear();
                o.Set("ListCount", Value.Number(0));
                Ui?.PropertyChanged(o, "RowSource");
                return Value.True;
            case "LIST" or "LISTITEM" when o.Class.BaseClass is "ListBox" or "ComboBox":
            {
                var at = (int)A(0).AsNumber;
                return Value.String(o.ListItems != null && at >= 1 && at <= o.ListItems.Count ? o.ListItems[at - 1] : "");
            }
            case "READEXPRESSION" or "READMETHOD" or "WRITEEXPRESSION" or "WRITEMETHOD" or "SAVEASCLASS" or "SETALL":
                if (name.Equals("SETALL", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var m in o.Members)
                        if (args.Count < 3 || m.Class.IsA(A(2).AsString))
                            if (m.FindProperty(A(0).AsString) != null) m.Set(A(0).AsString, A(1));
                    return Value.True;
                }
                return Value.EmptyString;
        }
        if (o.Items != null) return CollectionMethod(o, name, args);
        if (isDefault) return Value.True;
        throw new VfpException(1734, $"Property {name.ToUpperInvariant()} is not found.", name);
    }

    private Value CollectionMethod(VfpObject o, string name, List<Arg> args)
    {
        var items = o.Items!;
        Value A(int i) => i < args.Count ? args[i].Value : Value.False;
        int IndexOf(Value key)
        {
            if (key.Kind == ValueKind.Number)
            {
                var i = (int)key.AsNumber;
                if (i < 1 || i > items.Count) throw VfpException.InvalidArgument();
                return i - 1;
            }
            var k = key.AsString;
            var idx = items.FindIndex(it => string.Equals(it.Key, k, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw VfpException.InvalidArgument();
            return idx;
        }
        switch (name.ToUpperInvariant())
        {
            case "ADD":
            {
                string? key = args.Count > 1 && A(1).Kind == ValueKind.Character ? A(1).AsString : null;
                if (key != null && items.Any(it => string.Equals(it.Key, key, StringComparison.OrdinalIgnoreCase)))
                    throw new VfpException(2062, "The specified key already exists.");
                if (args.Count > 2 && A(2).Kind != ValueKind.Logical) items.Insert(IndexOf(A(2)), (key, A(0)));
                else if (args.Count > 3 && A(3).Kind != ValueKind.Logical) items.Insert(IndexOf(A(3)) + 1, (key, A(0)));
                else items.Add((key, A(0)));
                return Value.True;
            }
            case "REMOVE":
                if (A(0).Kind == ValueKind.Number && A(0).AsNumber == -1) items.Clear();
                else items.RemoveAt(IndexOf(A(0)));
                return Value.True;
            case "ITEM":
                return items[IndexOf(A(0))].Value;
            case "GETKEY":
                if (A(0).Kind == ValueKind.Character)
                {
                    var i = items.FindIndex(it => string.Equals(it.Key, A(0).AsString, StringComparison.OrdinalIgnoreCase));
                    return Value.Number(i + 1);
                }
                return Value.String(items[IndexOf(A(0))].Key ?? "");
        }
        throw new VfpException(1734, $"Property {name.ToUpperInvariant()} is not found.", name);
    }
}
