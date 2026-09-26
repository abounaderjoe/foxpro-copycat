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
            var parent = ResolveClass(def.Parent, def.ParentLib != null && ResolveProgramFile(def.ParentLib) is { } lib ? LoadProgram(lib) : module);
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
                    Sessions.Add(session);
                    _formSessions[o] = session;
                    o.Set("DataSessionId", Value.Number(session.Id));
                    Session = session;
                }
                else o.Set("DataSessionId", Value.Number(Session.Id));
                var loaded = RaiseEvent(o, "Load", []);
                if (loaded.Kind == ValueKind.Logical && !loaded.AsBool) o.Set("__LoadFailed", Value.True);
            }
            SyncAutoChildren(o);
            // Pass 2: member objects (their Init runs later, in InitTree).
            foreach (var level in levels)
            {
                foreach (var ao in level.Definition!.Objects)
                {
                    var path = ao.Name.Split('.');
                    var container = o;
                    for (int i = 0; i < path.Length - 1; i++)
                        container = container.FindProperty(path[i])?.Value is { Kind: ValueKind.Object } cv ? (VfpObject)cv.AsObject : throw VfpObject.PropertyNotFound(path[i]);
                    var childClass = ResolveClass(ao.Class, level.Unit);
                    var existing = container.FindProperty(path[^1])?.Value is { Kind: ValueKind.Object } ev ? (VfpObject)ev.AsObject : null;
                    if (existing != null) container.Members.Remove(existing);
                    var child = Build(childClass, container, path[^1]);
                    child.SkipInit = ao.NoInit;
                    foreach (var (prop, expr) in ao.Properties) SetPath(child, prop, Eval(expr));
                    SyncAutoChildren(child);
                    container.Members.Add(child);
                    container.Set(path[^1], Value.Object(child));
                }
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
        var m = FindHandler(o, name);
        if (m == null) return Value.True;
        return Invoke(m.Value.Method, m.Value.Owner.Unit, args, self: o, methodClass: m.Value.Owner);
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
            foreach (var m in form.Members.ToList()) ReleaseObject(m);
            UnloadResults[form] = RaiseEvent(form, "Unload", []);
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
        var o = (VfpObject)target.AsObject;
        var prop = o.FindProperty(mc.Name);
        if (prop?.Array != null) return prop.Array[ArrayIndex(prop.Array, mc.Args)];
        return InvokeMethod(o, mc.Name, EvalArgs(mc.Args, byRefVariables: false));
    }

    internal Value InvokeMethod(VfpObject o, string name, List<Arg> args)
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
            case "INIT" or "DESTROY" or "ERROR" or "LOAD" or "UNLOAD" or "ACTIVATE" or "DEACTIVATE" or "CLICK" or "DBLCLICK"
                or "GOTFOCUS" or "LOSTFOCUS" or "VALID" or "WHEN" or "INTERACTIVECHANGE" or "PROGRAMMATICCHANGE" or "QUERYUNLOAD"
                or "RESIZE" or "TIMER" or "KEYPRESS" or "MOUSEDOWN" or "MOUSEUP" or "MOUSEMOVE" or "DRAW" or "MOVED"
                or "BEFOREOPENTABLES" or "AFTERCLOSETABLES" or "OPENTABLES" or "CLOSETABLES":
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
                    if (args.Count > 2 && A(2).Kind == ValueKind.Character && A(2).AsString.Length > 0) module = LoadProgram(A(2).AsString);
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
                if (IsFormClass(o))
                {
                    if (Ui == null) Notify($"{o.Name}.Show(): no UI runtime is attached.");
                    else Ui.Show(o, A(0).Kind == ValueKind.Number && A(0).AsNumber == 1 || (o.FindProperty("WindowType")?.Value is { Kind: ValueKind.Number } wt && wt.AsNumber == 1));
                }
                else Ui?.PropertyChanged(o, "Visible");
                return Value.True;
            case "HIDE":
                if (o.FindProperty("Visible") != null) o.Set("Visible", Value.False);
                if (IsFormClass(o)) Ui?.Hide(o); else Ui?.PropertyChanged(o, "Visible");
                return Value.True;
            case "REFRESH":
                Ui?.Refresh(o);
                return Value.True;
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
