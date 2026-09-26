namespace JoePro.Documents.Design;

/// <summary>
/// Editing operations on a form or class document, as used by the Form and Class Designers: properties,
/// objects, methods, z-order. Every change is undoable. Undo keeps canonical text snapshots, so an undo restores
/// exactly the document that was there before (the files are small, and this cannot drift).
/// </summary>
public sealed class DesignSession
{
    private readonly List<(string Name, string Text)> _undo = new();
    private readonly List<(string Name, string Text)> _redo = new();
    private string _savedText;
    private int _transactionDepth;
    private string? _transactionStart;
    private string _transactionName = "";

    public DesignSession(ClassFile file, string? className = null)
    {
        File = file;
        ClassName = className ?? file.Classes.First().Name;
        _savedText = Text;
    }

    public ClassFile File { get; private set; }
    public string ClassName { get; private set; }
    public ClassDocument Class => File.Find(ClassName) ?? throw new InvalidOperationException($"Class {ClassName} is not in the document.");

    /// <summary>The canonical text of the whole document.</summary>
    public string Text => ClassFileWriter.Write(File);

    public bool IsDirty => Text != _savedText;
    public void MarkSaved() => _savedText = Text;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    /// <summary>Raised after every change, undo and redo.</summary>
    public event Action? Changed;

    // ---- Transactions and undo ------------------------------------------------------------------

    /// <summary>Groups changes into one undo step (nested calls join the outer transaction).</summary>
    public void Transaction(string name, Action body)
    {
        if (_transactionDepth++ == 0) { _transactionStart = Text; _transactionName = name; }
        try { body(); }
        finally
        {
            if (--_transactionDepth == 0)
            {
                var before = _transactionStart!;
                _transactionStart = null;
                if (before != Text)
                {
                    _undo.Add((_transactionName, before));
                    _redo.Clear();
                    Changed?.Invoke();
                }
            }
        }
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (name, text) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((name, Text));
        Restore(text);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (name, text) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((name, Text));
        Restore(text);
    }

    private void Restore(string text)
    {
        File = ClassFileReader.Parse(text, File.Kind);
        if (File.Find(ClassName) == null) ClassName = File.Classes.First().Name;
        Changed?.Invoke();
    }

    // ---- Objects and paths --------------------------------------------------------------------------

    /// <summary>
    /// The declared object that stores properties for <paramref name="objectPath"/>, and the path below it.
    /// Objects created automatically (Page1, Column1.Header1) or inside a member's own class store their
    /// properties as dotted names on the nearest declared ancestor, as VFP does.
    /// </summary>
    public (string OwnerPath, string SubPath) Owner(string objectPath)
    {
        if (objectPath.Length == 0) return ("", "");
        var best = "";
        foreach (var m in Class.Members)
            if ((objectPath.Equals(m.Path, StringComparison.OrdinalIgnoreCase) || objectPath.StartsWith(m.Path + ".", StringComparison.OrdinalIgnoreCase))
                && m.Path.Length > best.Length)
                best = m.Path;
        var rest = best.Length == 0 ? objectPath : objectPath.Length == best.Length ? "" : objectPath[(best.Length + 1)..];
        return (best, rest);
    }

    private PropertyList ListOf(string ownerPath) =>
        Class.PropertiesOf(ownerPath) ?? throw new ArgumentException($"No object {ownerPath}.");

    private static string Qualified(string rest, string prop) => rest.Length == 0 ? prop : rest + "." + prop;

    /// <summary>The expression text stored for a property at this level, or null when it is inherited or default.</summary>
    public string? GetProperty(string objectPath, string property)
    {
        var (owner, rest) = Owner(objectPath);
        return ListOf(owner)[Qualified(rest, property)];
    }

    /// <summary>Sets a property to expression text (null resets it to its inherited or default value).</summary>
    public void SetProperty(string objectPath, string property, string? expression) =>
        Transaction($"Set {property}", () =>
        {
            var (owner, rest) = Owner(objectPath);
            ListOf(owner)[Qualified(rest, property)] = expression;
        });

