using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JoePro.Migration;

public enum FindingStatus { Converted, ConvertedWithChanges, NeedsReview, Unsupported, Failed }

public sealed record SourceLocation(string File, string? Object = null, string? Member = null, int? Line = null, string? Snippet = null);

public sealed record TargetLocation(string File, string? Object = null);

/// <summary>One migration outcome. Every legacy artifact ends up with at least one finding; nothing is dropped silently.</summary>
public sealed record Finding
{
    public string Id { get; init; } = "";
    public FindingStatus Status { get; init; }
    public string Severity { get; init; } = "info";
    public string Rule { get; init; } = "";
    public string Category { get; init; } = "";
    public SourceLocation Source { get; init; } = new("");
    public TargetLocation? Target { get; init; }
    public string Message { get; init; } = "";
    public string? Action { get; init; }
    public string? AutoFix { get; init; }
}

public sealed record TableSummary(string Source, string Target, int RowsRead, int RowsImported, bool RowsVerified, string CodePage,
    int TagsFound, int TagsRebuilt, int DeletedRows);

/// <summary>The migration report (JSON for tools and diffs between runs, HTML for people).</summary>
public sealed class MigrationReport
{
    private int _seq;

    public string SchemaVersion { get; init; } = "1.0";
    public string Tool { get; init; } = "Joe Pro migration";
    public DateTime GeneratedUtc { get; init; } = DateTime.UtcNow;
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public List<Finding> Findings { get; } = new();
    public List<TableSummary> Tables { get; } = new();

    public Finding Add(FindingStatus status, string rule, string category, SourceLocation source, string message,
        string? action = null, TargetLocation? target = null, string? severity = null)
    {
        var f = new Finding
        {
            Id = $"F-{++_seq:000000}",
            Status = status,
            Severity = severity ?? status switch
            {
                FindingStatus.Converted => "info",
                FindingStatus.ConvertedWithChanges => "info",
                FindingStatus.NeedsReview => "warning",
                _ => "error",
            },
            Rule = rule,
            Category = category,
            Source = source,
            Target = target,
            Message = message,
            Action = action,
        };
        Findings.Add(f);
        return f;
    }

