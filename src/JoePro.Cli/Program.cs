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
                "version" or "--version" => Version(),
                "functions" => Functions(),
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

    private static int Functions()
    {
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
              joepro functions            List the built-in functions implemented so far
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