    /// <summary>Sets several properties of several objects as one undo step (move, resize, align).</summary>
    public void SetProperties(string name, IEnumerable<(string ObjectPath, string Property, string? Expression)> changes) =>
        Transaction(name, () =>
        {
            foreach (var (path, prop, expr) in changes)
            {
                var (owner, rest) = Owner(path);
                ListOf(owner)[Qualified(rest, prop)] = expr;
            }
        });

    public bool IsDeclared(string objectPath) => objectPath.Length == 0 || Class.FindMember(objectPath) != null;

    /// <summary>A free name like Text1, Text2… for a new object in a container.</summary>
    public string NewName(string parentPath, string baseName)
    {
        for (int i = 1; ; i++)
        {
            var candidate = baseName + i;
            var path = parentPath.Length == 0 ? candidate : parentPath + "." + candidate;
            if (Class.FindMember(path) == null) return candidate;
        }
    }

    /// <summary>VFP's default name prefix for a new object of a base class (TextBox → Text1, CommandButton → Command1).</summary>
    public static string DefaultNamePrefix(string className) => className.ToLowerInvariant() switch
    {
        "textbox" => "Text",
        "commandbutton" => "Command",
        "checkbox" => "Check",
        "combobox" => "Combo",
        "listbox" => "List",
        "editbox" => "Edit",
        "optiongroup" => "Optiongroup",
        "commandgroup" => "Commandgroup",
        "pageframe" => "Pageframe",
        "olecontrol" => "Olecontrol",
        _ => char.ToUpperInvariant(className[0]) + className[1..].ToLowerInvariant(),
    };

    /// <summary>Adds an object to a container (the class itself when <paramref name="parentPath"/> is empty). Returns its path.</summary>
    public string AddObject(string parentPath, string className, string? classLibrary = null, IEnumerable<KeyValuePair<string, string>>? properties = null, string? name = null)
    {
        var path = "";
        Transaction($"Add {className}", () =>
        {
            name ??= NewName(parentPath, DefaultNamePrefix(className));
            path = parentPath.Length == 0 ? name : parentPath + "." + name;
            var member = new MemberDocument { Path = path, Class = className, ClassLibrary = classLibrary };
            if (properties != null) foreach (var (k, v) in properties) member.Properties[k] = v;
            Class.Members.Add(member);
        });
        return path;
    }

