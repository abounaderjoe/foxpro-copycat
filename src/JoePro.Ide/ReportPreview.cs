using JoePro.Reports;
using JoePro.Ui.Runtime;

namespace JoePro.Ide;

/// <summary>A laid-out report in the preview: page navigation, zoom, find, export and print.</summary>
public sealed class ReportPreviewTab : DocumentTab
{
    public ReportPreviewTab(RenderedReport report, ReportEngine engine)
    {
        Preview = new ReportPreview(report, engine);
        Content = Preview;
        Title = "Preview: " + report.Title;
    }

    public ReportPreview Preview { get; }
}

