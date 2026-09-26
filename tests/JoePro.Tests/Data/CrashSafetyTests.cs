using System.Diagnostics;
using JoePro.Runtime;
using Microsoft.Data.Sqlite;

namespace JoePro.Tests.Data;

/// <summary>
/// Kills a real Joe Pro process in the middle of writing and checks that the table files are intact:
/// committed work survives, uncommitted transactions vanish, and SQLite's integrity check passes.
/// </summary>
public class CrashSafetyTests
{
    private readonly string _dir = TestPaths.TempDir();

    private void RunAndKill(string program, int timeoutSeconds = 60)
    {
        File.WriteAllText(Path.Combine(_dir, "work.prg"), program);
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(host)
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "joepro.dll"));
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("work.prg");
        using var p = Process.Start(psi)!;
        var flag = Path.Combine(_dir, "ready.flag");
        var sw = Stopwatch.StartNew();
        while (!File.Exists(flag))
        {
            if (p.HasExited) Assert.Fail("The child process exited early: " + p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd());
            if (sw.Elapsed.TotalSeconds > timeoutSeconds) { p.Kill(true); Assert.Fail("The child process never became ready."); }
            Thread.Sleep(20);
        }
        Thread.Sleep(100); // let it write some more
        p.Kill(entireProcessTree: true);
        p.WaitForExit();
    }

    private string IntegrityCheck(string file)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, file), Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check";
        return (string)cmd.ExecuteScalar()!;
    }

    private string Query(string code)
    {
        var output = new StringWriter();
        var rt = new Interpreter(new TextWriterOutput(output), _dir);
        try
        {
            rt.ExecuteCommand("SET TALK OFF\n" + code);
            return output.ToString().Trim();
        }
        finally { rt.Session.Dispose(); }
    }

    [Fact]
    public void Uncommitted_transaction_is_rolled_back_after_a_crash()
    {
        RunAndKill("""
            SET TALK OFF
            CREATE TABLE t (id I PRIMARY KEY, pad C(100))
            INSERT INTO t VALUES (0, "committed before the transaction")
            BEGIN TRANSACTION
            FOR i = 1 TO 10000000
              INSERT INTO t VALUES (i, "uncommitted")
              IF i = 2000
                =STRTOFILE("ready", "ready.flag")
              ENDIF
            ENDFOR
            END TRANSACTION
            """);
        Assert.Equal("ok", IntegrityCheck("t.jpt"));
        Assert.Equal("1 committed before the transaction", Query("USE t\n? TRANSFORM(RECCOUNT()) + \" \" + TRIM(pad)"));
    }

    [Fact]
    public void Committed_rows_survive_a_crash_and_indexes_stay_consistent()
    {
        RunAndKill("""
            SET TALK OFF
            CREATE TABLE u (id I PRIMARY KEY, pad C(100))
            FOR i = 1 TO 10000000
              INSERT INTO u VALUES (i, REPLICATE("x", 100))
              IF i = 500
                =STRTOFILE("ready", "ready.flag")
              ENDIF
            ENDFOR
            """);
        Assert.Equal("ok", IntegrityCheck("u.jpt"));
        // Every committed row is complete and the rows are contiguous: nothing torn, nothing lost in the middle.
        var result = Query("""
            USE u
            SELECT COUNT(*) AS n, MAX(id) AS hi, MIN(id) AS lo FROM u INTO CURSOR s
            SELECT u
            SET ORDER TO id
            ? s.n >= 500, s.n = s.hi, s.lo = 1, SEEK(s.hi), LEN(TRIM(pad)) = 100
            """);
        Assert.Equal(".T. .T. .T. .T. .T.", result);
    }
}
