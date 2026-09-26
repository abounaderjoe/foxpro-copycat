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

    /// <summary>Instantiates a class: native defaults, class-level member values base→derived, member objects, then Init.</summary>
    internal VfpObject? CreateObject(ClassInfo cls, List<Arg> args, VfpObject? parent = null, string? name = null, bool noInit = false)
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
        _frame = new Frame(cls.Name.ToUpperInvariant() + ".INIT", saved) { This = o, Unit = cls.Unit ?? saved.Unit };
        try
        {
            foreach (var level in cls.Hierarchy().Reverse())
            {
                if (level.Definition == null) continue;
                var deferred = new List<MemberDef>();
                foreach (var m in level.Definition.Members)
                {
                    if (m.Name.Contains('.')) { deferred.Add(m); continue; }
                    if (m.Dims != null)
                    {
                        var dims = m.Dims.Select(d => (int)Eval(d).AsNumber).ToList();
                        var v = o.FindProperty(m.Name) ?? new Variable(m.Name, Value.False);
                        v.Array = new VfpArray(dims[0], dims.Count > 1 ? dims[1] : 0);
                        o.Properties[m.Name] = v;
                    }
                    else o.Set(m.Name, Eval(m.Value!));
                }
                foreach (var ao in level.Definition.Objects)
                {
                    var path = ao.Name.Split('.');
                    var container = o;
                    for (int i = 0; i < path.Length - 1; i++)
                        container = container.FindProperty(path[i])?.Value is { Kind: ValueKind.Object } cv ? (VfpObject)cv.AsObject : throw VfpObject.PropertyNotFound(path[i]);
                    var childClass = ResolveClass(ao.Class, level.Unit);
                    var child = CreateObject(childClass, [], container, path[^1], noInit: true)!;
                    foreach (var (prop, expr) in ao.Properties) SetPath(child, prop, Eval(expr));
                    container.Members.Add(child);
                    container.Set(path[^1], Value.Object(child));
                    if (!ao.NoInit) RaiseEvent(child, "Init", []);
                }
                foreach (var m in deferred) SetPath(o, m.Name, Eval(m.Value!));
            }
        }
        finally
        {
            _frame = saved;
        }

        if (noInit) return o;
        var ok = RaiseEvent(o, "Init", args);
        if (ok.Kind == ValueKind.Logical && !ok.AsBool) return null;
        return o;
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
        var m = o.Class.FindMethod(name);
        if (m == null) return Value.True;
        return Invoke(m.Value.Method, m.Value.Owner.Unit, args, self: o, methodClass: m.Value.Owner);
    }

    internal void ReleaseObject(VfpObject o)
    {
        if (o.Released) return;
        o.Released = true;
        RaiseEvent(o, "Destroy", []);
        foreach (var m in o.Members.ToList()) ReleaseObject(m);
        if (o.Parent != null)
        {
            o.Parent.Members.Remove(o);
            o.Parent.Properties.Remove(o.Name);
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
        var m = o.Class.FindMethod(name);
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
            case "INIT" or "DESTROY" or "ERROR" or "LOAD" or "UNLOAD" or "ACTIVATE" or "DEACTIVATE" or "REFRESH" or "CLICK" or "DBLCLICK"
                or "GOTFOCUS" or "LOSTFOCUS" or "VALID" or "WHEN" or "INTERACTIVECHANGE" or "PROGRAMMATICCHANGE" or "QUERYUNLOAD"
                or "RESIZE" or "TIMER" or "KEYPRESS" or "MOUSEDOWN" or "MOUSEUP" or "MOUSEMOVE" or "SETFOCUS" or "DRAW" or "MOVED"
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
            case "SHOW" or "HIDE":
                if (o.FindProperty("Visible") != null) o.Set("Visible", Value.Logical(name.Equals("SHOW", StringComparison.OrdinalIgnoreCase)));
                return Value.True;
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
