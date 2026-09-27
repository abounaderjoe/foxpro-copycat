using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using JoePro.Core;
using JoePro.Data;
using JoePro.Documents.Reports;
using JoePro.Runtime;

namespace JoePro.Reports;

/// <summary>
/// REPORT FORM / LABEL FORM: loads the layout (.jpreport/.jplabel, or .frx/.lbx converted in memory), opens its
/// data environment (in a private data session when the report asks for one), lays it out over the records in scope
/// and sends the pages to the preview, a printer, a file (PDF, HTML, XML, PNG, text) or the screen as text.
/// </summary>
public sealed class ReportEngine : IReportRunner
{
    private readonly Interpreter _rt;

    private ReportEngine(Interpreter rt) => _rt = rt;

    /// <summary>Connects the report engine to a runtime (REPORT FORM and LABEL FORM start working).</summary>
    public static ReportEngine Attach(Interpreter rt)
    {
        if (rt.Reports is ReportEngine existing) return existing;
        var engine = new ReportEngine(rt);
        rt.Reports = engine;
        return engine;
    }

    /// <summary>Shows a preview (set by the IDE). Returns false to fall back to a PDF file.</summary>
    public Func<RenderedReport, ReportRequest, bool>? PreviewHandler { get; set; }

    /// <summary>Prints a PDF file (TO PRINTER). Hosts may replace the default, which uses the operating system's print command.</summary>
    public Func<string, bool, bool> PrintHandler { get; set; } = DefaultPrint;

    /// <summary>The last report laid out (for hosts and tests).</summary>
    public RenderedReport? LastReport { get; private set; }

