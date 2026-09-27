using System.Text;
using JoePro.Core;
using JoePro.Documents.Yaml;
using JoePro.Legacy.Formats;

namespace JoePro.Documents.Projects;

/// <summary>The kinds of files a project tracks (the Project Manager's categories come from these).</summary>
public enum ProjectFileType { Program, Form, Report, Label, ClassLibrary, Menu, Query, Database, Table, Text, Library, Application, Other }

public sealed class ProjectFile
{
    /// <summary>Path relative to the project folder, with forward slashes.</summary>
    public string Path { get; set; } = "";
    public ProjectFileType Type { get; set; }
    /// <summary>Excluded files are not built into the application (data files that change at run time, for example).</summary>
    public bool Exclude { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// A project (.jpproj): its files, main program, build settings and version information. Stored as canonical YAML.
/// </summary>
public sealed class ProjectDocument
{
    public string Name { get; set; } = "";
    public string? Main { get; set; }
    /// <summary>Build output name (without extension); defaults to the project name.</summary>
    public string? Output { get; set; }
    public bool Debug { get; set; } = true;
    public string? Icon { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string? Description { get; set; }
    public string? Company { get; set; }
    public string? Product { get; set; }
    public string? Copyright { get; set; }
    public List<ProjectFile> Files { get; } = new();

    public ProjectFile? Find(string path) => Files.FirstOrDefault(f => f.Path.Equals(Normalize(path), StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p;
    }

    /// <summary>The file type for a file name (Joe Pro and legacy extensions).</summary>
    public static ProjectFileType TypeOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".prg" or ".mpr" or ".h" => ProjectFileType.Program,
        ".jpform" or ".scx" => ProjectFileType.Form,
        ".jpreport" or ".frx" => ProjectFileType.Report,
        ".jplabel" or ".lbx" => ProjectFileType.Label,
        ".jpclass" or ".vcx" => ProjectFileType.ClassLibrary,
        ".jpmenu" or ".mnx" => ProjectFileType.Menu,
        ".jpquery" or ".qpr" => ProjectFileType.Query,
        ".jpdb" or ".dbc" => ProjectFileType.Database,
        ".jpt" or ".dbf" => ProjectFileType.Table,
        ".txt" or ".md" or ".xml" or ".json" or ".ini" => ProjectFileType.Text,
        ".fll" or ".dll" => ProjectFileType.Library,
        ".jpapp" or ".app" or ".exe" => ProjectFileType.Application,
        _ => ProjectFileType.Other,
    };

    /// <summary>Adds a file (relative to the project folder) if it is not already in the project. Data files start excluded.</summary>
    public ProjectFile Add(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        if (Find(path) is { } existing) return existing;
        var type = TypeOf(path);
        var file = new ProjectFile { Path = path, Type = type, Exclude = type is ProjectFileType.Table or ProjectFileType.Database };
        Files.Add(file);
        if (Main == null && type == ProjectFileType.Program && !path.EndsWith(".h", StringComparison.OrdinalIgnoreCase)) Main = path;
        return file;
    }

    /// <summary>Packages the project uses: name, version range (1.2.0, ^1.2, ~1.2.3, >=1.0, *) and an optional source.</summary>
    public List<PackageReference> Dependencies { get; } = new();
    /// <summary>The package registry (a folder or an http(s) URL) for dependencies without their own source.</summary>
    public string? Registry { get; set; }

    public static ProjectDocument Load(string path) => ProjectSerializer.Parse(File.ReadAllText(path));
    public void Save(string path) => File.WriteAllText(path, ProjectSerializer.Write(this), new UTF8Encoding(false));
}

/// <summary>
/// A dependency on a package. <see cref="Source"/> is null for the project's registry, a folder path for a local
/// package or library project, or git+URL[#tag] for a repository.
/// </summary>
public sealed record PackageReference(string Name, string Version, string? Source = null);

public static class ProjectSerializer
{
    public const string Header = "Joe Pro project v1";
    private static string Word(ProjectFileType t) { var s = t.ToString(); return char.ToLowerInvariant(s[0]) + s[1..]; }

    public static string Write(ProjectDocument p)
    {
        var root = new YamlMap().Set("name", p.Name).Set("main", p.Main).Set("output", p.Output).Set("debug", p.Debug, true).Set("icon", p.Icon);
        root.SetNode("version", new YamlMap().Set("number", p.Version, "1.0.0").Set("description", p.Description).Set("company", p.Company)
            .Set("product", p.Product).Set("copyright", p.Copyright));
        var files = new YamlSeq();
        foreach (var f in p.Files.OrderBy(f => f.Type).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var m = new YamlMap().Set("path", f.Path);
            if (f.Type != ProjectDocument.TypeOf(f.Path)) m.SetWord("type", Word(f.Type));
            m.Set("exclude", f.Exclude).Set("comment", f.Comment);
            files.Add(m);
        }
        root.SetNode("files", files);
        root.Set("registry", p.Registry);
        if (p.Dependencies.Count > 0)
        {
            var deps = new YamlSeq();
            foreach (var d in p.Dependencies.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var m = new YamlMap().Set("name", d.Name);
                m.Add("version", new YamlScalar(d.Version, quoted: true));
                m.Set("source", d.Source);
                deps.Add(m);
            }
            root.SetNode("dependencies", deps);
        }
        return YamlText.Write(root, Header);
    }

    public static ProjectDocument Parse(string text)
    {
        var root = YamlText.Parse(text);
        root.CheckKeys("name", "main", "output", "debug", "icon", "version", "files", "registry", "dependencies");
        var p = new ProjectDocument { Name = root.Str("name", ""), Main = root.Str("main"), Output = root.Str("output"), Debug = root.Bool("debug", true), Icon = root.Str("icon"), Registry = root.Str("registry") };
        foreach (var node in root.Seq("dependencies"))
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a dependency should be a mapping.");
            m.CheckKeys("name", "version", "source");
            p.Dependencies.Add(new PackageReference(m.Str("name") ?? throw new FormatException($"Line {m.Line}: a dependency needs a name."), m.Str("version", "*"), m.Str("source")));
        }
        if (root.Map("version") is { } v)
        {
            v.CheckKeys("number", "description", "company", "product", "copyright");
            p.Version = v.Str("number", "1.0.0"); p.Description = v.Str("description"); p.Company = v.Str("company");
            p.Product = v.Str("product"); p.Copyright = v.Str("copyright");
        }
        foreach (var node in root.Seq("files"))
        {
            var m = node as YamlMap ?? throw new FormatException($"Line {node.Line}: a project file should be a mapping.");
            m.CheckKeys("path", "type", "exclude", "comment");
            var path = m.Str("path") ?? throw new FormatException($"Line {m.Line}: a project file needs a path.");
            var type = m.Str("type") is { } t
                ? Enum.TryParse<ProjectFileType>(t, true, out var parsed) ? parsed : throw new FormatException($"Line {m.Line}: '{t}' is not a file type.")
                : ProjectDocument.TypeOf(path);
            p.Files.Add(new ProjectFile { Path = path, Type = type, Exclude = m.Bool("exclude"), Comment = m.Str("comment") });
        }
        return p;
    }
}

public sealed class ProjectConversion
{
    public required ProjectDocument Document { get; init; }
    public List<ConversionFinding> Findings { get; } = new();
}

/// <summary>
/// Converts a VFP project (.PJX/.PJT) to .jpproj. File names become relative, forward-slash paths; with
/// <c>mapConverted</c>, legacy designer files point to their converted Joe Pro files (.scx → .jpform, …).
/// </summary>
public static class LegacyProjectConverter
{
    public static string ConvertedName(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".scx" => System.IO.Path.ChangeExtension(path, ".jpform"),
        ".vcx" => System.IO.Path.ChangeExtension(path, ".jpclass"),
        ".frx" => System.IO.Path.ChangeExtension(path, ".jpreport"),
        ".lbx" => System.IO.Path.ChangeExtension(path, ".jplabel"),
        ".mnx" => System.IO.Path.ChangeExtension(path, ".jpmenu"),
        ".qpr" => System.IO.Path.ChangeExtension(path, ".jpquery"),
        ".dbf" => System.IO.Path.ChangeExtension(path, ".jpt"),
        ".dbc" => System.IO.Path.ChangeExtension(path, ".jpdb"),
        _ => path,
    };

    public static ProjectConversion Convert(string pjxPath, bool mapConverted = true)
    {
        var file = System.IO.Path.GetFileName(pjxPath);
        var doc = new ProjectDocument { Name = System.IO.Path.GetFileNameWithoutExtension(pjxPath) };
        var result = new ProjectConversion { Document = doc };
        using var t = DbfTable.Open(pjxPath);
        var names = t.Fields.Select(f => f.Name.ToUpperInvariant()).ToList();
        string S(DbfRecord r, string col) => names.IndexOf(col) is var i and >= 0 && r.Values[i].Kind == ValueKind.Character ? r.Values[i].AsString.TrimEnd('\0', ' ', '\r', '\n') : "";
        bool L(DbfRecord r, string col) => names.IndexOf(col) is var i and >= 0 && r.Values[i].Kind == ValueKind.Logical && r.Values[i].AsBool;
        foreach (var r in t.Records())
        {
            if (r.Deleted) continue;
            var type = S(r, "TYPE");
            var name = S(r, "NAME").Replace('\\', '/');
            if (type == "H")
            {
                if (S(r, "OUTFILE") is { Length: > 0 } outfile && !outfile.StartsWith('<'))
                    doc.Output = System.IO.Path.GetFileNameWithoutExtension(outfile.Replace('\\', '/'));
                doc.Debug = L(r, "DEBUG");
                continue;
            }
            if (name.Length == 0) continue;
            if (System.IO.Path.IsPathRooted(name) || name.Contains(':'))
                result.Findings.Add(new(FindingStatus.NeedsReview, file, $"{name} has an absolute path; it was kept as it is."));
            else if (name.StartsWith("../"))
                result.Findings.Add(new(FindingStatus.Info, file, $"{name} is outside the project folder."));
            var mapped = mapConverted ? ConvertedName(name) : name;
            var fileType = type switch
            {
                "P" => ProjectFileType.Program, "K" => ProjectFileType.Form, "R" => ProjectFileType.Report, "B" => ProjectFileType.Label,
                "V" => ProjectFileType.ClassLibrary, "M" => ProjectFileType.Menu, "Q" => ProjectFileType.Query, "d" => ProjectFileType.Database,
                "D" => ProjectFileType.Table, "T" => ProjectFileType.Text, "L" => ProjectFileType.Library, "Z" => ProjectFileType.Application,
                _ => ProjectDocument.TypeOf(name),
            };
            var pf = new ProjectFile { Path = mapped, Type = fileType, Exclude = L(r, "EXCLUDE") };
            if (S(r, "COMMENTS") is { Length: > 0 } comment) pf.Comment = comment;
            if (doc.Find(mapped) != null) continue;
            doc.Files.Add(pf);
            if (L(r, "MAINPROG")) doc.Main = mapped;
            if (fileType == ProjectFileType.Library)
                result.Findings.Add(new(FindingStatus.Unsupported, file, $"{name} is a FoxPro API library (FLL); it cannot be loaded by Joe Pro."));
        }
        return result;
    }
}
