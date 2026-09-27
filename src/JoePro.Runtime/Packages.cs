using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JoePro.Core;
using JoePro.Documents.Projects;

namespace JoePro.Runtime;

/// <summary>Semantic versions (major.minor.patch) and the ranges dependencies use: 1.2.0, ^1.2, ~1.2.3, &gt;=1.0, *.</summary>
public static class SemVer
{
    public static bool TryParse(string s, out (int Major, int Minor, int Patch) v)
    {
        v = default;
        var core = s.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var n = new int[3];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out n[i]) || n[i] < 0) return false;
        v = (n[0], n[1], n[2]);
        return true;
    }

    public static int Compare(string a, string b)
    {
        TryParse(a, out var x);
        TryParse(b, out var y);
        return x.CompareTo(y);
    }

    public static bool IsValid(string s) => TryParse(s, out _);

    public static bool IsValidRange(string range)
    {
        var r = range.Trim();
        if (r is "" or "*" or "latest") return true;
        return TryParse(r.TrimStart('^', '~', '>', '=', '<'), out _);
    }

    public static bool Satisfies(string version, string range)
    {
        if (!TryParse(version, out var v)) return false;
        var r = range.Trim();
        if (r is "" or "*" or "latest") return true;
        if (r.StartsWith(">=")) return TryParse(r[2..], out var min) && v.CompareTo(min) >= 0;
        if (r.StartsWith('^'))
        {
            if (!TryParse(r[1..], out var b)) return false;
            return v.CompareTo(b) >= 0 && (b.Major > 0 ? v.Major == b.Major : v.Major == 0 && v.Minor == b.Minor);
        }
        if (r.StartsWith('~')) return TryParse(r[1..], out var t) && v.CompareTo(t) >= 0 && v.Major == t.Major && v.Minor == t.Minor;
        return TryParse(r.TrimStart('='), out var exact) && v == exact;
    }
}

/// <summary>A package's manifest (joepro-package.json inside a .jppkg).</summary>
public sealed record PackageManifest(string Name, string Version, string? Description, string[] Files, Dictionary<string, string> Dependencies);

/// <summary>A resolved package in packages.lock.json.</summary>
public sealed record LockedPackage(string Name, string Version, string Source, string Sha256);

/// <summary>
/// A static package registry (v1): a folder or an http(s) location holding index.json ({"name": ["1.0.0", …]})
/// and name/version/name-version.jppkg. Any file server can host one; publishing writes to folder registries.
/// </summary>
public sealed class PackageRegistry(string location)
{
    private static readonly HttpClient Http = new();
    public string Location { get; } = location.TrimEnd('/', '\\');
    public bool IsRemote => Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private string Url(string rel) => IsRemote ? Location + "/" + rel : Path.Combine(Location, rel.Replace('/', Path.DirectorySeparatorChar));

    private byte[]? Read(string rel)
    {
        if (!IsRemote) return File.Exists(Url(rel)) ? File.ReadAllBytes(Url(rel)) : null;
        using var response = Http.GetAsync(Url(rel)).GetAwaiter().GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
    }

