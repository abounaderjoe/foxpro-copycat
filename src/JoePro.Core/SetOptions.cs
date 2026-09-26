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

    public SetOptions Clone()
    {
        var c = (SetOptions)MemberwiseClone();
        c.Path = new List<string>(Path);
        return c;
    }
}