    /// <summary>Removes an object, the objects inside it, and their method code.</summary>
    public void RemoveObject(string path) =>
        Transaction($"Delete {path}", () =>
        {
            bool Under(string p) => p.Equals(path, StringComparison.OrdinalIgnoreCase) || p.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase);
            Class.Members.RemoveAll(m => Under(m.Path));
            Class.Methods.RemoveAll(m => m.ObjectPath.Length > 0 && Under(m.ObjectPath));
            // Properties of implicit children stored on an ancestor (Pageframe1: Page1.Caption) go too.
            var (owner, rest) = Owner(path);
            if (rest.Length > 0)
                foreach (var key in ListOf(owner).Select(p => p.Key).Where(k => k.StartsWith(rest + ".", StringComparison.OrdinalIgnoreCase)).ToList())
                    ListOf(owner).Remove(key);
        });

    /// <summary>Renames an object: its path, the paths of objects inside it, and the names of its methods.</summary>
    public void RenameObject(string path, string newName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(newName, @"^[A-Za-z_]\w*$")) throw new ArgumentException($"'{newName}' is not a valid name.");
        var member = Class.FindMember(path) ?? throw new ArgumentException($"{path} is not a declared object.");
        var newPath = member.ParentPath.Length == 0 ? newName : member.ParentPath + "." + newName;
        if (Class.FindMember(newPath) != null) throw new ArgumentException($"An object named {newName} already exists there.");
        Transaction($"Rename {member.Name}", () =>
        {
            string Map(string p) =>
                p.Equals(path, StringComparison.OrdinalIgnoreCase) ? newPath
                : p.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase) ? newPath + p[path.Length..] : p;
            foreach (var m in Class.Members) m.Path = Map(m.Path);
            foreach (var m in Class.Methods.Where(m => m.ObjectPath.Length > 0)) m.Name = Map(m.ObjectPath) + "." + m.MethodName;
        });
    }

    /// <summary>Moves an object to the end (front) or start (back) of the z-order among its siblings.</summary>
    public void ChangeZOrder(string path, bool toFront) =>
        Transaction(toFront ? "Bring to front" : "Send to back", () =>
        {
            var member = Class.FindMember(path) ?? throw new ArgumentException($"{path} is not a declared object.");
            var subtree = Class.Members.Where(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || m.Path.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var m in subtree) Class.Members.Remove(m);
            var siblings = Class.Members.Select((m, i) => (m, i)).Where(x => x.m.ParentPath.Equals(member.ParentPath, StringComparison.OrdinalIgnoreCase)).ToList();
            int at;
            if (toFront)
            {
                // After the last sibling and everything inside it.
                if (siblings.Count == 0) at = Class.Members.Count;
                else
                {
                    var last = siblings[^1].m.Path;
                    at = Class.Members.FindLastIndex(m => m.Path.Equals(last, StringComparison.OrdinalIgnoreCase) || m.Path.StartsWith(last + ".", StringComparison.OrdinalIgnoreCase)) + 1;
                }
            }
            else at = siblings.Count == 0 ? Class.Members.Count : siblings[0].i;
            Class.Members.InsertRange(at, subtree);
        });

    // ---- Methods -----------------------------------------------------------------------------------

    private static string MethodName(string objectPath, string method) => objectPath.Length == 0 ? method : objectPath + "." + method;

    public string? GetMethod(string objectPath, string method) => Class.FindMethod(MethodName(objectPath, method))?.Code;

    /// <summary>Sets a method's code; empty code removes the method.</summary>
    public void SetMethod(string objectPath, string method, string code) =>
        Transaction($"Edit {method}", () =>
        {
            var name = MethodName(objectPath, method);
            var existing = Class.FindMethod(name);
            if (code.Trim().Length == 0)
            {
                if (existing != null) Class.Methods.Remove(existing);
                return;
            }
            if (existing == null) Class.Methods.Add(existing = new MethodDocument { Name = name });
            existing.Code = code;
        });

    /// <summary>Methods with code for an object (method names without the object path).</summary>
    public IEnumerable<string> MethodsWithCode(string objectPath) =>
        Class.Methods.Where(m => m.ObjectPath.Equals(objectPath, StringComparison.OrdinalIgnoreCase) && m.Body.Count > 0).Select(m => m.MethodName);

    // ---- Class-level members (Class Designer "New Property/Method") ----------------------------------

    public void AddProperty(string name, string initialValue = ".F.", string? visibility = null) =>
        Transaction($"New property {name}", () =>
        {
            Class.Properties[name] = initialValue;
            SetVisibility(name, visibility);
        });

    public void AddMethod(string name, string? visibility = null) =>
        Transaction($"New method {name}", () =>
        {
            if (Class.FindMethod(name) == null) Class.Methods.Add(new MethodDocument { Name = name, Visibility = visibility });
        });

    public void SetVisibility(string memberName, string? visibility) =>
        Transaction("Visibility", () =>
        {
            Class.Protected.RemoveAll(n => n.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            Class.Hidden.RemoveAll(n => n.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            if (Class.FindMethod(memberName) is { } m) { m.Visibility = visibility; return; }
            if (visibility == "PROTECTED") Class.Protected.Add(memberName);
            else if (visibility == "HIDDEN") Class.Hidden.Add(memberName);
        });

    // ---- Clipboard -------------------------------------------------------------------------------

    /// <summary>Copies objects (with the objects inside them and their code) as a small class document.</summary>
    public string Copy(IEnumerable<string> paths)
    {
        var clip = new ClassDocument { Name = "clipboard", ParentClass = "Custom" };
        foreach (var path in paths)
        {
            foreach (var m in Class.Members.Where(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || m.Path.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)))
            {
                var parentLen = path.Contains('.') ? path.LastIndexOf('.') + 1 : 0;
                var copy = new MemberDocument { Path = m.Path[parentLen..], Class = m.Class, ClassLibrary = m.ClassLibrary };
                foreach (var (k, v) in m.Properties) copy.Properties[k] = v;
                clip.Members.Add(copy);
            }
            foreach (var method in Class.Methods.Where(x => x.ObjectPath.Equals(path, StringComparison.OrdinalIgnoreCase) || x.ObjectPath.StartsWith(path + ".", StringComparison.OrdinalIgnoreCase)))
            {
                var parentLen = path.Contains('.') ? path.LastIndexOf('.') + 1 : 0;
                var copy = new MethodDocument { Name = method.Name[parentLen..], Code = method.Code, Visibility = method.Visibility };
                clip.Methods.Add(copy);
            }
        }
        var file = new ClassFile { Kind = ClassFileKind.ClassLibrary };
        file.Classes.Add(clip);
        return ClassFileWriter.Write(file);
    }

    /// <summary>Pastes copied objects into a container, renaming them when names are taken and offsetting positions. Returns the new top-level paths.</summary>
    public List<string> Paste(string clipboardText, string parentPath, int offset = 8)
    {
        var added = new List<string>();
        var clip = ClassFileReader.Parse(clipboardText, ClassFileKind.ClassLibrary).Classes.FirstOrDefault();
        if (clip == null) return added;
        Transaction("Paste", () =>
        {
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in clip.Members)
            {
                var top = m.Path.Split('.')[0];
                if (!renames.ContainsKey(top))
                {
                    var baseName = System.Text.RegularExpressions.Regex.Replace(top, @"\d+$", "");
                    var target = parentPath.Length == 0 ? top : parentPath + "." + top;
                    renames[top] = Class.FindMember(target) == null ? top : NewName(parentPath, baseName.Length > 0 ? baseName : top);
                }
                var rel = renames[top] + m.Path[top.Length..];
                var path = parentPath.Length == 0 ? rel : parentPath + "." + rel;
                var copy = new MemberDocument { Path = path, Class = m.Class, ClassLibrary = m.ClassLibrary };
                foreach (var (k, v) in m.Properties) copy.Properties[k] = v;
                if (!m.Path.Contains('.'))
                {
                    added.Add(path);
                    foreach (var p in new[] { "Left", "Top" })
                        if (copy.Properties[p] is { } pos && Literal.TryParse(pos) is { Kind: JoePro.Core.ValueKind.Number } n)
                            copy.Properties[p] = Literal.FormatNumber(n.AsNumber + offset);
                }
                Class.Members.Add(copy);
            }
            foreach (var method in clip.Methods)
            {
                var top = method.Name.Split('.')[0];
                if (!renames.TryGetValue(top, out var newTop)) continue;
                var name = (parentPath.Length == 0 ? "" : parentPath + ".") + newTop + method.Name[top.Length..];
                Class.Methods.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                Class.Methods.Add(new MethodDocument { Name = name, Code = method.Code, Visibility = method.Visibility });
            }
        });
        return added;
    }

    /// <summary>A new, empty form document (CREATE FORM).</summary>
    public static ClassFile NewForm(string name)
    {
        var file = new ClassFile { Kind = ClassFileKind.Form };
        var cls = new ClassDocument { Name = name, ParentClass = "Form" };
        cls.Properties["Caption"] = Literal.Quote("Form1");
        cls.Properties["Width"] = "375";
        cls.Properties["Height"] = "250";
        file.Classes.Add(cls);
        return file;
    }
}
