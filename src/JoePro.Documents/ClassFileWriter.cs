using System.Text;

namespace JoePro.Documents;

/// <summary>
/// Writes a <see cref="ClassFile"/> canonically: LF line endings, four-space indentation, classes and properties
/// in alphabetical order, one property per line, members in z-order, methods grouped by object (class first, then
/// members in z-order) and sorted by name. The same document always produces the same bytes, and a change
/// to one property changes one line.
/// </summary>
public static class ClassFileWriter
{
    public const string FormHeader = "*-- Joe Pro form v1";
    public const string ClassLibraryHeader = "*-- Joe Pro class library v1";
    private const string Indent = "    ";

    public static string Write(ClassFile file)
    {
        var sb = new StringBuilder();
        sb.Append(file.Kind == ClassFileKind.Form ? FormHeader : ClassLibraryHeader).Append('\n');
        foreach (var inc in file.Includes) sb.Append("#INCLUDE ").Append(Literal.Quote(inc)).Append('\n');
        // Classes in alphabetical order, so the file does not depend on the order they were created in.
        foreach (var cls in file.Classes.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append('\n');
            WriteClass(sb, cls);
        }
        return sb.ToString();
    }

    public static void Save(ClassFile file, string path) => File.WriteAllText(path, Write(file), new UTF8Encoding(false));

    private static void WriteClass(StringBuilder sb, ClassDocument cls)
    {
        sb.Append("DEFINE CLASS ").Append(cls.Name).Append(" AS ").Append(cls.ParentClass);
        if (!string.IsNullOrEmpty(cls.ParentLibrary)) sb.Append(" OF ").Append(Literal.QuoteIfNeeded(cls.ParentLibrary));
        if (cls.OlePublic) sb.Append(" OLEPUBLIC");
        sb.Append('\n');
        // Class Info and member descriptions are comments, so the file stays plain DEFINE CLASS code.
        void Meta(string key, string? text)
        {
            if (!string.IsNullOrEmpty(text)) sb.Append(Indent).Append("*-- ").Append(key).Append(": ").Append(EscapeMeta(text)).Append('\n');
        }
        Meta("Description", cls.Description);
        Meta("Icon", cls.Icon);
        Meta("ContainerIcon", cls.ContainerIcon);
        foreach (var (name, text) in cls.MemberDescriptions.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase)) Meta("Member " + name, text);

        foreach (var (name, value) in cls.Properties.Canonical())
        {
            if (name.Equals("Name", StringComparison.OrdinalIgnoreCase) && Literal.IsString(value, cls.Name)) continue;
            sb.Append(Indent).Append(name).Append(" = ").Append(value).Append('\n');
        }
        foreach (var (name, dims) in cls.Arrays.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            sb.Append(Indent).Append("DIMENSION ").Append(name).Append('[').Append(dims).Append("]\n");
        if (cls.Protected.Count > 0) sb.Append(Indent).Append("PROTECTED ").Append(string.Join(", ", cls.Protected.Order(StringComparer.OrdinalIgnoreCase))).Append('\n');
        if (cls.Hidden.Count > 0) sb.Append(Indent).Append("HIDDEN ").Append(string.Join(", ", cls.Hidden.Order(StringComparer.OrdinalIgnoreCase))).Append('\n');

        foreach (var m in cls.Members)
        {
            sb.Append('\n');
            sb.Append(Indent).Append("ADD OBJECT ");
            if (m.Protected) sb.Append("PROTECTED ");
            sb.Append(m.Path).Append(" AS ").Append(m.Class);
            if (!string.IsNullOrEmpty(m.ClassLibrary)) sb.Append(" OF ").Append(Literal.QuoteIfNeeded(m.ClassLibrary));
            if (m.NoInit) sb.Append(" NOINIT");
            var props = m.Properties.Canonical()
                .Where(p => !(p.Key.Equals("Name", StringComparison.OrdinalIgnoreCase) && Literal.IsString(p.Value, m.Name))).ToList();
            if (props.Count > 0)
            {
                sb.Append(" WITH ;\n");
                for (int i = 0; i < props.Count; i++)
                {
                    sb.Append(Indent).Append(Indent).Append(props[i].Key).Append(" = ").Append(props[i].Value);
                    sb.Append(i < props.Count - 1 ? ", ;\n" : "\n");
                }
            }
            else sb.Append('\n');
        }

        foreach (var method in OrderedMethods(cls))
        {
            sb.Append('\n');
            sb.Append(Indent);
            if (method.Visibility != null) sb.Append(method.Visibility).Append(' ');
            sb.Append(method.IsFunction ? "FUNCTION " : "PROCEDURE ").Append(method.Name);
            if (!string.IsNullOrEmpty(method.Parameters)) sb.Append('(').Append(method.Parameters).Append(')');
            sb.Append('\n');
            foreach (var line in method.Body)
                sb.Append(line.Length == 0 ? "" : Indent + Indent + line).Append('\n');
            sb.Append(Indent).Append(method.IsFunction ? "ENDFUNC" : "ENDPROC").Append('\n');
        }
        sb.Append("ENDDEFINE\n");
    }

    /// <summary>Metadata comments are one line: backslashes and line breaks are escaped.</summary>
    internal static string EscapeMeta(string text) => text.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");

    internal static string UnescapeMeta(string text)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                sb.Append(text[i] == 'n' ? '\n' : text[i]);
            }
            else sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>Methods of the class itself first, then of each member in z-order; alphabetical within an object.</summary>
    public static IEnumerable<MethodDocument> OrderedMethods(ClassDocument cls)
    {
        int Rank(MethodDocument m)
        {
            if (m.ObjectPath.Length == 0) return 0;
            var i = cls.Members.FindIndex(x => x.Path.Equals(m.ObjectPath, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i + 1 : int.MaxValue; // implicit children (Page1, Column1…) after declared members
        }
        return cls.Methods
            .OrderBy(Rank)
            .ThenBy(m => m.ObjectPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.MethodName, StringComparer.OrdinalIgnoreCase);
    }
}
