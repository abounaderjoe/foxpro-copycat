using System.Text.RegularExpressions;

namespace JoePro.Documents;

/// <summary>
/// Operations on whole class libraries, as in VFP's CREATE CLASSLIB, ADD CLASS, RENAME CLASS, REMOVE CLASS and
/// the Class Browser (redefine, copy between libraries). Libraries are .jpclass files; a legacy .vcx is read by
/// converting it in memory.
/// </summary>
public static class ClassLibrary
{
    public static ClassFile Empty() => new() { Kind = ClassFileKind.ClassLibrary };

    /// <summary>Loads a .jpclass file, or converts a .vcx file (the conversion's findings are returned).</summary>
    public static ClassFile Load(string path, out ConversionResult? conversion)
    {
        conversion = null;
        if (Path.GetExtension(path).Equals(".vcx", StringComparison.OrdinalIgnoreCase))
        {
            conversion = LegacyFormConverter.ConvertClassLibrary(path);
            return conversion.File;
        }
        var file = ClassFileReader.Load(path);
        file.Kind = ClassFileKind.ClassLibrary;
        return file;
    }

    public static ClassFile Load(string path) => Load(path, out _);

    private static void CheckName(string name)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) throw new ArgumentException($"'{name}' is not a valid class name.");
    }

    /// <summary>A new class in the library (CREATE CLASS name OF lib AS parent [FROM parentlib]).</summary>
    public static ClassDocument NewClass(ClassFile lib, string name, string parentClass, string? parentLibrary = null)
    {
        CheckName(name);
        if (lib.Find(name) != null) throw new ArgumentException($"Class {name} already exists in the library.");
        var cls = new ClassDocument { Name = name, ParentClass = parentClass, ParentLibrary = string.IsNullOrEmpty(parentLibrary) ? null : parentLibrary };
        if (parentClass.Equals("Form", StringComparison.OrdinalIgnoreCase) && parentLibrary == null)
        {
            cls.Properties["Caption"] = Literal.Quote("Form1");
            cls.Properties["Width"] = "375";
            cls.Properties["Height"] = "250";
        }
        lib.Classes.Add(cls);
        return cls;
    }

    /// <summary>RENAME CLASS: renames the class and the references to it inside the same library. Returns the number of references updated.</summary>
    public static int RenameClass(ClassFile lib, string oldName, string newName)
    {
        CheckName(newName);
        var cls = lib.Find(oldName) ?? throw new ArgumentException($"Class {oldName} is not in the library.");
        if (!oldName.Equals(newName, StringComparison.OrdinalIgnoreCase) && lib.Find(newName) != null)
            throw new ArgumentException($"Class {newName} already exists in the library.");
        cls.Name = newName;
        var updated = 0;
        foreach (var c in lib.Classes)
        {
            if (c.ParentLibrary == null && c.ParentClass.Equals(oldName, StringComparison.OrdinalIgnoreCase)) { c.ParentClass = newName; updated++; }
            foreach (var m in c.Members.Where(m => m.ClassLibrary == null && m.Class.Equals(oldName, StringComparison.OrdinalIgnoreCase)))
            {
                m.Class = newName;
                updated++;
            }
        }
        return updated;
    }

    /// <summary>Classes in the library that depend on a class (subclasses and classes that contain it).</summary>
    public static List<string> Dependents(ClassFile lib, string name) =>
        lib.Classes.Where(c => !c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                               && ((c.ParentLibrary == null && c.ParentClass.Equals(name, StringComparison.OrdinalIgnoreCase))
                                   || c.Members.Any(m => m.ClassLibrary == null && m.Class.Equals(name, StringComparison.OrdinalIgnoreCase))))
            .Select(c => c.Name).ToList();

    /// <summary>REMOVE CLASS. Returns the classes left depending on it (they will not load until fixed).</summary>
    public static List<string> RemoveClass(ClassFile lib, string name)
    {
        var cls = lib.Find(name) ?? throw new ArgumentException($"Class {name} is not in the library.");
        var dependents = Dependents(lib, name);
        lib.Classes.Remove(cls);
        return dependents;
    }

    /// <summary>A deep copy of a class.</summary>
    public static ClassDocument Clone(ClassDocument cls)
    {
        var file = Empty();
        file.Classes.Add(cls);
        return ClassFileReader.Parse(ClassFileWriter.Write(file), ClassFileKind.ClassLibrary).Classes[0];
    }

    /// <summary>
    /// ADD CLASS name OF source TO target: copies a class between libraries. A parent class that lived in the source
    /// library is then referenced there (<paramref name="sourceLibraryReference"/>, relative to the target).
    /// </summary>
    public static ClassDocument CopyClass(ClassFile source, string name, ClassFile target, string sourceLibraryReference, bool overwrite = false, string? newName = null)
    {
        var cls = source.Find(name) ?? throw new ArgumentException($"Class {name} is not in the source library.");
        var copy = Clone(cls);
        if (newName != null) { CheckName(newName); copy.Name = newName; }
        if (target.Find(copy.Name) is { } existing)
        {
            if (!overwrite) throw new ArgumentException($"Class {copy.Name} already exists in the target library.");
            target.Classes.Remove(existing);
        }
        if (copy.ParentLibrary == null && source.Find(copy.ParentClass) != null && !ReferenceEquals(source, target))
            copy.ParentLibrary = sourceLibraryReference;
        foreach (var m in copy.Members.Where(m => m.ClassLibrary == null && source.Find(m.Class) != null && !ReferenceEquals(source, target)))
            m.ClassLibrary = sourceLibraryReference;
        target.Classes.Add(copy);
        return copy;
    }

    /// <summary>Class Browser → Redefine: gives a class a new parent class, keeping its own members and code.</summary>
    public static void Redefine(ClassDocument cls, string parentClass, string? parentLibrary)
    {
        cls.ParentClass = parentClass;
        cls.ParentLibrary = string.IsNullOrEmpty(parentLibrary) ? null : parentLibrary;
    }

    /// <summary>
    /// Saves one class into a library file, keeping the other classes as they are on disk (so designers open on
    /// different classes of one library never overwrite each other's work).
    /// </summary>
    public static void SaveClass(string path, ClassDocument cls, IEnumerable<string>? includes = null, string? previousName = null)
    {
        var lib = File.Exists(path) && Path.GetExtension(path).Equals(".jpclass", StringComparison.OrdinalIgnoreCase) ? Load(path) : Empty();
        lib.Classes.RemoveAll(c => c.Name.Equals(previousName ?? cls.Name, StringComparison.OrdinalIgnoreCase) || c.Name.Equals(cls.Name, StringComparison.OrdinalIgnoreCase));
        lib.Classes.Add(Clone(cls));
        foreach (var inc in includes ?? [])
            if (!lib.Includes.Contains(inc, StringComparer.OrdinalIgnoreCase)) lib.Includes.Add(inc);
        ClassFileWriter.Save(lib, path);
    }

    /// <summary>The DEFINE CLASS … ENDDEFINE text of one class (Class Browser → View Class Code).</summary>
    public static string ClassCode(ClassDocument cls)
    {
        var file = Empty();
        file.Classes.Add(cls);
        var text = ClassFileWriter.Write(file);
        return text[(text.IndexOf('\n') + 1)..].TrimStart('\n');
    }

    /// <summary>Classes in hierarchy order: roots (parents outside the library) first, each followed by its subclasses.</summary>
    public static IEnumerable<(ClassDocument Class, int Depth)> Hierarchy(ClassFile lib)
    {
        var byParent = lib.Classes
            .Where(c => c.ParentLibrary == null && lib.Find(c.ParentClass) != null && !c.ParentClass.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
            .ToLookup(c => c.ParentClass, StringComparer.OrdinalIgnoreCase);
        var roots = lib.Classes.Where(c => !(c.ParentLibrary == null && lib.Find(c.ParentClass) != null && !c.ParentClass.Equals(c.Name, StringComparison.OrdinalIgnoreCase)));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<(ClassDocument, int)> Walk(ClassDocument c, int depth)
        {
            if (!seen.Add(c.Name)) yield break;
            yield return (c, depth);
            foreach (var child in byParent[c.Name].OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                foreach (var x in Walk(child, depth + 1)) yield return x;
        }
        foreach (var r in roots.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            foreach (var x in Walk(r, 0)) yield return x;
        // Cycles (a broken library) still list every class.
        foreach (var c in lib.Classes.Where(c => !seen.Contains(c.Name))) yield return (c, 0);
    }
}