    /// <summary>Loads a report or label layout; legacy .frx/.lbx files are converted in memory.</summary>
    public static ReportDocument LoadDocument(string path, out ReportConversion? conversion)
    {
        conversion = null;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".frx" or ".lbx")
        {
            conversion = LegacyReportConverter.Convert(path);
            return conversion.Document;
        }
        return ReportDocument.Load(path);
    }

    /// <summary>Lays out a report document over the current work area (or a data environment) without sending it anywhere.</summary>
    public RenderedReport Layout(ReportDocument doc, string? reportPath, ReportRequest? request = null)
    {
        request ??= new ReportRequest { Path = reportPath ?? "" };
        var session = doc.PrivateDataSession ? _rt.BeginPrivateSession() : null;
        VfpObject? de = null;
        return RunIn(session, () =>
        {
            try
            {
                if (doc.DataEnvironment != null) de = _rt.OpenReportDataEnvironment(doc.DataEnvironment, reportPath ?? Path.Combine(_rt.Options.Default_, "report.jpreport"));
                if (request.NameVar != null && de != null) _rt.SetVariable(request.NameVar, Value.Object(de));
                var area = _rt.Session.Current;
                var records = area.InUse ? _rt.RecordsInScope(area, request.Scope) : null;
                var listener = request.Listener != null ? new VfpReportListener(_rt, request.Listener, request) : null;
                var options = new LayoutOptions
                {
                    BaseDirectory = reportPath != null ? Path.GetDirectoryName(Path.GetFullPath(reportPath)) : null,
                    Heading = request.Heading, Plain = request.Plain, SummaryOnly = request.Summary, Listener = listener,
                };
                var report = new ReportLayout(doc, _rt, options).Run(area.InUse ? area : null, records);
                if (UsesPageTotal(doc) || listener != null)
                {
                    // Second pass: _PAGETOTAL (and ReportListener.PageTotal) need the page count.
                    listener?.SetPageTotal(report.Pages.Count);
                    report = new ReportLayout(doc, _rt, new LayoutOptions
                    {
                        BaseDirectory = options.BaseDirectory, Heading = options.Heading, Plain = options.Plain, SummaryOnly = options.SummaryOnly,
                        Listener = listener, PageTotal = report.Pages.Count,
                    }).Run(area.InUse ? area : null, records);
                }
                // Like LIST, the report leaves the record pointer at the end of the file.
                if (area.InUse && records is { Count: > 0 }) { area.GoBottom(); if (!area.Eof) area.Skip(); }
                if (request.RangeFrom is { } from)
                {
                    var to = request.RangeTo ?? int.MaxValue;
                    var keep = report.Pages.Where((p, i) => i + 1 >= from && i + 1 <= to).ToList();
                    report.Pages.Clear();
                    report.Pages.AddRange(keep);
                }
                return new RenderedReport { Document = doc, Title = Path.GetFileNameWithoutExtension(reportPath ?? "report") }.With(report);
            }
            finally
            {
                if (de != null) _rt.CloseReportDataEnvironment(de);
            }
        });
    }

    private T RunIn<T>(DataSession? session, Func<T> body)
    {
        if (session == null) return body();
        try { return _rt.InSession(session, body); }
        finally { _rt.EndPrivateSession(session); }
    }

    private static bool UsesPageTotal(ReportDocument doc) =>
        doc.Bands.SelectMany(b => b.Objects).Any(o => o is ReportField f && f.Expression.Contains("_PAGETOTAL", StringComparison.OrdinalIgnoreCase)
                                                      || o.PrintWhen?.Contains("_PAGETOTAL", StringComparison.OrdinalIgnoreCase) == true)
        || doc.Variables.Any(v => v.Value.Contains("_PAGETOTAL", StringComparison.OrdinalIgnoreCase));

    public void Run(ReportRequest request)
    {
        var doc = LoadDocument(request.Path, out var conversion);
        if (conversion != null)
        {
            var review = conversion.Findings.Count(f => f.Status is Documents.FindingStatus.NeedsReview or Documents.FindingStatus.Unsupported);
            _rt.Notify($"{Path.GetFileName(request.Path)} was converted in memory{(review > 0 ? $" ({review} item(s) need review)" : "")}. IMPORT FOXPRO converts it permanently.");
        }
        if (request.Sample)
        {
            _rt.Notify("LABEL FORM … SAMPLE: printing sample labels is not needed with a preview; the labels are printed normally.");
        }
        var report = Layout(doc, request.Path, request);
        LastReport = report;
        foreach (var (font, used) in report.FontSubstitutions)
            _rt.Notify($"Report font {font} is not installed; {used} was used instead.");

        var type = request.ObjectType;
        if (request.Preview || type == 1)
        {
            if (PreviewHandler?.Invoke(report, request) == true) return;
            var pdf = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(request.Path) + "-preview.pdf");
            PdfRenderer.Write(report, pdf);
            _rt.Notify($"Preview written to {pdf} ({report.Pages.Count} page(s)).");
            return;
        }
        if (request.ToPrinter || type == 0)
        {
            var pdf = Path.Combine(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(request.Path)}-{Guid.NewGuid():N}.pdf");
            PdfRenderer.Write(report, pdf);
            if (!PrintHandler(pdf, request.Prompt)) throw new VfpException(125, $"Printer is not ready. The report was saved as {pdf}.");
            return;
        }
        if (request.ToFile is { } file)
        {
            WriteFile(report, file, request.Ascii, type);
            return;
        }
        if (type is 4 or 5)
        {
            var output = type == 4 ? XmlRenderer.Render(report) : HtmlRenderer.Render(report);
            _rt.SetVariable("_oReportOutput", Value.String(output));
            return;
        }
        if (!request.NoConsole)
        {
            // No destination: the report is shown as text on the screen, as VFP does.
            if (_rt.Output.Column > 0) _rt.Output.NewLine();
            foreach (var line in TextRenderer.Render(report, formFeeds: false).TrimEnd('\n').Split('\n'))
            {
                _rt.Output.Write(line);
                _rt.Output.NewLine();
            }
        }
    }

    /// <summary>TO FILE: the extension picks the format (.pdf, .htm/.html, .xml, .png, .txt); ASCII always writes text.</summary>
    public static void WriteFile(RenderedReport report, string file, bool ascii = false, int? objectType = null)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (!Path.HasExtension(file)) file += ascii ? ".txt" : objectType == 5 ? ".html" : objectType == 4 ? ".xml" : ".pdf";
        ext = Path.GetExtension(file).ToLowerInvariant();
        if (ascii || ext is ".txt" or ".prn") File.WriteAllText(file, TextRenderer.Render(report), new UTF8Encoding(false));
        else if (ext is ".htm" or ".html" || objectType == 5) File.WriteAllText(file, HtmlRenderer.Render(report), new UTF8Encoding(false));
        else if (ext == ".xml" || objectType == 4) File.WriteAllText(file, XmlRenderer.Render(report), new UTF8Encoding(false));
        else if (ext is ".png") ImageRenderer.Write(report, file);
        else PdfRenderer.Write(report, file);
    }

    private static bool DefaultPrint(string pdf, bool prompt)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true, Verb = "print", CreateNoWindow = true });
            else
            {
                using var p = Process.Start(new ProcessStartInfo("lp", $"\"{pdf}\"") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
                if (p == null) return false;
                p.WaitForExit(30000);
                return p.ExitCode == 0;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}

internal static class RenderedReportExtensions
{
    /// <summary>Copies the pages and font notes of a layout into a titled report.</summary>
    public static RenderedReport With(this RenderedReport target, RenderedReport source)
    {
        target.Pages.AddRange(source.Pages);
        foreach (var (k, v) in source.FontSubstitutions) target.FontSubstitutions[k] = v;
        return target;
    }
}

/// <summary>
/// Object-assisted reporting: a VFP 9 ReportListener object receives BeforeReport, AfterReport, BeforeBand,
/// AfterBand, EvaluateContents and Render, and its PageNo/PageTotal/OutputType properties are kept up to date.
/// </summary>
internal sealed class VfpReportListener : IReportListener
{
    private readonly Interpreter _rt;
    private readonly VfpObject _o;
    private readonly bool _evaluates, _renders, _bands;

    public VfpReportListener(Interpreter rt, VfpObject listener, ReportRequest request)
    {
        _rt = rt;
        _o = listener;
        _evaluates = rt.Handles(listener, "EvaluateContents");
        _renders = rt.Handles(listener, "Render");
        _bands = rt.Handles(listener, "BeforeBand") || rt.Handles(listener, "AfterBand");
        Set("OutputType", Value.Number(request.ObjectType ?? (request.Preview ? 1 : request.ToPrinter ? 0 : -1)));
        Set("PageNo", Value.Number(0));
        Set("PageTotal", Value.Number(0));
        Set("CurrentPass", Value.Number(0));
    }

    private void Set(string name, Value v) => _o.Set(name, v);

    public void SetPageTotal(int total)
    {
        Set("PageTotal", Value.Number(total));
        Set("CurrentPass", Value.Number(1));
    }

    public void BeforeReport() => _rt.Raise(_o, "BeforeReport");
    public void AfterReport() => _rt.Raise(_o, "AfterReport");
    public void PageStarted(int pageNo) => Set("PageNo", Value.Number(pageNo));

    private static int BandCode(ReportBand b) => b.Kind switch
    {
        BandKind.Title => 0, BandKind.PageHeader => 1, BandKind.ColumnHeader => 2, BandKind.GroupHeader => 3, BandKind.Detail => 4,
        BandKind.GroupFooter => 5, BandKind.ColumnFooter => 6, BandKind.PageFooter => 7, BandKind.Summary => 8,
        BandKind.DetailHeader => 9, _ => 10,
    };

    public void BeforeBand(ReportBand band) { if (_bands) _rt.Raise(_o, "BeforeBand", Value.Number(BandCode(band)), Value.Number(band.Index)); }
    public void AfterBand(ReportBand band) { if (_bands) _rt.Raise(_o, "AfterBand", Value.Number(BandCode(band)), Value.Number(band.Index)); }

    public string EvaluateContents(int objectId, ReportField field, string text)
    {
        if (!_evaluates) return text;
        var props = (VfpObject)_rt.Evaluate("CREATEOBJECT('Empty')").AsObject;
        props.Set("Text", Value.String(text));
        props.Set("Value", Value.String(text));
        props.Set("Reload", Value.False);
        props.Set("FontName", Value.String(field.FontName ?? ""));
        props.Set("FontSize", Value.Number(field.FontSize ?? 0));
        _rt.Raise(_o, "EvaluateContents", Value.Number(objectId + 1), Value.Object(props));
        return props.FindProperty("Reload")?.Value is { Kind: ValueKind.Logical } r && r.AsBool && props.FindProperty("Text")?.Value is { Kind: ValueKind.Character } t
            ? t.AsString : text;
    }

    public bool Render(int objectId, PageItem item)
    {
        if (!_renders) return true;
        // Positions in 1/960 inch, as VFP passes them.
        double U(double pt) => Math.Round(pt / 72 * 960);
        _rt.Raise(_o, "Render", Value.Number(objectId + 1), Value.Number(U(item.X)), Value.Number(U(item.Y)), Value.Number(U(item.W)), Value.Number(U(item.H)),
            Value.Number(0), Value.String(item is TextItem t ? t.Text : ""), Value.Number(0));
        return !_rt.LastNoDefault;
    }
}
