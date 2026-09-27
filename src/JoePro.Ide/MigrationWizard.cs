using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JoePro.Migration;

namespace JoePro.Ide;

/// <summary>The migration wizard in a document tab.</summary>
public sealed class MigrationWizardTab : DocumentTab
{
    public MigrationWizardTab(MigrationWizard wizard)
    {
        Wizard = wizard;
        Content = wizard;
        Title = "Migrate FoxPro application";
    }

    public MigrationWizard Wizard { get; }
    public override bool CanClose() => !Wizard.IsImporting;
}

/// <summary>
/// The migration wizard: pick a FoxPro application folder (with a summary of what it holds), choose where the Joe Pro
/// version goes, import it with progress (cancellable), then open the converted project and the migration report.
/// </summary>
public sealed class MigrationWizard : UserControl
{
    public static readonly string[] PageNames = ["Source", "Options", "Import", "Done"];

    private readonly TextBox _source = new() { Watermark = "Folder with the FoxPro application (.pjx, .scx, .prg, .dbf …)" };
    private readonly TextBox _target = new() { Watermark = "Folder for the Joe Pro version" };
    private readonly StackPanel _scanPanel = new() { Spacing = 2, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _openProject = new() { Content = "Open the converted project", IsChecked = true };
    private readonly CheckBox _showReport = new() { Content = "Show the migration report", IsChecked = true };
    private readonly CheckBox _setDefault = new() { Content = "Make the new folder the current folder (SET DEFAULT)", IsChecked = true };
    private readonly TextBlock _targetNote = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 8, Margin = new Thickness(0, 8) };
    private readonly TextBlock _progressStep = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _progressFile = new() { Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _donePanel = new() { Spacing = 8 };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _steps = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ContentControl _page = new();
    private readonly Control[] _pages;
    private readonly Button _back = new() { Content = "Back" };
    private readonly Button _next = new() { Content = "Next", Classes = { "accent" } };
    private readonly Button _cancel = new() { Content = "Cancel" };
    private CancellationTokenSource? _cts;
    private bool _targetEdited, _settingTarget;
    private string? _scannedFolder;

    public MigrationWizard(string? source = null)
    {
        _source.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Scan(); };
        _source.LostFocus += (_, _) => Scan();
        _target.TextChanged += (_, _) =>
        {
            if (!_settingTarget) _targetEdited = true;
            UpdateTargetNote();
        };
        _back.Click += (_, _) => Back();
        _next.Click += (_, _) => Next();
        _cancel.Click += (_, _) => Cancel();

        _pages = [SourcePage(), OptionsPage(), ImportPage(), _donePanel];
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, _back, _next } };
        var root = new DockPanel { Margin = new Thickness(20), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        DockPanel.SetDock(_steps, Dock.Top);
        root.Children.Add(_steps);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        DockPanel.SetDock(_message, Dock.Bottom);
        root.Children.Add(_message);
        root.Children.Add(new ScrollViewer { Content = _page });
        Content = root;
        ShowPage(0);
        if (source != null) SourceFolder = source;
    }

    public int Page { get; private set; }
    public bool IsImporting => _cts != null;
    public MigrationScan? ScanResult { get; private set; }
    public MigrationReport? Report { get; private set; }
    /// <summary>The .jpproj files the import produced.</summary>
    public IReadOnlyList<string> ConvertedProjects => Report == null ? [] :
        Report.Findings.Where(f => f.Rule == "PROJECT.CONVERTED" && f.Target != null).Select(f => f.Target!.File).Distinct().ToList();
    public string Message => _message.Text ?? "";

    public string SourceFolder
    {
        get => (_source.Text ?? "").Trim();
        set
        {
            _source.Text = value;
            Scan();
        }
    }

    public string TargetFolder
    {
        get => (_target.Text ?? "").Trim();
        set => _target.Text = value;
    }

    public bool OpenProjectWhenDone { get => _openProject.IsChecked == true; set => _openProject.IsChecked = value; }
    public bool ShowReportWhenDone { get => _showReport.IsChecked == true; set => _showReport.IsChecked = value; }
    public bool SetDefaultWhenDone { get => _setDefault.IsChecked == true; set => _setDefault.IsChecked = value; }

    /// <summary>Raised when the import finished: the report and the target folder.</summary>
    public event Action<MigrationReport, string>? Finished;
    public event Action<string>? OpenRequested;
    public event Action<MigrationReport>? ReportRequested;

    // ---- Pages ------------------------------------------------------------------------------------

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 8) };

    private Control PathRow(TextBox box, string title, Action<string> picked)
    {
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) picked(path);
        };
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(box);
        return row;
    }

    private Control SourcePage() => new StackPanel
    {
        Children =
        {
            Heading("Which FoxPro application do you want to migrate?"),
            Note("Pick the folder with its project, forms, class libraries, reports, menus, programs and data. The folder is only read; " +
                 "the Joe Pro version goes to a new folder."),
            PathRow(_source, "FoxPro application folder", p => SourceFolder = p),
            _scanPanel,
        },
    };

    private Control OptionsPage() => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            Heading("Where should the Joe Pro version go?"),
            Note("Tables and databases are imported, forms, class libraries, reports, labels, menus and projects are converted to Joe Pro " +
                 "documents, and programs and other files are copied. The folder layout is kept, so relative paths keep working."),
            PathRow(_target, "Folder for the Joe Pro version", p => TargetFolder = p),
            _targetNote,
            new Border { Height = 8 },
            _openProject,
            _showReport,
            _setDefault,
        },
    };

    private Control ImportPage() => new StackPanel
    {
        Children = { Heading("Importing…"), _progressStep, _progress, _progressFile },
    };

    private void ShowPage(int page)
    {
        Page = page;
        _page.Content = _pages[page];
        _steps.Children.Clear();
        for (int i = 0; i < PageNames.Length; i++)
        {
            if (i > 0) _steps.Children.Add(new TextBlock { Text = "›", Opacity = 0.5 });
            _steps.Children.Add(new TextBlock { Text = $"{i + 1} {PageNames[i]}", FontWeight = i == page ? FontWeight.Bold : FontWeight.Normal, Opacity = i == page ? 1 : 0.6 });
        }
        _back.IsVisible = page == 1;
        _next.IsVisible = page < 2;
        _next.Content = page == 1 ? "Import" : "Next";
        _cancel.IsVisible = page == 2;
        UpdateNext();
    }

    private void UpdateNext() => _next.IsEnabled = Page switch
    {
        0 => ScanResult is { Files: > 0 },
        1 => TargetFolder.Length > 0,
        _ => false,
    };

    // ---- Steps ------------------------------------------------------------------------------------

    /// <summary>Scans the source folder (once per folder) and shows what it holds.</summary>
    public void Scan()
    {
        var folder = SourceFolder;
        if (folder == _scannedFolder) return;
        _scannedFolder = folder;
        _scanPanel.Children.Clear();
        ScanResult = null;
        _message.Text = "";
        if (folder.Length == 0) { UpdateNext(); return; }
        if (!Directory.Exists(folder))
        {
            _message.Text = $"The folder {folder} does not exist.";
            UpdateNext();
            return;
        }
        try { ScanResult = MigrationScan.Scan(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _message.Text = ex.Message;
            UpdateNext();
            return;
        }
        var scan = ScanResult;
        _scanPanel.Children.Add(new TextBlock { Text = $"{scan.Files} file(s), {Size(scan.Bytes)}", FontWeight = FontWeight.SemiBold });
        foreach (var (kind, count) in scan.Kinds)
            _scanPanel.Children.Add(new TextBlock { Text = $"   {count,5}  {kind}" , FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace") });
        if (scan.Projects.Count > 0)
            _scanPanel.Children.Add(new TextBlock { Text = "Projects: " + string.Join(", ", scan.Projects), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        else
            _scanPanel.Children.Add(Note("No project (.pjx) was found; every file in the folder is migrated."));
        if (scan.Files == 0) _message.Text = "The folder is empty.";
        if (!_targetEdited)
        {
            _settingTarget = true;
            _target.Text = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-joepro";
            _settingTarget = false;
        }
        UpdateNext();
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    private void UpdateTargetNote()
    {
        var t = TargetFolder;
        var src = SourceFolder.Length > 0 ? Path.GetFullPath(SourceFolder) : "";
        _targetNote.Text =
            t.Length == 0 ? "" :
            src.Length > 0 && string.Equals(Path.GetFullPath(t).TrimEnd('/', '\\'), src.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase)
                ? "Choose a different folder from the FoxPro application."
            : Directory.Exists(t) && Directory.EnumerateFileSystemEntries(t).Any()
                ? "The folder is not empty: converted files replace files of the same name, other files are kept."
            : "The folder will be created.";
        UpdateNext();
    }

    public void Next()
    {
        _message.Text = "";
        if (Page == 0)
        {
            Scan();
            if (ScanResult is not { Files: > 0 }) return;
            ShowPage(1);
            UpdateTargetNote();
        }
        else if (Page == 1) _ = ImportAsync();
    }

    public void Back()
    {
        if (Page == 1) ShowPage(0);
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>Runs the import in the background, showing progress; ends on the Done page (or back on Options).</summary>
    public async Task ImportAsync()
    {
        var source = Path.GetFullPath(SourceFolder);
        var target = Path.GetFullPath(TargetFolder);
        if (string.Equals(source.TrimEnd('/', '\\'), target.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
        {
            _message.Text = "Choose a different folder from the FoxPro application.";
            return;
        }
        _cts = new CancellationTokenSource();
        ShowPage(2);
        _progress.Value = 0;
        _progressStep.Text = "Starting…";
        _progressFile.Text = "";
        var report = new MigrationReport();
        var importer = new LegacyImporter(report) { Cancellation = _cts.Token };
        importer.Progress += p => Dispatcher.UIThread.Post(() => ShowProgress(p));
        try
        {
            await Task.Run(() =>
            {
                importer.ImportFolder(source, target);
                report.Save(target);
            });
            Report = report;
            ShowDone(target);
            Finished?.Invoke(report, target);
        }
        catch (OperationCanceledException)
        {
            ShowPage(1);
            _message.Text = "The import was cancelled. Files converted so far are in the target folder.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowPage(1);
            _message.Text = "The import stopped: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ShowProgress(MigrationProgress p)
    {
        if (Page != 2) return;
        _progress.Value = p.Total == 0 ? 1 : (double)p.Done / p.Total;
        _progressStep.Text = p.Total == 0 ? p.Step : $"{p.Step} ({Math.Min(p.Done + 1, p.Total)} of {p.Total})";
        _progressFile.Text = p.File;
    }

    private void ShowDone(string target)
    {
        var report = Report!;
        _donePanel.Children.Clear();
        _donePanel.Children.Add(Heading("The application was migrated"));
        _donePanel.Children.Add(new TextBlock { Text = $"{report.ReadinessScore:P0} of the findings need no action.", FontSize = 15 });
        var counts = new WrapPanel();
        foreach (var status in Enum.GetValues<FindingStatus>())
        {
            var n = report.Findings.Count(f => f.Status == status);
            if (n == 0) continue;
            counts.Children.Add(MigrationReportView.Badge($"{MigrationReportView.StatusText(status)}  {n}", status, new Thickness(0, 0, 6, 6)));
        }
        _donePanel.Children.Add(counts);
        _donePanel.Children.Add(Note($"The Joe Pro version is in {target}."));
        var actions = new WrapPanel();
        Button Action(string text, Action act)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => act();
            actions.Children.Add(b);
            return b;
        }
        foreach (var project in ConvertedProjects)
            Action($"Open project {Path.GetFileName(project)}", () => OpenRequested?.Invoke(project));
        Action("View migration report", () => ReportRequested?.Invoke(report));
        var html = Path.Combine(target, "migration-report.html");
        Action("Open HTML report", () => _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchFileInfoAsync(new FileInfo(html)));
        Action("Migrate another application", () =>
        {
            Report = null;
            _scannedFolder = null;
            _targetEdited = false;
            SourceFolder = "";
            ShowPage(0);
        });
        _donePanel.Children.Add(actions);
        ShowPage(3);
    }
}

/// <summary>A migration report (migration-report.json) in a document tab.</summary>
public sealed class MigrationReportTab : DocumentTab
{
    public MigrationReportTab(MigrationReportView view)
    {
        View = view;
        Content = view;
        Title = "Migration report" + (view.Report.Target.Length > 0 ? " · " + Path.GetFileName(view.Report.Target.TrimEnd('/', '\\')) : "");
    }

    public MigrationReportView View { get; }
}

/// <summary>
/// The migration report in the IDE: readiness and counts per status, findings filtered by status, category and text,
/// the selected finding's details, and opening the converted file (at the line for code) or the original.
/// </summary>
public sealed class MigrationReportView : UserControl
{
    public const string NeedsAttention = "Needs attention";
    public const string AllStatuses = "All statuses";
    public const string AllCategories = "All categories";

    private readonly ComboBox _status = new() { MinWidth = 170, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox _category = new() { MinWidth = 150, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _search = new() { Watermark = "Search findings", MinWidth = 220 };
    private readonly ListBox _list = new();
    private readonly SelectableTextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button _openConverted = new() { Content = "Open converted file", Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _openOriginal = new() { Content = "Open original" };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0) };
    private List<Finding> _visible = new();
    private bool _filling;

    public MigrationReportView(MigrationReport report)
    {
        Report = report;
        _status.ItemsSource = new[] { NeedsAttention, AllStatuses }.Concat(Enum.GetValues<FindingStatus>().Select(StatusText)).ToList();
        _category.ItemsSource = new[] { AllCategories }.Concat(report.Findings.Select(f => f.Category).Distinct().Order(StringComparer.Ordinal)).ToList();
        _status.SelectedItem = report.Findings.Any(IsAttention) ? NeedsAttention : AllStatuses;
        _category.SelectedItem = AllCategories;
        _status.SelectionChanged += (_, _) => Fill();
        _category.SelectionChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();
        _list.ItemTemplate = new FuncDataTemplate<Finding>((f, _) => f == null ? new TextBlock() : Row(f), supportsRecycling: false);
        _list.SelectionChanged += (_, _) => ShowDetails();
        _list.DoubleTapped += (_, _) => OpenSelected(converted: true);
        _openConverted.Click += (_, _) => OpenSelected(converted: true);
        _openOriginal.Click += (_, _) => OpenSelected(converted: false);

        var header = new StackPanel { Margin = new Thickness(10, 8), Spacing = 4 };
        header.Children.Add(new TextBlock { Text = "Migration of " + (report.Source.Length > 0 ? report.Source : "(unknown source)"), FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (report.Target.Length > 0) header.Children.Add(new TextBlock { Text = "to " + report.Target, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        chips.Children.Add(new TextBlock { Text = $"Readiness {report.ReadinessScore:P0}", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) });
        foreach (var status in Enum.GetValues<FindingStatus>())
        {
            var n = report.Findings.Count(f => f.Status == status);
            if (n == 0) continue;
            var chip = new Button { Content = Badge($"{StatusText(status)}  {n}", status, default), Padding = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 6, 6) };
            ToolTip.SetTip(chip, "Show only these findings");
            chip.Click += (_, _) => StatusFilter = StatusText(status);
            chips.Children.Add(chip);
        }
        header.Children.Add(chips);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 6), Children = { _status, _category, _search, _count } };
        var detailsPanel = new StackPanel
        {
            Margin = new Thickness(10, 6),
            Children = { _details, new StackPanel { Orientation = Orientation.Horizontal, Children = { _openConverted, _openOriginal } } },
        };
        var detailsBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), Child = detailsPanel, MaxHeight = 220 };
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(filters, Dock.Top);
        root.Children.Add(filters);
        DockPanel.SetDock(detailsBorder, Dock.Bottom);
        root.Children.Add(detailsBorder);
        root.Children.Add(_list);
        Content = root;
        Fill();
    }

    public MigrationReport Report { get; }
    public IReadOnlyList<Finding> Visible => _visible;
    public string StatusFilter { get => _status.SelectedItem as string ?? AllStatuses; set => _status.SelectedItem = value; }
    public string CategoryFilter { get => _category.SelectedItem as string ?? AllCategories; set => _category.SelectedItem = value; }
    public string Search
    {
        get => _search.Text ?? "";
        set
        {
            _search.Text = value;
            Fill();
        }
    }
    public Finding? Selected { get => _list.SelectedItem as Finding; set => _list.SelectedItem = value; }
    public string Details => _details.Text ?? "";

    /// <summary>Opens a file in the IDE: the path and the line (0 for none).</summary>
    public event Action<string, int>? OpenRequested;

    public static bool IsAttention(Finding f) => f.Status is FindingStatus.NeedsReview or FindingStatus.Unsupported or FindingStatus.Failed;

    public static string StatusText(FindingStatus s) => s switch
    {
        FindingStatus.Converted => "Converted",
        FindingStatus.ConvertedWithChanges => "Converted with changes",
        FindingStatus.NeedsReview => "Needs review",
        FindingStatus.Unsupported => "Unsupported",
        _ => "Failed",
    };

    public static IBrush StatusBrush(FindingStatus s) => new SolidColorBrush(s switch
    {
        FindingStatus.Converted => Color.FromRgb(0x2E, 0x7D, 0x32),
        FindingStatus.ConvertedWithChanges => Color.FromRgb(0x00, 0x79, 0x6B),
        FindingStatus.NeedsReview => Color.FromRgb(0xB2, 0x6A, 0x00),
        FindingStatus.Unsupported => Color.FromRgb(0xC6, 0x28, 0x28),
        _ => Color.FromRgb(0x8E, 0x24, 0xAA),
    });

    public static Border Badge(string text, FindingStatus status, Thickness margin) => new()
    {
        Background = StatusBrush(status),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 1),
        Margin = margin,
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 11 },
    };

    /// <summary>Where a finding is: file › object › member : line.</summary>
    public static string Location(Finding f)
    {
        var s = f.Source.File;
        if (f.Source.Object is { Length: > 0 } o) s += " › " + o;
        if (f.Source.Member is { Length: > 0 } m) s += " › " + m;
        if (f.Source.Line is { } line) s += ":" + line;
        return s;
    }

    private Control Row(Finding f)
    {
        var badge = Badge(StatusText(f.Status), f.Status, new Thickness(0, 0, 8, 0));
        badge.Width = 150;
        var where = new TextBlock { Text = Location(f), Width = 280, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.85, Margin = new Thickness(0, 0, 8, 0) };
        var message = new TextBlock { Text = f.Message, TextTrimming = TextTrimming.CharacterEllipsis };
        var row = new DockPanel { Margin = new Thickness(0, 1) };
        DockPanel.SetDock(badge, Dock.Left);
        DockPanel.SetDock(where, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(where);
        row.Children.Add(message);
        return row;
    }

    private void Fill()
    {
        if (_filling) return;
        _filling = true;
        try
        {
            var status = StatusFilter;
            var category = CategoryFilter;
            var text = Search.Trim();
            _visible = Report.Findings.Where(f =>
                    status == AllStatuses || (status == NeedsAttention ? IsAttention(f) : StatusText(f.Status) == status))
                .Where(f => category == AllCategories || f.Category == category)
                .Where(f => text.Length == 0 || f.Message.Contains(text, StringComparison.OrdinalIgnoreCase) || Location(f).Contains(text, StringComparison.OrdinalIgnoreCase)
                            || f.Rule.Contains(text, StringComparison.OrdinalIgnoreCase) || (f.Source.Snippet?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(f => f.Status switch { FindingStatus.Failed => 0, FindingStatus.Unsupported => 1, FindingStatus.NeedsReview => 2, FindingStatus.ConvertedWithChanges => 3, _ => 4 })
                .ThenBy(f => f.Source.File, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Source.Line ?? 0)
                .ToList();
            _list.ItemsSource = _visible;
            _count.Text = $"{_visible.Count} of {Report.Findings.Count}";
        }
        finally { _filling = false; }
        ShowDetails();
    }

    private void ShowDetails()
    {
        var f = Selected;
        _openConverted.IsEnabled = f != null && ConvertedFile(f) != null;
        _openOriginal.IsEnabled = f != null && OriginalFile(f) != null;
        if (f == null)
        {
            _details.Text = _visible.Count == 0 ? "No findings match the filters." : "Select a finding to see its details.";
            return;
        }
        var lines = new List<string> { $"{StatusText(f.Status)} · {f.Category} · {f.Rule}", Location(f), "", f.Message };
        if (f.Source.Snippet is { Length: > 0 } snippet) lines.AddRange(["", "    " + snippet]);
        if (f.Action is { Length: > 0 } action) lines.AddRange(["", "What to do: " + action]);
        if (f.Target?.File is { Length: > 0 } target) lines.AddRange(["", "Converted to " + target]);
        _details.Text = string.Join("\n", lines);
    }

    /// <summary>The Joe Pro file a finding is about (the converted document, or the copied program) and the line.</summary>
    public (string Path, int Line)? ConvertedFile(Finding f)
    {
        var line = f.Source.Line ?? 0;
        if (f.Target?.File is { Length: > 0 } t && File.Exists(t))
            return (t, Path.GetExtension(t).ToLowerInvariant() is ".prg" or ".h" or ".mpr" or ".qpr" ? line : 0);
        if (Report.Target.Length > 0 && f.Source.File.Length > 0 && JoePro.Data.DataSession.FindIgnoringCase(Path.Combine(Report.Target, f.Source.File)) is { } copy
            && Path.GetExtension(copy).ToLowerInvariant() is ".prg" or ".h" or ".mpr" or ".qpr" or ".txt" or ".xml")
            return (copy, line);
        return null;
    }

    /// <summary>The FoxPro file a finding is about.</summary>
    public string? OriginalFile(Finding f) =>
        Report.Source.Length > 0 && f.Source.File.Length > 0 ? JoePro.Data.DataSession.FindIgnoringCase(Path.Combine(Report.Source, f.Source.File)) : null;

    public void OpenSelected(bool converted)
    {
        if (Selected is not { } f) return;
        if (converted && ConvertedFile(f) is var (path, line)) OpenRequested?.Invoke(path, line);
        else if (!converted && OriginalFile(f) is { } original) OpenRequested?.Invoke(original, f.Source.Line ?? 0);
    }
}
