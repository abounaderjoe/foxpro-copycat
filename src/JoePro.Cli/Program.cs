using JoePro.Core;
using JoePro.Language;
using JoePro.Migration;
using JoePro.Runtime;

namespace JoePro.Cli;

/// <summary>
/// The Joe Pro command-line host: an interactive Command Window, program runner and migration tool.
/// The desktop IDE (Avalonia) hosts the same runtime.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        MigrationCommands.Register();
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "repl";
        try
        {
            return cmd switch
            {
                "repl" => Repl(),
                "run" when args.Length >= 2 => RunProgram(args[1], args.Skip(2).ToArray()),
                "exec" when args.Length >= 2 => Exec(string.Join(" ", args.Skip(1))),
                "import" when args.Length >= 2 => Import(args.Skip(1).ToArray()),
                "add" or "remove" or "restore" or "publish" or "pack" => Packages(cmd, args.Skip(1).ToArray()),
                "sync" => Sync(args.Skip(1).ToArray()),
                "version" or "--version" => Version(),
                "functions" => Functions(args),
                "docs" when args.Contains("--reference") => WriteText(JoePro.Tooling.LanguageReference.Markdown()),
                "lsp" => new JoePro.Tooling.LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput()).Run(),
                "dap" => new JoePro.Tooling.DapServer(Console.OpenStandardInput(), Console.OpenStandardOutput()).Run(),
                "help" or "--help" or "-h" => Help(),
                _ => Help(1),
            };
        }
        catch (QuitException q)
        {
            return q.Cancel ? 1 : 0;
        }
        catch (VfpException ex)
        {
            PrintError(ex);
            return 2;
        }
        finally
        {
            foreach (var rt in Runtimes) rt.Session.Dispose();
        }
    }

    private static readonly List<Interpreter> Runtimes = new();

    private static Interpreter NewRuntime()
    {
        var rt = new Interpreter(new TextWriterOutput(Console.Out), Directory.GetCurrentDirectory());
        rt.ReadLine = _ => Console.ReadLine();
        JoePro.Reports.ReportEngine.Attach(rt);
        Runtimes.Add(rt);
        rt.Status += msg =>
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Error.WriteLine(msg);
            Console.ForegroundColor = old;
        };
        return rt;
    }

    private static void PrintError(VfpException ex)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        var where = ex.ErrorLine is { } l && l > 0 ? $" (line {l}{(ex.ErrorProgram is { } p && p != "(command)" ? " in " + p : "")})" : "";
        Console.Error.WriteLine($"Error {ex.Number}: {ex.Message}{where}");
        Console.ForegroundColor = old;
    }

    private static int RunProgram(string file, string[] args)
    {
        var rt = NewRuntime();
        if (Path.GetExtension(file).Equals(".jpapp", StringComparison.OrdinalIgnoreCase))
        {
            // A built application: data files are found next to it.
            rt.ExecuteCommand($"SET DEFAULT TO \"{Path.GetDirectoryName(Path.GetFullPath(file))}\"");
            rt.RunApp(Path.GetFullPath(file));
            Console.WriteLine();
            return 0;
        }
        var result = rt.RunProgram(Path.GetFullPath(file), args.Select(Value.String).ToArray());
        Console.WriteLine();
        return result.Kind == ValueKind.Number ? (int)result.AsNumber : 0;
    }

    private static int Exec(string code)
    {
        var rt = NewRuntime();
        rt.ExecuteCommand(code.Replace("\\n", "\n"));
        Console.WriteLine();
        return 0;
    }

    private static int Import(string[] args)
    {
        var source = args[0];
        string? target = null;
        for (int i = 1; i < args.Length; i++)
            if (args[i] is "--to" or "-o" && i + 1 < args.Length) target = args[++i];
        var rt = NewRuntime();
        var report = MigrationCommands.ImportFiles(rt, source, target ?? Path.GetFileNameWithoutExtension(source.TrimEnd('/', '\\')) + "-joepro");
        Console.WriteLine($"Readiness: {report.ReadinessScore:P0} of {report.Findings.Count} findings need no manual work.");
        foreach (var (category, counts) in report.Summary())
            Console.WriteLine($"  {category,-10} " + string.Join("  ", counts.Where(c => c.Value > 0).Select(c => $"{c.Key}: {c.Value}")));
        return report.Findings.Any(f => f.Status == FindingStatus.Failed) ? 3 : 0;
    }

    /// <summary>joepro add|remove|restore|publish|pack: the packages of the project in the current folder (or --project).</summary>
    private static int Packages(string cmd, string[] args)
    {
        string? projectPath = null, registry = null, source = null;
        bool update = false;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--project" or "-p" && i + 1 < args.Length) projectPath = args[++i];
            else if (args[i] is "--registry" or "-r" && i + 1 < args.Length) registry = args[++i];
            else if (args[i] is "--source" or "-s" && i + 1 < args.Length) source = args[++i];
            else if (args[i] is "--update" or "-u") update = true;
            else positional.Add(args[i]);
        }
        projectPath ??= Directory.EnumerateFiles(Directory.GetCurrentDirectory(), "*.jpproj").FirstOrDefault()
                        ?? throw new VfpException(ErrorCodes.FileDoesNotExist, "No .jpproj project in this folder; use --project.");
        projectPath = Path.GetFullPath(projectPath);
        var dir = Path.GetDirectoryName(projectPath)!;
        var project = JoePro.Documents.Projects.ProjectDocument.Load(projectPath);
        if (registry != null && cmd is "add" or "restore") project.Registry = registry;
        switch (cmd)
        {
            case "add":
            {
                if (positional.Count == 0) throw new VfpException(ErrorCodes.InvalidArgument, "joepro add <name>[@version]");
                var spec = positional[0];
                var at = spec.IndexOf('@');
                var version = PackageManager.Add(project, dir, at > 0 ? spec[..at] : spec, at > 0 ? spec[(at + 1)..] : null, source);
                project.Save(projectPath);
                Console.WriteLine($"Added {(at > 0 ? spec[..at] : spec)} {version}.");
                break;
            }
            case "remove":
                if (positional.Count == 0) throw new VfpException(ErrorCodes.InvalidArgument, "joepro remove <name>");
                PackageManager.Remove(project, dir, positional[0]);
                project.Save(projectPath);
                Console.WriteLine($"Removed {positional[0]}.");
                break;
            case "restore":
            {
                var result = PackageManager.Restore(project, dir, update);
                if (registry != null) project.Save(projectPath);
                foreach (var m in result.Messages) Console.WriteLine(m);
                Console.WriteLine($"{result.Packages.Count} package(s): " + string.Join(", ", result.Packages.Select(p => $"{p.Name} {p.Version}")));
                break;
            }
            case "publish":
            {
                var target = registry ?? project.Registry ?? PackageManager.DefaultRegistry ?? throw new VfpException(ErrorCodes.InvalidArgument, "joepro publish --registry <folder>");
                var path = PackageManager.Publish(project, dir, Path.IsPathRooted(target) || target.Contains("://") ? target : Path.GetFullPath(Path.Combine(dir, target)));
                Console.WriteLine($"Published {project.Name} {project.Version} to {path}.");
                break;
            }
            default:
            {
                var bytes = PackageManager.Pack(project, dir, out var manifest);
                var file = Path.Combine(dir, $"{manifest.Name.ToLowerInvariant()}-{manifest.Version}.jppkg");
                File.WriteAllBytes(file, bytes);
                Console.WriteLine($"Packed {manifest.Files.Length} file(s) into {file}.");
                break;
            }
        }
        return 0;
    }

    /// <summary>joepro sync config|init|run|status|conflicts|resolve|dismiss|cutover: two-way sync with a legacy VFP application.</summary>
    private static int Sync(string[] args)
    {
        string configPath = "joesync.json";
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "-c" && i + 1 < args.Length) configPath = args[++i];
            else rest.Add(args[i]);
        }
        string? Opt(string name) { var i = rest.IndexOf(name); return i >= 0 && i + 1 < rest.Count ? rest[i + 1] : null; }
        var sub = rest.Count > 0 ? rest[0].ToLowerInvariant() : "status";
        if (sub == "config")
        {
            // joepro sync config --legacy <folder> --database <db> --table customer:cust_id [--table …] [--dbc app.dbc]
            var config = new JoePro.Runtime.Sync.SyncConfig
            {
                Legacy = Opt("--legacy") ?? throw new VfpException(ErrorCodes.InvalidArgument, "--legacy <folder> is required."),
                Database = Opt("--database") ?? throw new VfpException(ErrorCodes.InvalidArgument, "--database <db> is required."),
                LegacyDatabase = Opt("--dbc"),
                Capture = Opt("--dbc") != null ? JoePro.Runtime.Sync.CaptureMode.Triggers : JoePro.Runtime.Sync.CaptureMode.Snapshot,
            };
            for (int i = 0; i < rest.Count - 1; i++)
                if (rest[i] == "--table" && rest[i + 1].Split(':') is [var name, var key])
                    config.Tables.Add(new JoePro.Runtime.Sync.SyncTable { Name = name, Key = key });
            if (config.Tables.Count == 0) throw new VfpException(ErrorCodes.InvalidArgument, "Give at least one --table name:keyfield.");
            config.Save(configPath);
            Console.WriteLine($"Wrote {configPath} ({config.Capture.ToString().ToLowerInvariant()} capture, {config.Tables.Count} table(s)). Next: joepro sync init");
            return 0;
        }
        using var engine = new JoePro.Runtime.Sync.SyncEngine(configPath);
        void Print(JoePro.Runtime.Sync.SyncCycle c) =>
            Console.WriteLine($"{c.TimeUtc.ToLocalTime():HH:mm:ss}  legacy changes {c.LegacyChanges}, Joe Pro changes {c.JoeChanges}; applied to Joe Pro {c.AppliedToJoe}, to legacy {c.AppliedToLegacy}; conflicts {c.Conflicts}, errors {c.Errors} ({c.Milliseconds} ms)");
        switch (sub)
        {
            case "init":
                Print(engine.Initialize());
                Console.WriteLine($"Wrote joesync_agent.prg{(engine.Config.Capture == JoePro.Runtime.Sync.CaptureMode.Triggers ? ", joesync_install.prg and joesync_uninstall.prg" : "")} into {engine.Config.Legacy}. Run the agent in VFP 9.");
                return 0;
            case "run":
            {
                var watch = Opt("--watch") is { } w ? int.Parse(w) : 0;
                do
                {
                    Print(engine.RunCycle());
                    if (watch > 0) Thread.Sleep(TimeSpan.FromSeconds(watch));
                } while (watch > 0);
                return 0;
            }
            case "status":
            {
                var st = engine.Status();
                Console.WriteLine($"Last cycle: {(st.LastCycleUtc is { } t ? t.ToLocalTime().ToString("g") + $" ({st.Lag!.Value.TotalSeconds:0} s ago)" : "never")}");
                Console.WriteLine($"Waiting for the VFP agent: {st.PendingBatches} batch(es), {st.PendingRows} row(s). Blocked rows: {st.Blocked}. Open conflicts: {st.OpenConflicts}.{(st.CutOver ? " Cut over: Joe Pro is the system of record." : "")}");
                foreach (var c in st.Recent.Take(5)) Print(c);
                return 0;
            }
            case "conflicts":
                foreach (var c in engine.State.Conflicts())
                    Console.WriteLine($"#{c.Id} {c.TimeUtc.ToLocalTime():g} {c.Table} key {c.Key} {c.Field} ({c.Kind}): legacy [{c.LegacyValue}] Joe Pro [{c.JoeValue}] → {c.Winner.ToString().ToLowerInvariant()} won");
                foreach (var (t, k, reason) in engine.State.Blocked()) Console.WriteLine($"blocked: {t} key {k}: {reason}");
                return 0;
            case "resolve":
                engine.Resolve(long.Parse(rest[1].TrimStart('#')), rest.Count > 2 && rest[2].Equals("legacy", StringComparison.OrdinalIgnoreCase) ? JoePro.Runtime.Sync.SyncSide.Legacy : JoePro.Runtime.Sync.SyncSide.Joe);
                Console.WriteLine("Re-applied to both sides.");
                return 0;
            case "dismiss":
                engine.Dismiss(long.Parse(rest[1].TrimStart('#')));
                return 0;
            case "cutover":
                foreach (var step in engine.Cutover()) Console.WriteLine(step);
                return 0;
            default:
                throw new VfpException(ErrorCodes.InvalidArgument, "joepro sync config|init|run [--watch s]|status|conflicts|resolve <id> legacy|joe|dismiss <id>|cutover");
        }
    }

    private static int WriteText(string text)
    {
        Console.Write(text);
        return 0;
    }

    private static int Functions(string[] args)
    {
        if (args.Contains("--commands"))
        {
            foreach (var r in JoePro.Runtime.Builtins.CommandCoverage.Run()) Console.WriteLine($"{r.Status,-13} {r.Command,-24} {r.Detail}");
            return 0;
        }
        if (args.Contains("--coverage"))
        {
            Console.Write(JoePro.Runtime.Builtins.VfpCatalog.CoverageMarkdown());
            return 0;
        }
        foreach (var n in JoePro.Runtime.Builtins.Library.Names.Where(n => !n.StartsWith("__")).OrderBy(n => n)) Console.WriteLine(n);
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine(Interpreter.VersionString);
        return 0;
    }

    private static int Help(int code = 0)
    {
        Console.WriteLine("""
            Joe Pro — a modern FoxPro-compatible environment

            Usage:
              joepro                      Start the interactive Command Window
              joepro run <file.prg> [args] Run a program
              joepro exec "<code>"        Run one or more commands (use \n between lines)
              joepro import <path> [--to <folder>]
                                          Import a FoxPro .DBF, .DBC or a whole folder and
                                          write migration-report.html/.json
              joepro add <name>[@range] [--source <folder|git+url#tag>]
                                          Add a package to the project in this folder and install it
              joepro remove <name>        Remove a package
              joepro restore [--update]   Install the project's packages (pinned in packages.lock.json)
              joepro publish [--registry <folder>]
                                          Publish the project as a package to a registry
              joepro pack                 Write the project's package (.jppkg)
              joepro sync config|init|run|status|conflicts|resolve|cutover
                                          Two-way sync with a legacy VFP application during a transition
              joepro functions            List the built-in functions implemented so far
              joepro functions --coverage The VFP 9 function and command coverage matrix (Markdown)
              joepro functions --commands Run every VFP 9 command sample and show its status
              joepro docs --reference     The language reference (Markdown), from the function registry
              joepro lsp                  Run the Language Server Protocol server on stdio (editor integration)
              joepro dap                  Run the Debug Adapter Protocol server on stdio (debugging in VS Code)
              joepro version              Show the version
            """);
        return code;
    }

    private static int Repl()
    {
        var rt = NewRuntime();
        Console.WriteLine($"{Interpreter.VersionString}. Type FoxPro commands; QUIT to exit.");
        var buffer = new List<string>();
        while (true)
        {
            Console.Write(buffer.Count == 0 ? "\n> " : "  ");
            var line = Console.ReadLine();
            if (line == null) break;
            buffer.Add(line);
            var text = string.Join("\n", buffer);
            if (NeedsMoreInput(text)) continue;
            buffer.Clear();
            if (text.Trim().Length == 0) continue;
            try
            {
                rt.ExecuteCommand(text);
            }
            catch (QuitException)
            {
                break;
            }
            catch (VfpException ex)
            {
                if (rt.Output.Column > 0) Console.WriteLine();
                PrintError(ex);
            }
        }
        return 0;
    }

    /// <summary>True while a block (IF, FOR, DEFINE CLASS…) or a ';' continuation is still open.</summary>
    private static bool NeedsMoreInput(string text)
    {
        if (text.TrimEnd().EndsWith(';')) return true;
        try
        {
            Parser.ParseInteractive(text);
            return false;
        }
        catch (CompileException ex)
        {
            return ex.Message.StartsWith("Missing END", StringComparison.OrdinalIgnoreCase)
                   || ex.Message.StartsWith("Missing ENDDEFINE", StringComparison.OrdinalIgnoreCase)
                   || ex.Message.Contains("for the block started", StringComparison.OrdinalIgnoreCase);
        }
    }
}
