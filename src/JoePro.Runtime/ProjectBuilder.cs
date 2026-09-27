using System.IO.Compression;
using System.Text;
using System.Text.Json;
using JoePro.Documents;
using JoePro.Documents.Menus;
using JoePro.Documents.Projects;
using JoePro.Documents.Reports;
using JoePro.Language;

namespace JoePro.Runtime;

public sealed record BuildError(string File, int Line, string Message);

public sealed class BuildResult
{
    public List<BuildError> Errors { get; } = new();
    /// <summary>Problems that do not stop the build (a missing text or other non-code file).</summary>
    public List<BuildError> Warnings { get; } = new();
    public List<string> Files { get; } = new();
    public string? Output { get; set; }
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>
/// BUILD PROJECT / APP / EXE: checks every file of a project (programs compile, forms, class libraries, menus and
/// reports load) and packages the included files into a .jpapp application (a zip with a manifest). An EXE build is
/// a folder with the application and launchers for Windows and Unix.
/// </summary>
public static class ProjectBuilder
{
    public const string ManifestName = "joepro-app.json";

    public sealed record Manifest(string Name, string Main, string Version, bool Debug, string[] Files);

    /// <summary>Compiles and loads every file that is not excluded; returns the errors (file, line, message).</summary>
    public static BuildResult Check(ProjectDocument project, string projectDir)
    {
        var result = new BuildResult();
        foreach (var f in project.Files)
        {
            if (Resolve(projectDir, f.Path) is not { } path)
            {
                if (f.Exclude) continue;
                if (f.Type is ProjectFileType.Text or ProjectFileType.Other) result.Warnings.Add(new(f.Path, 0, "File does not exist; it was left out of the application."));
                else result.Errors.Add(new(f.Path, 0, "File does not exist."));
                continue;
            }
            if (f.Exclude) continue;
            result.Files.Add(RelativeTo(projectDir, path, f.Path));
            try
            {
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".prg" or ".mpr" or ".qpr":
                        Parser.ParseProgram(File.ReadAllText(path), "X", path, inc => Include(inc, path, projectDir));
                        break;
                    case ".jpform" or ".jpclass":
                        ClassFileReader.Load(path);
                        Parser.ParseProgram(File.ReadAllText(path), "X", path, inc => Include(inc, path, projectDir));
                        break;
                    case ".scx" or ".vcx":
                        Parser.ParseProgram(ClassFileWriter.Write(LegacyFormConverter.Convert(path).File), "X", path, inc => Include(inc, path, projectDir));
                        break;
                    case ".jpmenu":
                        Parser.ParseProgram(MenuGenerator.Generate(MenuDocument.Load(path), path), "X", path);
                        break;
                    case ".mnx":
                        Parser.ParseProgram(MenuGenerator.Generate(LegacyMenuConverter.Convert(path).Document, path), "X", path);
                        break;
                    case ".jpquery":
                        Parser.ParseProgram(JoePro.Documents.Queries.QueryDocument.Load(path).RunCode(), "X", path);
                        break;
                    case ".jpreport" or ".jplabel":
                        ReportDocument.Load(path);
                        break;
                    case ".frx" or ".lbx":
                        LegacyReportConverter.Convert(path);
                        break;
                }
            }
            catch (CompileException ex) { result.Errors.Add(new(f.Path, ex.Line, ex.Message)); }
            catch (FormatException ex) { result.Errors.Add(new(f.Path, 0, ex.Message)); }
            catch (IOException ex) { result.Errors.Add(new(f.Path, 0, ex.Message)); }
        }
        if (project.Main == null) result.Errors.Add(new(project.Name, 0, "The project has no main program (set one in the Project Manager)."));
        else if (project.Find(project.Main) is not { Exclude: false }) result.Errors.Add(new(project.Main, 0, "The main program is not in the project or is excluded."));
        return result;
    }

    /// <summary>Finds a project file ignoring case (FoxPro projects come from case-insensitive file systems).</summary>
    public static string? Resolve(string projectDir, string relPath) =>
        JoePro.Data.DataSession.FindIgnoringCase(Path.GetFullPath(Path.Combine(projectDir, relPath)));

    // Files inside the project folder keep their on-disk spelling in the application; others keep the project's.
    private static string RelativeTo(string projectDir, string path, string fallback)
    {
        var rel = Path.GetRelativePath(projectDir, path).Replace('\\', '/');
        return rel.StartsWith("..") || Path.IsPathRooted(rel) ? fallback : rel;
    }

