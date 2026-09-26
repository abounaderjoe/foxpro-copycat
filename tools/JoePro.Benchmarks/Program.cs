using System.Diagnostics;
using System.Runtime.InteropServices;
using JoePro.Runtime;

// Joe Pro benchmarks: typical FoxPro workloads timed end to end through the interpreter.
// Usage: dotnet run -c Release --project tools/JoePro.Benchmarks [-- --rows N] [--markdown file]

int rows = 100_000;
string? markdown = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--rows" && i + 1 < args.Length) rows = int.Parse(args[++i]);
    else if (args[i] == "--markdown" && i + 1 < args.Length) markdown = args[++i];
}

var dir = Path.Combine(Path.GetTempPath(), "joepro-bench-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var rt = new Interpreter(new TextWriterOutput(TextWriter.Null), dir);
rt.ExecuteCommand("SET TALK OFF\nSET SAFETY OFF");

var results = new List<(string Name, string Workload, double Ms, double PerSecond)>();

void Bench(string name, string workload, int ops, string setup, string code)
{
    if (setup.Length > 0) rt.ExecuteCommand(setup);
    rt.ExecuteCommand(code); // warm-up (JIT, caches); workloads are written to be repeatable
    var times = new List<double>();
    for (int r = 0; r < 3; r++)
    {
        if (setup.Length > 0) rt.ExecuteCommand(setup);
        var sw = Stopwatch.StartNew();
        rt.ExecuteCommand(code);
        times.Add(sw.Elapsed.TotalMilliseconds);
    }
    var best = times.Min();
    results.Add((name, workload, best, ops / (best / 1000)));
    Console.WriteLine($"{name,-34} {best,10:F1} ms  {ops / (best / 1000),14:N0} ops/s");
}

int loop = rows * 10;
Bench("Interpreter loop", $"FOR 1..{loop:N0} with arithmetic and IF", loop, "",
    $"""
    LOCAL i, n
    n = 0
    FOR i = 1 TO {loop}
      n = n + i % 7
      IF n > 1000000
        n = 0
      ENDIF
    ENDFOR
    """);

Bench("String building", $"{rows:N0} concatenations + STRTRAN/UPPER", rows, "",
    $"""
    LOCAL i, s
    s = ""
    FOR i = 1 TO {rows}
      s = UPPER(STRTRAN("abc" + TRANSFORM(i), "b", "x"))
    ENDFOR
    """);

Bench("INSERT INTO (table)", $"{rows:N0} rows into an indexed table", rows,
    "CLOSE TABLES ALL\nIF FILE('orders.jpt')\nERASE orders.jpt\nENDIF\nCREATE TABLE orders (id I, cust I, amt N(10,2), note C(20))\nINDEX ON id TAG id\nINDEX ON cust TAG cust",
    $"""
    LOCAL i
    FOR i = 1 TO {rows}
      INSERT INTO orders VALUES (i, i % 1000, i * 1.5, "note")
    ENDFOR
    """);

Bench("APPEND BLANK + REPLACE", $"{rows:N0} rows", rows,
    "CLOSE TABLES ALL\nCREATE CURSOR c (id I, amt N(10,2), note C(20))",
    $"""
    LOCAL i
    FOR i = 1 TO {rows}
      APPEND BLANK
      REPLACE id WITH i, amt WITH i * 2, note WITH "x"
    ENDFOR
    """);

rt.ExecuteCommand("CLOSE TABLES ALL\nUSE orders");
Bench("SCAN FOR + SUM", $"scan {rows:N0} rows, sum where cust < 500", rows, "SELECT orders",
    """
    LOCAL t
    t = 0
    SCAN FOR cust < 500
      t = t + amt
    ENDSCAN
    """);

int seeks = Math.Min(rows, 50_000);
Bench("SEEK (indexed)", $"{seeks:N0} random SEEKs", seeks, "SELECT orders\nSET ORDER TO id",
    $"""
    LOCAL i, k
    FOR i = 1 TO {seeks}
      k = INT(RAND() * {rows}) + 1
      =SEEK(k)
    ENDFOR
    """);

Bench("LOCATE (unindexed)", "20 LOCATEs, each scanning the whole table", 20, "SELECT orders\nSET ORDER TO",
    $"""
    LOCAL i
    FOR i = 1 TO 20
      LOCATE FOR note = "none" AND id = {rows} - i
    ENDFOR
    """);

Bench("SELECT GROUP BY", $"group {rows:N0} rows into 1,000 groups", rows, "",
    "SELECT cust, SUM(amt) AS total, COUNT(*) AS n FROM orders GROUP BY cust INTO CURSOR g");

rt.ExecuteCommand($"""
    CREATE CURSOR cust (id I, name C(20))
    FOR i = 0 TO 999
      INSERT INTO cust VALUES (i, "Customer " + TRANSFORM(i))
    ENDFOR
    """);
Bench("SELECT JOIN (hash)", $"{rows:N0} orders joined to 1,000 customers", rows, "",
    "SELECT o.id, c.name, o.amt FROM orders o JOIN cust c ON o.cust = c.id WHERE o.amt > 10 INTO CURSOR j");

Bench("UPDATE-SQL", $"update {rows:N0} rows", rows, "",
    "UPDATE orders SET amt = amt + 1 WHERE cust >= 0");

Bench("Object method calls", $"{rows:N0} method calls", rows,
    """
    DEFINE CLASS Counter AS Custom
      nValue = 0
      PROCEDURE Add(n)
        THIS.nValue = THIS.nValue + n
      ENDPROC
    ENDDEFINE
    """,
    $"""
    LOCAL o, i
    o = CREATEOBJECT("Counter")
    FOR i = 1 TO {rows}
      o.Add(i)
    ENDFOR
    """);

rt.ExecuteCommand("CLOSE ALL");
foreach (var s in rt.Sessions.ToList()) s.Dispose();
try { Directory.Delete(dir, true); } catch (IOException) { }

if (markdown != null)
{
    using var w = new StreamWriter(markdown);
    w.WriteLine($"| Benchmark | Workload | Best of 3 (ms) | Operations/s |");
    w.WriteLine("|---|---|---:|---:|");
    foreach (var r in results) w.WriteLine($"| {r.Name} | {r.Workload} | {r.Ms:F1} | {r.PerSecond:N0} |");
    w.WriteLine();
    w.WriteLine($"Machine: {RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}, {RuntimeInformation.ProcessArchitecture}.");
}
