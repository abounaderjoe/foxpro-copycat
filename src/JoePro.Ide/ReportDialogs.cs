using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using JoePro.Documents.Reports;
using JoePro.Runtime;

namespace JoePro.Ide;

/// <summary>Report Designer dialogs: data grouping, variables, title/summary, page setup, data environment, quick report.</summary>
public static class ReportDialogs
{
    private static Window Dialog(string title, Control content, double width = 460) => new()
    {
        Title = Strings.T(title), Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(12), Child = content },
    };

    private static void Show(Control anchor, Window dialog)
    {
        if (TopLevel.GetTopLevel(anchor) is Window owner) _ = dialog.ShowDialog(owner);
        else dialog.Show();
    }

    private static Control Row(string label, Control editor) =>
        new DockPanel { Margin = new Thickness(0, 3), Children = { new TextBlock { Text = Strings.T(label), Width = 130, VerticalAlignment = VerticalAlignment.Center }, editor } };

    private static StackPanel ButtonRow(IEnumerable<Button> buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    /// <summary>Data Grouping: add, remove, reorder and edit groups.</summary>
    public static void Groups(ReportDesigner d)
    {
        var s = d.Session;
        var list = new ListBox { Height = 140 };
        var expr = new TextBox();
        var newPage = new CheckBox { Content = Strings.T("Start each group on a new page") };
        var newColumn = new CheckBox { Content = Strings.T("Start each group in a new column") };
        var resetPage = new CheckBox { Content = Strings.T("Reset page number to 1 for each group") };
        var reprint = new CheckBox { Content = Strings.T("Reprint group header on each page") };
        var minSpace = new TextBox { Text = "0" };
        void Fill()
        {
            var keep = list.SelectedIndex;
            list.Items.Clear();
            for (int i = 0; i < s.Document.Groups.Count; i++) list.Items.Add($"{i + 1}. {s.Document.Groups[i].Expression}");
            list.SelectedIndex = Math.Min(keep, list.Items.Count - 1);
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= s.Document.Groups.Count) return;
            var g = s.Document.Groups[list.SelectedIndex];
            expr.Text = g.Expression; newPage.IsChecked = g.NewPage; newColumn.IsChecked = g.NewColumn; resetPage.IsChecked = g.ResetPageNumber;
            reprint.IsChecked = g.ReprintHeader; minSpace.Text = g.MinSpace.ToString(System.Globalization.CultureInfo.InvariantCulture);
        };
        Button B(string text, Action act) { var b = new Button { Content = Strings.T(text) }; b.Click += (_, _) => { act(); Fill(); }; return b; }
        var add = B("Add", () => { if ((expr.Text ?? "").Trim() is { Length: > 0 } e) { s.AddGroup(e); list.SelectedIndex = s.Document.Groups.Count - 1; } });
        var apply = B("Apply", () =>
        {
            var i = list.SelectedIndex;
            if (i < 0) return;
            double.TryParse(minSpace.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms);
            s.Update("Edit group", doc =>
            {
                var g = doc.Groups[i];
                g.Expression = (expr.Text ?? "").Trim();
                g.NewPage = newPage.IsChecked == true; g.NewColumn = newColumn.IsChecked == true; g.ResetPageNumber = resetPage.IsChecked == true;
                g.ReprintHeader = reprint.IsChecked == true; g.MinSpace = Math.Max(0, ms);
            });
        });
        var remove = B("Remove", () => { if (list.SelectedIndex >= 0) s.RemoveGroup(list.SelectedIndex + 1); });
        var up = B("Outward", () => { if (list.SelectedIndex > 0) { s.MoveGroupInward(list.SelectedIndex); list.SelectedIndex--; } });
        var down = B("Inward", () => { if (list.SelectedIndex >= 0) { s.MoveGroupInward(list.SelectedIndex + 1); list.SelectedIndex++; } });
        var close = new Button { Content = Strings.T("Close"), IsCancel = true };
        var dialog = Dialog("Data Grouping", new StackPanel
        {
            Children =
            {
                new TextBlock { Text = Strings.T("Groups (outermost first)"), FontWeight = FontWeight.SemiBold },
                list,
                Row("Group on expression", expr),
                newPage, newColumn, resetPage, reprint,
                Row("New page if less than (in)", minSpace),
                ButtonRow([add, apply, remove, up, down, close]),
            },
        }, 520);
        close.Click += (_, _) => dialog.Close();
        Fill();
        Show(d, dialog);
    }

    /// <summary>Report variables: name, value, initial value, calculation and reset.</summary>
    public static void Variables(ReportDesigner d)
    {
        var s = d.Session;
        var list = new ListBox { Height = 120 };
        var name = new TextBox();
        var value = new TextBox();
        var initial = new TextBox();
        var calc = new ComboBox { ItemsSource = Enum.GetNames<CalcType>(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var reset = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var release = new CheckBox { Content = Strings.T("Release after the report"), IsChecked = true };
        void FillResets()
        {
            reset.Items.Clear();
            reset.Items.Add("Report"); reset.Items.Add("Page"); reset.Items.Add("Column");
            for (int i = 0; i < s.Document.Groups.Count; i++) reset.Items.Add(Strings.F("Group {0}: {1}", i + 1, s.Document.Groups[i].Expression));
            reset.SelectedIndex = 0;
        }
        void Fill()
        {
            list.Items.Clear();
            foreach (var v in s.Document.Variables) list.Items.Add($"{v.Name} = {v.Value}{(v.Calculate != CalcType.None ? $" ({v.Calculate.ToString().ToLowerInvariant()})" : "")}");
        }
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= s.Document.Variables.Count) return;
            var v = s.Document.Variables[list.SelectedIndex];
            name.Text = v.Name; value.Text = v.Value; initial.Text = v.Initial ?? ""; calc.SelectedItem = v.Calculate.ToString();
            reset.SelectedIndex = v.Reset switch { ResetScope.Page => 1, ResetScope.Column => 2, ResetScope.Group => 2 + v.ResetGroup, _ => 0 };
            release.IsChecked = v.Release;
        };
        ReportVariable FromFields() => new()
        {
            Name = (name.Text ?? "").Trim(), Value = (value.Text ?? "").Trim(), Initial = string.IsNullOrWhiteSpace(initial.Text) ? null : initial.Text.Trim(),
            Calculate = Enum.Parse<CalcType>((string)calc.SelectedItem!), Release = release.IsChecked == true,
            Reset = reset.SelectedIndex switch { 1 => ResetScope.Page, 2 => ResetScope.Column, > 2 => ResetScope.Group, _ => ResetScope.Report },
            ResetGroup = Math.Max(0, reset.SelectedIndex - 2),
        };
        var message = new TextBlock { Foreground = Brushes.IndianRed };
        Button B(string text, Action act) { var b = new Button { Content = Strings.T(text) }; b.Click += (_, _) => { message.Text = ""; act(); Fill(); }; return b; }
        var add = B("Add", () =>
        {
            var v = FromFields();
            if (!System.Text.RegularExpressions.Regex.IsMatch(v.Name, @"^[A-Za-z_]\w*$")) { message.Text = Strings.T("Give the variable a valid name."); return; }
            if (s.Document.Variables.Any(x => x.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase))) { message.Text = Strings.F("{0} already exists.", v.Name); return; }
            s.Update($"Add variable {v.Name}", doc => doc.Variables.Add(v));
        });
        var apply = B("Apply", () => { var i = list.SelectedIndex; if (i >= 0) { var v = FromFields(); s.Update($"Edit variable {v.Name}", doc => doc.Variables[i] = v); } });
        var remove = B("Remove", () => { var i = list.SelectedIndex; if (i >= 0) s.Update("Remove variable", doc => doc.Variables.RemoveAt(i)); });
        var close = new Button { Content = Strings.T("Close"), IsCancel = true };
        var dialog = Dialog("Report Variables", new StackPanel
        {
            Children =
            {
                list, Row("Name", name), Row("Value to store", value), Row("Initial value", initial), Row("Calculate", calc), Row("Reset at", reset),
                release, message, ButtonRow([add, apply, remove, close]),
            },
        }, 500);
        close.Click += (_, _) => dialog.Close();
        FillResets();
        Fill();
        Show(d, dialog);
    }

    public static void TitleSummary(ReportDesigner d)
    {
        var doc = d.Session.Document;
        var title = new CheckBox { Content = Strings.T("Title band"), IsChecked = doc.Band(BandKind.Title) != null };
        var titlePage = new CheckBox { Content = Strings.T("Title on its own page"), IsChecked = doc.TitleOnNewPage, Margin = new Thickness(20, 0, 0, 0) };
        var summary = new CheckBox { Content = Strings.T("Summary band"), IsChecked = doc.Band(BandKind.Summary) != null };
        var summaryPage = new CheckBox { Content = Strings.T("Summary on its own page"), IsChecked = doc.SummaryOnNewPage, Margin = new Thickness(20, 0, 0, 0) };
        var columns = new CheckBox { Content = Strings.T("Column header and footer bands"), IsChecked = doc.Band(BandKind.ColumnHeader) != null };
        var ok = new Button { Content = Strings.T("OK"), IsDefault = true };
        var cancel = new Button { Content = Strings.T("Cancel"), IsCancel = true };
        var dialog = Dialog("Title/Summary", new StackPanel { Children = { title, titlePage, summary, summaryPage, columns, ButtonRow([ok, cancel]) } }, 360);
        ok.Click += (_, _) =>
        {
            d.Session.Transaction("Title/Summary", () =>
            {
                d.Session.SetTitleSummary(title.IsChecked == true, summary.IsChecked == true, titlePage.IsChecked == true, summaryPage.IsChecked == true);
                d.Session.SetColumnBands(columns.IsChecked == true);
            });
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();
        Show(d, dialog);
    }

    private static readonly (string Name, double W, double H)[] Papers = [("Letter", 8.5, 11), ("Legal", 8.5, 14), ("A4", 8.2677, 11.6929), ("A5", 5.8268, 8.2677), ("A3", 11.6929, 16.5354), ("Executive", 7.25, 10.5)];

    public static void PageSetup(ReportDesigner d)
    {
        var doc = d.Session.Document;
        string N(double v) => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        var paper = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var p in Papers) paper.Items.Add(p.Name);
        paper.Items.Add("Custom");
        paper.SelectedItem = Papers.Any(p => p.Name == doc.Paper) ? doc.Paper : "Custom";
        var width = new TextBox { Text = N(doc.PageWidth) };
        var height = new TextBox { Text = N(doc.PageHeight) };
        paper.SelectionChanged += (_, _) => { if (Papers.FirstOrDefault(p => p.Name == (string?)paper.SelectedItem) is { Name: not null } p) { width.Text = N(p.W); height.Text = N(p.H); } };
        var landscape = new CheckBox { Content = Strings.T("Landscape"), IsChecked = doc.Landscape };
        var left = new TextBox { Text = N(doc.LeftMargin) };
        var right = new TextBox { Text = N(doc.RightMargin) };
        var top = new TextBox { Text = N(doc.TopMargin) };
        var bottom = new TextBox { Text = N(doc.BottomMargin) };
        var cols = new TextBox { Text = doc.Columns.ToString() };
        var colWidth = new TextBox { Text = N(doc.ColumnWidth) };
        var spacing = new TextBox { Text = N(doc.ColumnSpacing) };
        var across = new CheckBox { Content = Strings.T("Print columns across (left to right)"), IsChecked = doc.ColumnOrder == ColumnOrder.Across };
        var message = new TextBlock { Foreground = Brushes.IndianRed };
        var ok = new Button { Content = Strings.T("OK"), IsDefault = true };
        var cancel = new Button { Content = Strings.T("Cancel"), IsCancel = true };
        var dialog = Dialog("Page Setup", new StackPanel
        {
            Children =
            {
                Row("Paper", paper), Row("Width (in)", width), Row("Height (in)", height), landscape,
                Row("Left margin", left), Row("Right margin", right), Row("Top margin", top), Row("Bottom margin", bottom),
                Row("Columns", cols), Row("Column width (0 = auto)", colWidth), Row("Column spacing", spacing), across,
                message, ButtonRow([ok, cancel]),
            },
        });
        ok.Click += (_, _) =>
        {
            double P(TextBox b) => double.Parse(b.Text ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                var (w, h, l, r, t, bm, cw, sp) = (P(width), P(height), P(left), P(right), P(top), P(bottom), P(colWidth), P(spacing));
                var n = int.Parse(cols.Text ?? "1");
                if (w <= 0 || h <= 0 || n < 1) throw new FormatException();
                d.Session.Update("Page setup", x =>
                {
                    x.Paper = (string?)paper.SelectedItem ?? "Custom"; x.PageWidth = w; x.PageHeight = h; x.Landscape = landscape.IsChecked == true;
                    x.LeftMargin = l; x.RightMargin = r; x.TopMargin = t; x.BottomMargin = bm;
                    x.Columns = n; x.ColumnWidth = cw; x.ColumnSpacing = sp; x.ColumnOrder = across.IsChecked == true ? ColumnOrder.Across : ColumnOrder.Down;
                });
                dialog.Close();
            }
            catch (FormatException) { message.Text = Strings.T("Enter numbers (inches) for the sizes and a whole number of columns."); }
        };
        cancel.Click += (_, _) => dialog.Close();
        Show(d, dialog);
    }

    /// <summary>The data environment as code (DEFINE CLASS … AS DataEnvironment), with a shortcut to add a table.</summary>
    public static void DataEnvironment(ReportDesigner d)
    {
        var editor = new TextEditor
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,DejaVu Sans Mono,monospace"), FontSize = 13, ShowLineNumbers = true, Height = 320,
            SyntaxHighlighting = FoxProHighlighting.Get(false),
            Text = d.Session.Document.DataEnvironment ?? "DEFINE CLASS ReportDataEnvironment AS DataEnvironment\n    PROCEDURE BeforeOpenTables\n    ENDPROC\nENDDEFINE\n",
        };
        var table = new TextBox { Watermark = Strings.T("table file (for example customer.jpt)") };
        var addTable = new Button { Content = Strings.T("Add table") };
        var message = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        var ok = new Button { Content = Strings.T("OK"), IsDefault = false };
        var remove = new Button { Content = Strings.T("No data environment") };
        var cancel = new Button { Content = Strings.T("Cancel"), IsCancel = true };
        var dialog = Dialog("Data Environment", new StackPanel
        {
            Children = { editor, new DockPanel { Margin = new Thickness(0, 6), Children = { addTable, table } }, message, ButtonRow([remove, ok, cancel]) },
        }, 640);
        DockPanel.SetDock(addTable, Dock.Right);
        addTable.Click += (_, _) =>
        {
            try
            {
                d.Session.Update("Data environment", x => x.DataEnvironment = editor.Text);
                d.AddTable(table.Text ?? "");
                editor.Text = d.Session.Document.DataEnvironment ?? editor.Text;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException) { message.Text = ex.Message; }
        };
        ok.Click += (_, _) =>
        {
            try
            {
                JoePro.Language.Parser.ParseProgram(editor.Text, "DE");
                d.Session.Update("Data environment", x => x.DataEnvironment = editor.Text.Trim().Length == 0 ? null : editor.Text);
                dialog.Close();
            }
            catch (JoePro.Language.CompileException ex) { message.Text = ex.Message; }
        };
        remove.Click += (_, _) => { d.Session.Update("Data environment", x => x.DataEnvironment = null); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        Show(d, dialog);
    }

    public static void QuickReport(ReportDesigner d, Interpreter rt)
    {
        var aliases = rt.Session.OpenWorkAreas().Select(w => w.Alias).ToList();
        if (aliases.Count == 0) throw new InvalidOperationException("Open a table first (USE, or add one to the data environment).");
        var alias = new ComboBox { ItemsSource = aliases, SelectedItem = rt.Session.Current.InUse ? rt.Session.Current.Alias : aliases[0], HorizontalAlignment = HorizontalAlignment.Stretch };
        var columnar = new RadioButton { Content = Strings.T("Columns (one row per record)"), IsChecked = true, GroupName = "layout" };
        var form = new RadioButton { Content = Strings.T("Form (one field per line)"), GroupName = "layout" };
        var ok = new Button { Content = Strings.T("OK"), IsDefault = true };
        var cancel = new Button { Content = Strings.T("Cancel"), IsCancel = true };
        var dialog = Dialog("Quick Report", new StackPanel { Children = { Row("Table", alias), columnar, form, new TextBlock { Text = Strings.T("The current layout is replaced (Undo brings it back)."), Opacity = 0.7 }, ButtonRow([ok, cancel]) } }, 400);
        ok.Click += (_, _) => { d.QuickReport((string)alias.SelectedItem!, columnar.IsChecked == true); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        Show(d, dialog);
    }

    /// <summary>CREATE LABEL: choose a label sheet layout.</summary>
    public static void LabelLayout(Control anchor, Action<LabelPreset> chosen)
    {
        var list = new ListBox { Height = 260 };
        foreach (var p in LabelPresets.All) list.Items.Add(p.Name);
        list.SelectedIndex = 0;
        var ok = new Button { Content = Strings.T("OK"), IsDefault = true };
        var cancel = new Button { Content = Strings.T("Cancel"), IsCancel = true };
        var dialog = Dialog("New Label", new StackPanel { Children = { new TextBlock { Text = Strings.T("Label layout"), FontWeight = FontWeight.SemiBold }, list, ButtonRow([ok, cancel]) } }, 480);
        ok.Click += (_, _) => { chosen(LabelPresets.All[Math.Max(0, list.SelectedIndex)]); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        list.DoubleTapped += (_, _) => { chosen(LabelPresets.All[Math.Max(0, list.SelectedIndex)]); dialog.Close(); };
        Show(anchor, dialog);
    }
}