    private static string? Include(string name, string file, string projectDir)
    {
        foreach (var dir in new[] { Path.GetDirectoryName(file)!, projectDir })
            if (JoePro.Data.DataSession.FindIgnoringCase(Path.Combine(dir, name.Replace('\\', '/'))) is { } p)
                return File.ReadAllText(p);
        return null;
    }

    /// <summary>BUILD APP: checks the project and writes name.jpapp. Errors are also written to name.err.</summary>
    public static BuildResult BuildApp(ProjectDocument project, string projectDir, string output)
    {
        var result = Check(project, projectDir);
        var errFile = Path.ChangeExtension(output, ".err");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        static string Line(BuildError e, string prefix = "") => $"{prefix}{e.File}{(e.Line > 0 ? $"({e.Line})" : "")}: {e.Message}";
        if (!result.Succeeded || result.Warnings.Count > 0)
            File.WriteAllText(errFile, string.Join("\n", result.Errors.Select(e => Line(e)).Concat(result.Warnings.Select(w => Line(w, "Warning: ")))) + "\n");
        else if (File.Exists(errFile)) File.Delete(errFile);
        if (!result.Succeeded) return result;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (File.Exists(output)) File.Delete(output);
        using (var zip = ZipFile.Open(output, ZipArchiveMode.Create))
        {
            foreach (var f in result.Files)
                zip.CreateEntryFromFile(Path.Combine(projectDir, f), f.Replace('\\', '/'), CompressionLevel.Optimal);
            // Header files next to included programs travel with them.
            foreach (var h in Directory.EnumerateFiles(projectDir, "*.h", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projectDir, h).Replace('\\', '/');
                if (!result.Files.Contains(rel, StringComparer.OrdinalIgnoreCase) && !rel.StartsWith("..")) zip.CreateEntryFromFile(h, rel);
            }
            var manifest = new Manifest(project.Name, project.Main!, project.Version, project.Debug, result.Files.ToArray());
            var entry = zip.CreateEntry(ManifestName);
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        result.Output = output;
        return result;
    }

    /// <summary>
    /// BUILD EXE: a folder with the application and launchers (name.cmd, name.sh) that start it with the Joe Pro
    /// runtime (joepro-app for windowed applications, else joepro run).
    /// </summary>
    public static BuildResult BuildExe(ProjectDocument project, string projectDir, string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        var name = Path.GetFileName(outputFolder.TrimEnd('/', '\\'));
        var result = BuildApp(project, projectDir, Path.Combine(outputFolder, name + ".jpapp"));
        if (!result.Succeeded) return result;
        File.WriteAllText(Path.Combine(outputFolder, name + ".cmd"),
            $"@echo off\r\nset APPDIR=%~dp0\r\nwhere joepro-app >nul 2>nul && (joepro-app \"%APPDIR%{name}.jpapp\" %*) || (joepro run \"%APPDIR%{name}.jpapp\" %*)\r\n");
        var sh = Path.Combine(outputFolder, name + ".sh");
        File.WriteAllText(sh, $"#!/bin/sh\nAPPDIR=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\nif command -v joepro-app >/dev/null 2>&1; then exec joepro-app \"$APPDIR/{name}.jpapp\" \"$@\"; else exec joepro run \"$APPDIR/{name}.jpapp\" \"$@\"; fi\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        // Excluded files (data) are copied next to the application so it runs as is.
        foreach (var f in project.Files.Where(f => f.Exclude))
        {
            if (Resolve(projectDir, f.Path) is not { } src || f.Path.StartsWith("..")) continue;
            var dest = Path.Combine(outputFolder, RelativeTo(projectDir, src, f.Path));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            foreach (var companion in Directory.EnumerateFiles(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + ".*").Where(c => c != src))
                File.Copy(companion, Path.Combine(Path.GetDirectoryName(dest)!, Path.GetFileName(companion)), overwrite: true);
        }
        result.Output = outputFolder;
        return result;
    }

    /// <summary>Extracts a .jpapp into a cache folder (reused while the file is unchanged) and returns the folder and manifest.</summary>
    public static (string Folder, Manifest Manifest) Extract(string appPath)
    {
        var info = new FileInfo(appPath);
        var key = $"{Path.GetFileNameWithoutExtension(appPath)}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
        var folder = Path.Combine(Path.GetTempPath(), "joepro-apps", key);
        if (!File.Exists(Path.Combine(folder, ManifestName)))
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            ZipFile.ExtractToDirectory(appPath, folder);
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(folder, ManifestName)))
                       ?? throw new FormatException($"{Path.GetFileName(appPath)} has no application manifest.");
        return (folder, manifest);
    }
}
