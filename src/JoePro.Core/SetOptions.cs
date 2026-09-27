namespace JoePro.Core;

public enum DateFormat { American, Ansi, British, French, German, Italian, Japan, Usa, Mdy, Dmy, Ymd, Taiwan, Long, Short }

/// <summary>
/// Settings changed by SET commands. In VFP most of these are scoped to the data session;
/// the runtime keeps one instance per data session.
/// </summary>
public sealed class SetOptions
{
    public static readonly SetOptions Default = new();

    public bool Exact { get; set; }
    public bool Ansi { get; set; }
    public bool Deleted { get; set; }
    public bool Near { get; set; }
    public bool Talk { get; set; } = true;
    public bool Century { get; set; }
    public bool Null { get; set; }
    public bool Seconds { get; set; } = true;
    public bool Exclusive { get; set; }
    public bool Safety { get; set; } = true;
    public bool Optimize { get; set; } = true;
    public int Hours { get; set; } = 12;
    public int Decimals { get; set; } = 2;
    public DateFormat Date { get; set; } = DateFormat.American;
    public char? Mark { get; set; }
    public char Point { get; set; } = '.';
    public char Separator { get; set; } = ',';
    public int EngineBehavior { get; set; } = 90;
    public string Collate { get; set; } = "MACHINE";
    public string Default_ { get; set; } = Directory.GetCurrentDirectory();
    public List<string> Path { get; private set; } = new();
    /// <summary>SET NULLDISPLAY TO: the text ? and grids show for .NULL. (null: ".NULL.").</summary>
    public string? NullDisplay { get; set; }
    /// <summary>SET MEMOWIDTH TO: the line width MLINE() and MEMLINES() wrap at.</summary>
    public int MemoWidth { get; set; } = 50;
    /// <summary>SET FDOW TO / SET FWEEK TO: the first day of the week (1 = Sunday) and the first-week rule.</summary>
    public int Fdow { get; set; } = 1;
    public int Fweek { get; set; } = 1;
    /// <summary>SET ASSERTS ON: ASSERT commands are checked.</summary>
    public bool Asserts { get; set; }
    /// <summary>SET CONSOLE OFF: ?, ?? and LIST output does not go to the screen.</summary>
    public bool Console { get; set; } = true;
    /// <summary>Other SET values as set (upper-case name → text), so SET() reports them.</summary>
    public Dictionary<string, string> Values { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An ON/OFF setting kept in <see cref="Values"/>.</summary>
    public bool IsOn(string name, bool defaultValue) => Values.TryGetValue(name, out var v) ? v.Equals("ON", StringComparison.OrdinalIgnoreCase) : defaultValue;

    public SetOptions Clone()
    {
        var c = (SetOptions)MemberwiseClone();
        c.Path = new List<string>(Path);
        c.Values = new Dictionary<string, string>(Values, StringComparer.OrdinalIgnoreCase);
        return c;
    }
}
