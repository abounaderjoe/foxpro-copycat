using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using JoePro.Reports;

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

public sealed class ReportPreview : UserControl
{
    private readonly ReportEngine _engine;
    private readonly Image _image = new() { Stretch = Stretch.None };
    private readonly Canvas _highlights = new() { IsHitTestVisible = false };
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly ComboBox _zoom = new() { Width = 110, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _find = new() { Watermark = "Find", Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _scroller;
    private static readonly (string Label, double Zoom)[] Zooms = [("Fit width", 0), ("50%", 0.5), ("75%", 0.75), ("100%", 1), ("150%", 1.5), ("200%", 2)];

    public ReportPreview(RenderedReport report, ReportEngine engine, bool compact = false)
    {
        Report = report;
        _engine = engine;
        if (compact) Zoom = 0;
        foreach (var z in Zooms) _zoom.Items.Add(z.Label);
        _zoom.SelectedIndex = compact ? 0 : 3;
        _zoom.SelectionChanged += (_, _) => { Zoom = Zooms[Math.Max(0, _zoom.SelectedIndex)].Zoom; Show(); };
        _find.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) FindNext(_find.Text ?? ""); };
        Button B(string text, string tip, Action act)
        {
            var b = new Button { Content = text, Padding = new Thickness(8, 2), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => act();
            return b;
        }
        var toolbar = new WrapPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                B("⏮", "First page", () => GoTo(0)), B("◀", "Previous page", () => GoTo(PageIndex - 1)),
                _pageLabel,
                B("▶", "Next page", () => GoTo(PageIndex + 1)), B("⏭", "Last page", () => GoTo(Report.Pages.Count - 1)),
            },
        };
        if (!compact)
        {
            toolbar.Children.Add(_zoom);
            toolbar.Children.Add(_find);
            toolbar.Children.Add(B("Find", "Find text", () => FindNext(_find.Text ?? "")));
            toolbar.Children.Add(B("Export…", "Save as PDF, HTML, text or images", () => _ = Export()));
            toolbar.Children.Add(B("Print", "Print the report", Print));
        }
        var pagePanel = new Panel { Children = { _image, _highlights }, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16) };
        _scroller = new ScrollViewer
        {
            Content = new Border { Child = pagePanel, Background = Brushes.Transparent },
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)),
        };
        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_scroller);
        Content = root;
        _scroller.SizeChanged += (_, _) => { if (Zoom == 0) Show(); };
        Show();
    }

    public RenderedReport Report { get; }
    public int PageIndex { get; private set; }
    /// <summary>1 = 100% (96 dpi); 0 = fit the page to the window's width.</summary>
    public double Zoom { get; set; } = 1;
    public event Action<string>? Status;

    /// <summary>The text items of the current page that matched the last search.</summary>
    public IReadOnlyList<TextItem> Matches { get; private set; } = [];

    public void GoTo(int index)
    {
        if (Report.Pages.Count == 0) return;
        PageIndex = Math.Clamp(index, 0, Report.Pages.Count - 1);
        Matches = [];
        Show();
    }

    private double EffectiveZoom()
    {
        if (Zoom > 0 || Report.Pages.Count == 0) return Zoom <= 0 ? 1 : Zoom;
        var available = Math.Max(200, _scroller.Bounds.Width - 48);
        return available / (Report.Pages[PageIndex].Width / 72 * 96);
    }

    private void Show()
    {
        if (Report.Pages.Count == 0) { _pageLabel.Text = "No pages"; _image.Source = null; return; }
        var page = Report.Pages[PageIndex];
        var zoom = EffectiveZoom();
        using var stream = new MemoryStream(ImageRenderer.Png(page, 96 * zoom));
        _image.Source = new Bitmap(stream);
        _pageLabel.Text = $"Page {PageIndex + 1} of {Report.Pages.Count}";
        _highlights.Children.Clear();
        var scale = 96 * zoom / 72;
        foreach (var m in Matches)
        {
            var r = new Rectangle { Width = m.W * scale + 4, Height = m.H * scale + 4, Stroke = Brushes.OrangeRed, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(50, 255, 200, 0)) };
            Canvas.SetLeft(r, m.X * scale - 2);
            Canvas.SetTop(r, m.Y * scale - 2);
            _highlights.Children.Add(r);
        }
    }

    /// <summary>Finds text from the page after the current match onward; returns false when nothing matches.</summary>
    public bool FindNext(string text)
    {
        if (text.Length == 0 || Report.Pages.Count == 0) return false;
        for (int step = 0; step < Report.Pages.Count; step++)
        {
            var index = (PageIndex + (Matches.Count > 0 ? 1 : 0) + step) % Report.Pages.Count;
            var hits = Report.Pages[index].Items.OfType<TextItem>().Where(t => t.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) continue;
            PageIndex = index;
            Matches = hits;
            Show();
            return true;
        }
        Matches = [];
        Show();
        Status?.Invoke($"\"{text}\" was not found.");
        return false;
    }

    private async Task Export()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export report",
            SuggestedFileName = Report.Title + ".pdf",
            FileTypeChoices =
            [
                new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }, new FilePickerFileType("HTML") { Patterns = ["*.html"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] }, new FilePickerFileType("PNG images") { Patterns = ["*.png"] },
                new FilePickerFileType("XML") { Patterns = ["*.xml"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        ReportEngine.WriteFile(Report, path);
        Status?.Invoke($"Exported {System.IO.Path.GetFileName(path)}.");
    }

    private void Print()
    {
        var pdf = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Report.Title}-{Guid.NewGuid():N}.pdf");
        PdfRenderer.Write(Report, pdf);
        Status?.Invoke(_engine.PrintHandler(pdf, true) ? "Sent to the printer." : $"Printing is not available; the report was saved as {pdf}.");
    }
}
