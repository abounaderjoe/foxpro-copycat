namespace JoePro.Migration;

/// <summary>Progress of a folder import: the step ("Converting form"), the file (relative), files done and the total.</summary>
public sealed record MigrationProgress(string Step, string File, int Done, int Total);

/// <summary>What a FoxPro application folder holds, before importing it (the migration wizard's first page).</summary>
public sealed class MigrationScan
{
    public string Folder { get; init; } = "";
    /// <summary>Count per kind, in display order ("Databases" → 1, "Tables" → 12, …). Kinds with no files are left out.</summary>
    public List<(string Kind, int Count)> Kinds { get; } = new();
    /// <summary>Projects found (.PJX), relative to the folder.</summary>
    public List<string> Projects { get; } = new();
    public int Files { get; set; }
    public long Bytes { get; set; }

    public int Count(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind).Count;

    private static readonly (string Kind, string[] Extensions)[] KindsByExtension =
    [
        ("Projects", [".pjx"]),
        ("Databases", [".dbc"]),
        ("Tables", [".dbf"]),
        ("Forms", [".scx"]),
        ("Class libraries", [".vcx"]),
        ("Reports", [".frx"]),
        ("Labels", [".lbx"]),
        ("Menus", [".mnx"]),
        ("Programs", [".prg", ".mpr", ".qpr"]),
        ("Header files", [".h"]),
    ];

    // Companion and compiled files are not counted as separate items.
    private static readonly HashSet<string> Companions =
        [".fpt", ".cdx", ".idx", ".dct", ".dcx", ".sct", ".vct", ".frt", ".lbt", ".mnt", ".pjt", ".fxp", ".spx", ".mpx", ".qpx", ".bak", ".tbk", ".err"];

    public static MigrationScan Scan(string folder)
    {
        var scan = new MigrationScan { Folder = Path.GetFullPath(folder) };
        var counts = new Dictionary<string, int>();
        int other = 0;
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(folder, f).Replace('\\', '/');
            if (rel.Split('/').Any(s => s is ".git" or ".svn" or ".hg" or ".vs")) continue;
            scan.Files++;
            try { scan.Bytes += new FileInfo(f).Length; } catch (IOException) { }
            var ext = Path.GetExtension(f).ToLowerInvariant();
            var kind = KindsByExtension.FirstOrDefault(k => k.Extensions.Contains(ext)).Kind;
            if (kind != null) counts[kind] = counts.GetValueOrDefault(kind) + 1;
            else if (!Companions.Contains(ext)) other++;
            if (ext == ".pjx") scan.Projects.Add(rel);
        }
        foreach (var (kind, _) in KindsByExtension)
            if (counts.TryGetValue(kind, out var n)) scan.Kinds.Add((kind, n));
        if (other > 0) scan.Kinds.Add(("Other files", other));
        scan.Projects.Sort(StringComparer.OrdinalIgnoreCase);
        return scan;
    }
}
