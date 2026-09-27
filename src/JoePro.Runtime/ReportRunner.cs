using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>Runs REPORT FORM / LABEL FORM. Implemented by the report engine (JoePro.Reports), which hosts attach.</summary>
public interface IReportRunner
{
    void Run(ReportRequest request);
}

/// <summary>A REPORT FORM or LABEL FORM command with its clauses evaluated.</summary>
public sealed class ReportRequest
{
    /// <summary>The report or label file (.jpreport/.jplabel, or a legacy .frx/.lbx).</summary>
    public required string Path { get; init; }
    public bool Label { get; init; }
    public Scope Scope { get; init; } = new();
    public bool Environment { get; init; }
    public string? Heading { get; init; }
    public bool NoConsole { get; init; }
    public bool Plain { get; init; }
    public int? RangeFrom { get; init; }
    public int? RangeTo { get; init; }
    public bool Preview { get; init; }
    public bool NoWait { get; init; }
    public bool ToPrinter { get; init; }
    public bool Prompt { get; init; }
    public string? ToFile { get; init; }
    public bool Ascii { get; init; }
    public bool Summary { get; init; }
    public bool Sample { get; init; }
    /// <summary>OBJECT clause: a ReportListener, and/or TYPE (0 printer, 1 preview, 4 XML, 5 HTML).</summary>
    public VfpObject? Listener { get; init; }
    public int? ObjectType { get; init; }
    public string? NameVar { get; init; }
}