    public Dictionary<string, List<string>> Index()
    {
        var bytes = Read("index.json");
        if (bytes == null) return new(StringComparer.OrdinalIgnoreCase);
        var index = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(bytes) ?? new();
        return new(index, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Versions(string name) =>
        Index().TryGetValue(name, out var v) ? v.Where(SemVer.IsValid).OrderBy(x => x, Comparer<string>.Create(SemVer.Compare)).ToList() : [];

    public string PackagePath(string name, string version) => $"{name.ToLowerInvariant()}/{version}/{name.ToLowerInvariant()}-{version}.jppkg";

    public byte[] Download(string name, string version) =>
        Read(PackagePath(name, version)) ?? throw new VfpException(ErrorCodes.FileDoesNotExist, $"Package {name} {version} is not in the registry {Location}.");

    public string Publish(PackageManifest manifest, byte[] package)
    {
        if (IsRemote) throw new VfpException(ErrorCodes.FeatureNotAvailable, "Publishing writes to a folder registry; copy the folder to the web server that hosts it.");
        var index = Index();
        if (index.TryGetValue(manifest.Name, out var existing) && existing.Contains(manifest.Version))
            throw new VfpException(ErrorCodes.FileInUse, $"{manifest.Name} {manifest.Version} is already published; publish a new version.");
        var path = Url(PackagePath(manifest.Name, manifest.Version));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, package);
        if (!index.ContainsKey(manifest.Name)) index[manifest.Name] = new();
        index[manifest.Name].Add(manifest.Version);
        Directory.CreateDirectory(Location);
        File.WriteAllText(Url("index.json"), JsonSerializer.Serialize(index.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(k => k.Key, k => k.Value), new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}

/// <summary>
/// Packages: a library project is packed (all its included files, and its dependencies) into a .jppkg and published
/// to a registry; a project lists dependencies with version ranges, and restore installs them into packages/ and
/// pins the exact versions in packages.lock.json so every machine gets the same ones. Programs find package files
/// through SET PATH (<see cref="UsePackages"/>), and BUILD includes them in the application.
/// </summary>
public static class PackageManager
{
    public const string PackagesFolder = "packages";
    public const string LockFileName = "packages.lock.json";
    public const string ManifestName = "joepro-package.json";
    /// <summary>The registry used when a project names none (the JOEPRO_REGISTRY environment variable).</summary>
    public static string? DefaultRegistry => Environment.GetEnvironmentVariable("JOEPRO_REGISTRY");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // ---- Packing and publishing --------------------------------------------------------------------

    /// <summary>Packs a project's included files (not its data or its own packages) and its dependencies into a .jppkg.</summary>
    public static byte[] Pack(ProjectDocument project, string projectDir, out PackageManifest manifest)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(project.Name, "^[A-Za-z_][A-Za-z0-9_.-]*$")) throw new VfpException(ErrorCodes.InvalidArgument, $"'{project.Name}' is not a valid package name.");
        if (!SemVer.IsValid(project.Version)) throw new VfpException(ErrorCodes.InvalidArgument, $"'{project.Version}' is not a version number (major.minor.patch).");
        var check = ProjectBuilder.Check(project, projectDir);
        // A library needs no main program.
        var errors = check.Errors.Where(e => !e.Message.Contains("main program", StringComparison.OrdinalIgnoreCase)).ToList();
        if (errors.Count > 0) throw new VfpException(1, $"The project has {errors.Count} error(s): {errors[0].File}: {errors[0].Message}");
        var files = check.Files.Where(f => !f.StartsWith(PackagesFolder + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
        manifest = new PackageManifest(project.Name, project.Version, project.Description, files,
            project.Dependencies.Where(d => d.Source == null).ToDictionary(d => d.Name, d => d.Version, StringComparer.OrdinalIgnoreCase));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var f in files.OrderBy(f => f, StringComparer.Ordinal))
            {
                var entry = zip.CreateEntry(f, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);   // same bytes for the same files
                using var w = entry.Open();
                w.Write(File.ReadAllBytes(Path.Combine(projectDir, f)));
            }
            foreach (var h in Directory.EnumerateFiles(projectDir, "*.h", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectDir, h).Replace('\\', '/');
                if (files.Contains(rel, StringComparer.OrdinalIgnoreCase) || rel.StartsWith(PackagesFolder + "/", StringComparison.OrdinalIgnoreCase)) continue;
                var entry = zip.CreateEntry(rel);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var w = entry.Open();
                w.Write(File.ReadAllBytes(h));
            }
            var m = zip.CreateEntry(ManifestName);
            m.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var mw = m.Open();
            mw.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
        }
        return ms.ToArray();
    }

    public static string Publish(ProjectDocument project, string projectDir, string registry)
    {
        var bytes = Pack(project, projectDir, out var manifest);
        return new PackageRegistry(registry).Publish(manifest, bytes);
    }

    private static PackageManifest ReadManifest(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = zip.GetEntry(ManifestName) ?? throw new VfpException(ErrorCodes.FileAccessDenied, "The package has no manifest.");
        using var s = entry.Open();
        return JsonSerializer.Deserialize<PackageManifest>(s, JsonOptions) ?? throw new VfpException(ErrorCodes.FileAccessDenied, "The package manifest is not valid.");
    }

    // ---- Lock file ----------------------------------------------------------------------------------

    public static List<LockedPackage> ReadLock(string projectDir)
    {
        var path = Path.Combine(projectDir, LockFileName);
        if (!File.Exists(path)) return new();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("packages").EnumerateArray().Select(e => e.Deserialize<LockedPackage>(JsonOptions)!).ToList();
    }

    private static void WriteLock(string projectDir, IEnumerable<LockedPackage> packages) =>
        File.WriteAllText(Path.Combine(projectDir, LockFileName), JsonSerializer.Serialize(new { lockVersion = 1, packages = packages.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase) }, JsonOptions) + "\n");

    // ---- Restore ------------------------------------------------------------------------------------

    public sealed record RestoreResult(List<LockedPackage> Packages, List<string> Messages);

    /// <summary>
    /// Installs the project's dependencies (and theirs) into packages/, using the versions pinned in the lock file
    /// while they still satisfy the project's ranges (or the newest matching ones with <paramref name="update"/>),
    /// and writes the lock file. Packages are checked against the lock file's hashes.
    /// </summary>
    public static RestoreResult Restore(ProjectDocument project, string projectDir, bool update = false)
    {
        var locked = update ? new List<LockedPackage>() : ReadLock(projectDir);
        var resolved = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase);
        var ranges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        var registryLocation = project.Registry is { } r ? Path.IsPathRooted(r) || r.Contains("://") ? r : Path.GetFullPath(Path.Combine(projectDir, r)) : DefaultRegistry;
        var queue = new Queue<(string Name, string Range, string? Source, string From)>(project.Dependencies.Select(d => (d.Name, d.Version, d.Source, project.Name)));
        var root = Path.Combine(projectDir, PackagesFolder);
        while (queue.Count > 0)
        {
            var (name, range, source, from) = queue.Dequeue();
            if (!SemVer.IsValidRange(range)) throw new VfpException(ErrorCodes.InvalidArgument, $"'{range}' is not a version range ({name}).");
            if (!ranges.TryGetValue(name, out var list)) ranges[name] = list = new();
            list.Add(range);
            if (resolved.TryGetValue(name, out var already))
            {
                if (!SemVer.Satisfies(already.Version, range))
                    throw new VfpException(ErrorCodes.InvalidArgument, $"{from} needs {name} {range}, but {name} {already.Version} was chosen for {string.Join(", ", list.Take(list.Count - 1))}.");
                continue;
            }
            byte[] package;
            string version, where;
            if (source != null && source.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
            {
                (package, where) = PackFromGit(source);
                version = ReadManifest(package).Version;
            }
            else if (source != null)
            {
                var folder = Path.GetFullPath(Path.Combine(projectDir, source));
                var projectFile = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.jpproj").FirstOrDefault() : null;
                if (projectFile == null) throw new VfpException(ErrorCodes.FileDoesNotExist, $"{source} has no Joe Pro project to use as package {name}.");
                package = Pack(ProjectDocument.Load(projectFile), folder, out var m);
                version = m.Version;
                where = "folder:" + source.Replace('\\', '/');
            }
            else
            {
                if (registryLocation == null) throw new VfpException(ErrorCodes.InvalidArgument, $"No package registry is set for {name}: set the project's registry (or JOEPRO_REGISTRY), or give the dependency a source.");
                var registry = new PackageRegistry(registryLocation);
                var pinned = locked.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && l.Source == "registry" && SemVer.Satisfies(l.Version, range));
                var versions = registry.Versions(name);
                version = pinned?.Version ?? versions.LastOrDefault(v => SemVer.Satisfies(v, range))
                    ?? throw new VfpException(ErrorCodes.FileDoesNotExist, versions.Count == 0 ? $"Package {name} is not in the registry {registryLocation}." : $"No version of {name} matches {range} (available: {string.Join(", ", versions)}).");
                package = registry.Download(name, version);
                where = "registry";
                if (pinned != null && pinned.Sha256 != Sha256(package))
                    throw new VfpException(ErrorCodes.FileAccessDenied, $"{name} {version} in the registry differs from the one in {LockFileName} (hash mismatch).");
            }
            if (source != null && !SemVer.Satisfies(version, range))
                throw new VfpException(ErrorCodes.InvalidArgument, $"{source} holds {name} {version}, which does not match {range}.");
            var manifest = ReadManifest(package);
            if (!manifest.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new VfpException(ErrorCodes.InvalidArgument, $"The package for {name} is named {manifest.Name}.");
            var target = Path.Combine(root, name.ToLowerInvariant());
            var sha = Sha256(package);
            var marker = Path.Combine(target, ".joepro-package");
            if (!(File.Exists(marker) && File.ReadAllText(marker).Trim() == sha))
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(target);
                using (var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read)) zip.ExtractToDirectory(target);
                File.WriteAllText(marker, sha);
                messages.Add($"Installed {name} {version}.");
            }
            resolved[name] = new LockedPackage(manifest.Name, version, where, sha);
            foreach (var (dep, depRange) in manifest.Dependencies) queue.Enqueue((dep, depRange, null, $"{name} {version}"));
        }
        // Packages no longer needed are removed.
        if (Directory.Exists(root))
            foreach (var dir in Directory.EnumerateDirectories(root))
                if (!resolved.ContainsKey(Path.GetFileName(dir)) && File.Exists(Path.Combine(dir, ".joepro-package")))
                {
                    Directory.Delete(dir, recursive: true);
                    messages.Add($"Removed {Path.GetFileName(dir)}.");
                }
        WriteLock(projectDir, resolved.Values);
        return new RestoreResult(resolved.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(), messages);
    }

    /// <summary>git+URL[#ref]: clones the repository and packs the project in it.</summary>
    private static (byte[] Package, string Where) PackFromGit(string source)
    {
        var spec = source[4..];
        var hash = spec.IndexOf('#');
        var url = hash >= 0 ? spec[..hash] : spec;
        var gitRef = hash >= 0 ? spec[(hash + 1)..] : null;
        var dir = Path.Combine(Path.GetTempPath(), "joepro-git-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            var args = new List<string> { "clone", "--depth", "1", "--quiet" };
            if (gitRef != null) { args.Add("--branch"); args.Add(gitRef); }
            args.Add(url);
            args.Add(dir);
            var psi = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi) ?? throw new VfpException(ErrorCodes.FeatureNotAvailable, "git is not available.");
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new VfpException(ErrorCodes.FileDoesNotExist, $"git clone {url} failed: {err.Trim()}");
            var projectFile = Directory.EnumerateFiles(dir, "*.jpproj").FirstOrDefault() ?? throw new VfpException(ErrorCodes.FileDoesNotExist, $"{url} has no Joe Pro project at its root.");
            return (Pack(ProjectDocument.Load(projectFile), dir, out _), "git:" + spec);
        }
        catch (System.ComponentModel.Win32Exception) { throw new VfpException(ErrorCodes.FeatureNotAvailable, "git is not installed."); }
        finally { if (Directory.Exists(dir)) try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // ---- Add and remove ------------------------------------------------------------------------------

    /// <summary>
    /// Adds (or changes) a dependency and restores. Without a version, the newest in the registry is used with a ^ range
    /// (compatible updates). Returns the installed version.
    /// </summary>
    public static string Add(ProjectDocument project, string projectDir, string name, string? version = null, string? source = null)
    {
        if (version == null && source == null)
        {
            var registry = project.Registry ?? DefaultRegistry ?? throw new VfpException(ErrorCodes.InvalidArgument, "No package registry is set: set the project's registry (or JOEPRO_REGISTRY).");
            var loc = Path.IsPathRooted(registry) || registry.Contains("://") ? registry : Path.GetFullPath(Path.Combine(projectDir, registry));
            var latest = new PackageRegistry(loc).Versions(name).LastOrDefault() ?? throw new VfpException(ErrorCodes.FileDoesNotExist, $"Package {name} is not in the registry {loc}.");
            version = "^" + latest;
        }
        var old = project.Dependencies.FindIndex(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var reference = new PackageReference(name, version ?? "*", source);
        if (old >= 0) project.Dependencies[old] = reference; else project.Dependencies.Add(reference);
        // A changed range may need a newer version than the one pinned.
        var result = Restore(project, projectDir);
        return result.Packages.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Version;
    }

    public static void Remove(ProjectDocument project, string projectDir, string name)
    {
        if (project.Dependencies.RemoveAll(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0)
            throw new VfpException(ErrorCodes.InvalidArgument, $"The project does not use {name}.");
        Restore(project, projectDir);
    }

    // ---- Use ----------------------------------------------------------------------------------------

    /// <summary>The installed package folders of a project.</summary>
    public static IReadOnlyList<string> PackageFolders(string projectDir)
    {
        var root = Path.Combine(projectDir, PackagesFolder);
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(d => File.Exists(Path.Combine(d, ".joepro-package"))).OrderBy(d => d, StringComparer.Ordinal).ToList() : [];
    }

    /// <summary>Adds a project's package folders (and their subfolders) to SET PATH, so programs find package files.</summary>
    public static void UsePackages(Interpreter rt, string projectDir)
    {
        foreach (var folder in PackageFolders(projectDir))
            foreach (var dir in new[] { folder }.Concat(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)))
                if (!rt.Options.Path.Contains(dir, StringComparer.OrdinalIgnoreCase)) rt.Options.Path.Add(dir);
    }
}
