namespace JoePro.Documents;

public enum ClassFileKind { Form, ClassLibrary }

/// <summary>
/// A form (.jpform) or class library (.jpclass): one or more classes written in a strict, canonical subset
/// of DEFINE CLASS syntax. Property values are kept as FoxPro expression text, exactly as written.
/// </summary>
public sealed class ClassFile
{
    public ClassFileKind Kind { get; set; }
    /// <summary>#INCLUDE files (header constants used by method code).</summary>
    public List<string> Includes { get; } = new();
    public List<ClassDocument> Classes { get; } = new();

    public ClassDocument? Find(string name) => Classes.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One DEFINE CLASS block.</summary>
public sealed class ClassDocument
{
    public string Name { get; set; } = "";
    public string ParentClass { get; set; } = "Custom";
    public string? ParentLibrary { get; set; }
    public bool OlePublic { get; set; }
    /// <summary>Class Info: a description of the class, its toolbar icon and its container icon.</summary>
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? ContainerIcon { get; set; }
    /// <summary>Descriptions of the custom properties, arrays and methods the class defines (shown in the property sheet).</summary>
    public Dictionary<string, string> MemberDescriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public PropertyList Properties { get; } = new();
    /// <summary>DIMENSION name[rows[, cols]] members: name → subscript text ("1" or "3,2").</summary>
    public List<(string Name, string Dimensions)> Arrays { get; } = new();
    public List<string> Protected { get; } = new();
    public List<string> Hidden { get; } = new();
    public List<MemberDocument> Members { get; } = new();
    public List<MethodDocument> Methods { get; } = new();

    public MemberDocument? FindMember(string path) => Members.FirstOrDefault(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    public MethodDocument? FindMethod(string name) => Methods.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The property list of the class itself (empty path) or of a member.</summary>
    public PropertyList? PropertiesOf(string path) => path.Length == 0 ? Properties : FindMember(path)?.Properties;
}

/// <summary>ADD OBJECT path AS class [OF library] [WITH properties]. Member order is z-order and matters.</summary>
public sealed class MemberDocument
{
    /// <summary>Dotted path relative to the class: "txtName" or "pgfMain.Page1.grdOrders".</summary>
    public string Path { get; set; } = "";
    public string Class { get; set; } = "";
    public string? ClassLibrary { get; set; }
    public bool Protected { get; set; }
    public bool NoInit { get; set; }
    public PropertyList Properties { get; } = new();

    public string Name => Path.Contains('.') ? Path[(Path.LastIndexOf('.') + 1)..] : Path;
    public string ParentPath => Path.Contains('.') ? Path[..Path.LastIndexOf('.')] : "";
}

/// <summary>PROCEDURE [object.]method. The body is stored without its common indentation.</summary>
public sealed class MethodDocument
{
    /// <summary>"Init", or "txtName.Valid" for code written in the class for one of its members.</summary>
    public string Name { get; set; } = "";
    /// <summary>Parameter list written in the header (PROCEDURE x(a, b)); usually empty, with LPARAMETERS in the body.</summary>
    public string? Parameters { get; set; }
    public string? Visibility { get; set; }
    public bool IsFunction { get; set; }
    public List<string> Body { get; } = new();

    public string ObjectPath => Name.Contains('.') ? Name[..Name.LastIndexOf('.')] : "";
    public string MethodName => Name.Contains('.') ? Name[(Name.LastIndexOf('.') + 1)..] : Name;

    public string Code
    {
        get => string.Join("\n", Body);
        set
        {
            Body.Clear();
            var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
            while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
            Body.AddRange(ClassFileReader.Dedent(lines));
        }
    }
}

/// <summary>Ordered property assignments (name → expression text), with case-insensitive names.</summary>
public sealed class PropertyList : IEnumerable<KeyValuePair<string, string>>
{
    private readonly List<KeyValuePair<string, string>> _items = new();

    public int Count => _items.Count;

    public string? this[string name]
    {
        get => _items.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        set
        {
            var i = _items.FindIndex(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (value == null) { if (i >= 0) _items.RemoveAt(i); return; }
            if (i >= 0) _items[i] = new(_items[i].Key, value);
            else _items.Add(new(name, value));
        }
    }

    public bool Contains(string name) => _items.Any(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool Remove(string name) => _items.RemoveAll(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>Canonical order: case-insensitive alphabetical, so the text does not depend on edit history.</summary>
    public IEnumerable<KeyValuePair<string, string>> Canonical() =>
        _items.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.Ordinal);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