    public Dictionary<string, Dictionary<string, int>> Summary() =>
        Findings.GroupBy(f => f.Category).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => Enum.GetValues<FindingStatus>().ToDictionary(s => s.ToString(), s => g.Count(f => f.Status == s)));

    /// <summary>Share of findings that need no human action (Converted or ConvertedWithChanges).</summary>
    public double ReadinessScore =>
        Findings.Count == 0 ? 1 : Findings.Count(f => f.Status is FindingStatus.Converted or FindingStatus.ConvertedWithChanges) / (double)Findings.Count;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = SchemaVersion,
        tool = Tool,
        generatedUtc = GeneratedUtc,
        source = Source,
        target = Target,
        readinessScore = Math.Round(ReadinessScore, 4),
        summary = Summary(),
        tables = Tables,
        findings = Findings,
    }, JsonOptions);

    public static MigrationReport FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var report = new MigrationReport
        {
            Source = root.GetProperty("source").GetString() ?? "",
            Target = root.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "",
            GeneratedUtc = root.TryGetProperty("generatedUtc", out var g) && g.TryGetDateTime(out var when) ? when : default,
        };
        if (root.TryGetProperty("tables", out var tables))
            foreach (var table in tables.EnumerateArray())
                report.Tables.Add(table.Deserialize<TableSummary>(JsonOptions)!);
        foreach (var f in doc.RootElement.GetProperty("findings").EnumerateArray())
            report.Findings.Add(f.Deserialize<Finding>(JsonOptions)!);
        return report;
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "migration-report.json"), ToJson());
        File.WriteAllText(Path.Combine(directory, "migration-report.html"), ToHtml());
    }

    public string ToHtml()
    {
        static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Migration Report</title>
            <style>
            :root { --bg:#fff; --fg:#1b1f24; --muted:#5b6470; --line:#d9dee4; --ok:#1a7f37; --chg:#0969da; --rev:#9a6700; --bad:#cf222e; --card:#f6f8fa; }
            @media (prefers-color-scheme: dark) { :root { --bg:#0d1117; --fg:#e6edf3; --muted:#8b949e; --line:#30363d; --ok:#3fb950; --chg:#58a6ff; --rev:#d29922; --bad:#f85149; --card:#161b22; } }
            body { background:var(--bg); color:var(--fg); font:14px/1.5 system-ui, sans-serif; margin:0; padding:24px 16px; }
            main { max-width:1100px; margin:0 auto; }
            h1 { margin:0 0 4px; font-size:22px; } .muted { color:var(--muted); }
            table { border-collapse:collapse; width:100%; margin:12px 0 24px; } th, td { border-bottom:1px solid var(--line); padding:6px 8px; text-align:left; vertical-align:top; }
            th { font-weight:600; } td.n { text-align:right; font-variant-numeric:tabular-nums; }
            .tag { display:inline-block; padding:1px 8px; border-radius:10px; font-size:12px; border:1px solid currentColor; }
            .Converted { color:var(--ok); } .ConvertedWithChanges { color:var(--chg); } .NeedsReview { color:var(--rev); } .Unsupported, .Failed { color:var(--bad); }
            .cards { display:flex; gap:12px; flex-wrap:wrap; margin:16px 0; } .card { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:10px 14px; min-width:120px; }
            .card b { display:block; font-size:22px; } code { font-family:ui-monospace, monospace; font-size:12px; }
            .filters { display:flex; gap:8px; flex-wrap:wrap; margin:8px 0; } .filters label { cursor:pointer; }
            .wrap { overflow-x:auto; }
            </style></head><body><main>
            """);
        sb.Append($"<h1>Migration Report</h1><div class=\"muted\">Source: {H(Source)} → Target: {H(Target)} · generated {GeneratedUtc:u}</div>");
        sb.Append("<div class=\"cards\">");
        sb.Append($"<div class=\"card\"><b>{ReadinessScore:P0}</b>ready without manual work</div>");
        foreach (var s in Enum.GetValues<FindingStatus>())
            sb.Append($"<div class=\"card\"><b class=\"{s}\">{Findings.Count(f => f.Status == s)}</b>{s}</div>");
        sb.Append("</div>");
        if (Tables.Count > 0)
        {
            sb.Append("<h2>Data</h2><div class=\"wrap\"><table><tr><th>Source</th><th>Target</th><th>Rows</th><th>Verified</th><th>Deleted</th><th>Code page</th><th>Index tags</th></tr>");
            foreach (var t in Tables)
                sb.Append($"<tr><td><code>{H(t.Source)}</code></td><td><code>{H(t.Target)}</code></td><td class=\"n\">{t.RowsImported}/{t.RowsRead}</td><td>{(t.RowsVerified ? "✔" : "✘")}</td><td class=\"n\">{t.DeletedRows}</td><td>{H(t.CodePage)}</td><td class=\"n\">{t.TagsRebuilt}/{t.TagsFound}</td></tr>");
            sb.Append("</table></div>");
        }
        sb.Append("<h2>Summary by category</h2><div class=\"wrap\"><table><tr><th>Category</th>");
        foreach (var s in Enum.GetValues<FindingStatus>()) sb.Append($"<th class=\"{s}\">{s}</th>");
        sb.Append("</tr>");
        foreach (var (cat, counts) in Summary())
        {
            sb.Append($"<tr><td>{H(cat)}</td>");
            foreach (var s in Enum.GetValues<FindingStatus>()) sb.Append($"<td class=\"n\">{counts[s.ToString()]}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</table></div><h2>Findings</h2><div class=\"filters\">");
        foreach (var s in Enum.GetValues<FindingStatus>())
            sb.Append($"<label><input type=\"checkbox\" checked data-status=\"{s}\"> {s}</label>");
        sb.Append("</div><div class=\"wrap\"><table id=\"findings\"><tr><th>Status</th><th>Rule</th><th>Source</th><th>Message</th><th>Action</th></tr>");
        foreach (var f in Findings.OrderBy(f => f.Status == FindingStatus.Converted).ThenByDescending(f => f.Status))
        {
            var loc = f.Source.File + (f.Source.Object != null ? " · " + f.Source.Object : "") + (f.Source.Member != null ? "." + f.Source.Member : "") + (f.Source.Line != null ? ":" + f.Source.Line : "");
            sb.Append($"<tr data-status=\"{f.Status}\"><td><span class=\"tag {f.Status}\">{f.Status}</span></td><td><code>{H(f.Rule)}</code></td><td><code>{H(loc)}</code>");
            if (f.Source.Snippet != null) sb.Append($"<br><code class=\"muted\">{H(f.Source.Snippet)}</code>");
            sb.Append($"</td><td>{H(f.Message)}</td><td>{H(f.Action)}</td></tr>");
        }
        sb.Append("""
            </table></div></main>
            <script>
            document.querySelectorAll('.filters input').forEach(cb => cb.addEventListener('change', () => {
              const on = new Set([...document.querySelectorAll('.filters input:checked')].map(x => x.dataset.status));
              document.querySelectorAll('#findings tr[data-status]').forEach(tr => tr.style.display = on.has(tr.dataset.status) ? '' : 'none');
            }));
            </script></body></html>
            """);
        return sb.ToString();
    }
}
