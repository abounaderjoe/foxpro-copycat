using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using JoePro.Core;
using JoePro.Data;
using JoePro.Language;
using JoePro.Runtime;
using JoePro.Runtime.Builtins;
using Path = System.IO.Path;

namespace JoePro.Ui.Runtime;

/// <summary>
/// Renders FoxPro forms and controls with Avalonia and routes user interaction back into the
/// runtime as FoxPro events (When, GotFocus, InteractiveChange, Valid, LostFocus, Click…).
/// Positions and sizes use FoxPro's pixel coordinates.
/// </summary>
public sealed class AvaloniaUiHost : IUiHost
{
    private readonly Interpreter _rt;
    private readonly Dictionary<VfpObject, Window> _windows = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<VfpObject> _closing = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<VfpObject, DispatcherFrame> _modalFrames = new(ReferenceEqualityComparer.Instance);
    private CancellationTokenSource? _readEvents;
    private bool _designMode;
    private int _updating;
    private int _groupSeq;
    private int _refusing;

    public AvaloniaUiHost(Interpreter rt)
    {
        _rt = rt;
        rt.Ui = this;
    }

    /// <summary>Errors raised by FoxPro code running in event handlers.</summary>
    public event Action<VfpException>? Error;

    /// <summary>The IDE supplies these to show tool windows inside its shell.</summary>
    public Func<BrowseModel, bool>? BrowseHandler { get; set; }
    public Func<string, bool>? ModifyHandler { get; set; }

    /// <summary>Window that owns modal forms (the IDE main window), if any.</summary>
    public Window? Owner { get; set; }

    public IReadOnlyCollection<VfpObject> OpenForms => _windows.Keys;

    public Window? WindowOf(VfpObject form) => _windows.GetValueOrDefault(form);

    // ================================================================================
    // IUiHost
    // ================================================================================

    public void Show(VfpObject form, bool modal)
    {
        if (form.Class.BaseClass == "FormSet")
        {
            // A form set shows its forms (each in its own window); the set itself has no window.
            foreach (var f in form.Members.Where(m => m.Class.BaseClass == "Form").ToList())
                if (ValueText.IsTrue(Prop(f, "Visible")) || Prop(f, "Visible").Kind != ValueKind.Logical) Show(f, false);
            if (modal && !_modalFrames.ContainsKey(form))
            {
                var setFrame = new DispatcherFrame();
                _modalFrames[form] = setFrame;
                Dispatcher.UIThread.PushFrame(setFrame);
            }
            return;
        }
        if (!_windows.TryGetValue(form, out var w))
        {
            w = form.Class.BaseClass == "Toolbar" ? BuildToolbarWindow(form) : BuildWindow(form);
            _windows[form] = w;
        }
        Refresh(form);
        if (!w.IsVisible) w.Show();
        else w.Activate();
        if (modal && !_modalFrames.ContainsKey(form))
        {
            var frame = new DispatcherFrame();
            _modalFrames[form] = frame;
            Dispatcher.UIThread.PushFrame(frame);
        }
    }

    public void Hide(VfpObject form)
    {
        if (form.Class.BaseClass == "FormSet")
            foreach (var f in form.Members.Where(m => m.Class.BaseClass == "Form")) Hide(f);
        if (_windows.TryGetValue(form, out var w)) w.Hide();
        EndModal(form);
    }

    public void Release(VfpObject form)
    {
        EndModal(form);
        if (form.Class.BaseClass == "FormSet")
            foreach (var f in form.Members.Where(m => m.Class.BaseClass == "Form").ToList()) Release(f);
        if (!_windows.Remove(form, out var w)) return;
        _closing.Add(form);
        w.Close();
        _closing.Remove(form);
        foreach (var t in Descendants(form).Select(d => d.Native).OfType<DispatcherTimer>()) t.Stop();
    }

    private void EndModal(VfpObject form)
    {
        if (_modalFrames.Remove(form, out var frame)) frame.Continue = false;
    }

    public void PropertyChanged(VfpObject o, string property)
    {
        if (_updating > 0) return;
        if (o.Class.BaseClass == "Toolbar" && _windows.TryGetValue(o, out var tw))
        {
            if (property.Equals("Caption", StringComparison.OrdinalIgnoreCase)) tw.Title = Prop(o, "Caption").AsString;
            return;
        }
        if (Interpreter.OwningForm(o) == o && _windows.TryGetValue(o, out var w))
        {
            ApplyWindow(o, w, property);
            return;
        }
        if (o.Native is Control c) Apply(o, c, property.ToUpperInvariant());
        else if (o.Native is DispatcherTimer t) ApplyTimer(o, t);
    }

    public void Refresh(VfpObject o)
    {
        foreach (var d in Descendants(o).Prepend(o))
        {
            if (d.Native is not Control c) continue;
            if (d.Native is DataGrid && d.Class.BaseClass == "Grid") { ReloadGrid(d, (DataGrid)c); continue; }
            var cs = ControlSource(d);
            if (cs != null)
            {
                try
                {
                    var v = _rt.ReadControlSource(d, cs);
                    d.Set("Value", v);
                }
                catch (VfpException ex) { Error?.Invoke(ex); }
            }
            ShowValue(d, c);
        }
    }

    public void SetFocus(VfpObject control)
    {
        if (control.Native is Control c) c.Focus();
    }

    public void ReadEvents()
    {
        if (_readEvents != null) return; // already inside READ EVENTS
        _readEvents = new CancellationTokenSource();
        try { Dispatcher.UIThread.MainLoop(_readEvents.Token); }
        finally { _readEvents = null; }
    }

    public void ClearEvents() => _readEvents?.Cancel();

    public bool Browse(WorkArea area, IReadOnlyList<string>? fields)
    {
        var model = new BrowseModel(_rt, area, fields);
        model.Load();
        if (BrowseHandler != null) return BrowseHandler(model);
        var grid = CreateGrid(model, readOnly: false);
        new Window { Title = model.Title, Width = 700, Height = 400, Content = grid }.Show();
        return true;
    }

    public bool ModifyFile(string path) => ModifyHandler?.Invoke(path) ?? false;

    /// <summary>Set by the IDE to open designers (CREATE/MODIFY FORM, CLASS…).</summary>
    public Func<DesignerRequest, bool>? DesignerHandler { get; set; }

    public bool OpenDesigner(DesignerRequest request) => DesignerHandler?.Invoke(request) ?? false;

    /// <summary>Raised when the program's menus change (the IDE or app window rebuilds its application menu bar).</summary>
    public event Action? MenusUpdated;

    public void MenusChanged() => MenusUpdated?.Invoke();

    /// <summary>The FoxPro key label for a key press (ON KEY LABEL names: F2, CTRL+S, ALT+X, DNARROW, PGUP…), or null.</summary>
    public static string? KeyLabel(Avalonia.Input.KeyEventArgs e)
    {
        string? name = e.Key switch
        {
            >= Avalonia.Input.Key.F1 and <= Avalonia.Input.Key.F12 => "F" + (e.Key - Avalonia.Input.Key.F1 + 1),
            >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z => ((char)('A' + (e.Key - Avalonia.Input.Key.A))).ToString(),
            >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => ((char)('0' + (e.Key - Avalonia.Input.Key.D0))).ToString(),
            Avalonia.Input.Key.Enter => "ENTER", Avalonia.Input.Key.Escape => "ESC", Avalonia.Input.Key.Tab => "TAB",
            Avalonia.Input.Key.Left => "LEFTARROW", Avalonia.Input.Key.Right => "RIGHTARROW", Avalonia.Input.Key.Up => "UPARROW", Avalonia.Input.Key.Down => "DNARROW",
            Avalonia.Input.Key.Home => "HOME", Avalonia.Input.Key.End => "END", Avalonia.Input.Key.PageUp => "PGUP", Avalonia.Input.Key.PageDown => "PGDN",
            Avalonia.Input.Key.Delete => "DEL", Avalonia.Input.Key.Insert => "INS", Avalonia.Input.Key.Back => "BACKSPACE", Avalonia.Input.Key.Space => "SPACEBAR",
            _ => null,
        };
        if (name == null) return null;
        var mods = e.KeyModifiers;
        var prefix = (mods.HasFlag(Avalonia.Input.KeyModifiers.Control) ? "CTRL+" : "") + (mods.HasFlag(Avalonia.Input.KeyModifiers.Alt) ? "ALT+" : "")
            + (mods.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? "SHIFT+" : "");
        return prefix + name;
    }

    /// <summary>The Avalonia menu items for the active FoxPro menu bar (empty when there is none or SET SYSMENU OFF).</summary>
    public List<Control> ActiveMenuItems()
    {
        var m = _rt.Menus;
        if (!m.Visible || m.ActiveMenu == null || !m.Menus.TryGetValue(m.ActiveMenu, out var menu)) return [];
        return MenuBuilder.Pads(_rt, menu, ex => Error?.Invoke(ex));
    }

    /// <summary>For tests and hosts without a pointer: picks the item a shortcut popup should choose instead of showing it.</summary>
    public Func<PopupDef, string?>? PopupChooser { get; set; }

    /// <summary>ACTIVATE POPUP: shows the popup as a context menu on the active window and waits until it closes.</summary>
    public bool ActivatePopup(string name)
    {
        if (_rt.Menus.Popup(name) is not { } popup) return false;
        if (PopupChooser != null)
        {
            if (PopupChooser(popup) is { } chosen) Guard(() => _rt.SelectMenuItem(popup.Name, chosen, isPad: false));
            return true;
        }
        var window = _windows.Values.FirstOrDefault(w => w.IsActive) ?? Owner ?? _windows.Values.LastOrDefault();
        if (window?.Content is not Control target) return false;
        string? selected = null;
        var menu = new ContextMenu();
        foreach (var item in MenuBuilder.Bars(_rt, popup, ex => Error?.Invoke(ex)))
        {
            // Selection runs after the popup closes, as in VFP.
            if (item is MenuItem { Tag: BarDef bar } mi && mi.Items.Count == 0)
            {
                var fresh = new MenuItem { Header = mi.Header, IsEnabled = mi.IsEnabled, InputGesture = mi.InputGesture, Icon = mi.Icon };
                fresh.Click += (_, _) => selected = bar.Id;
                menu.Items.Add(fresh);
            }
            else menu.Items.Add(item);
        }
        var frame = new DispatcherFrame();
        menu.Closed += (_, _) => frame.Continue = false;
        menu.Open(target);
        if (menu.IsOpen) Dispatcher.UIThread.PushFrame(frame);
        if (selected != null) Guard(() => _rt.SelectMenuItem(popup.Name, selected, isPad: false));
        return true;
    }

    // ================================================================================
    // Design surface (Form Designer)
    // ================================================================================

    /// <summary>
    /// Renders a form's controls into a panel for the Form Designer: the same controls as at run time, but no
    /// event code is wired, timers do not run, and grids show their columns without data.
    /// </summary>
    public Canvas BuildDesignSurface(VfpObject form)
    {
        _designMode = true;
        if (!form.Class.BaseClass.Equals("Form", StringComparison.OrdinalIgnoreCase)) return BuildClassSurface(form);
        var canvas = new Canvas
        {
            Width = Math.Max(20, Prop(form, "Width").AsNumber),
            Height = Math.Max(20, Prop(form, "Height").AsNumber),
            Tag = form,
        };
        if (IsCustomColor(Prop(form, "BackColor"))) canvas.Background = new SolidColorBrush(ValueText.ToColor(Prop(form, "BackColor")));
        AddMembers(form, canvas);
        form.Native = canvas;
        return canvas;
    }

    /// <summary>
    /// The Class Designer's surface for a class that is not a form: the object itself at the top left (a container
    /// with its members, a control, or a placeholder for a non-visual class and the objects it holds).
    /// </summary>
    private Canvas BuildClassSurface(VfpObject root)
    {
        double N(string name, double fallback) => Prop(root, name) is { Kind: ValueKind.Number } v && v.AsNumber > 0 ? v.AsNumber : fallback;
        var canvas = new Canvas { Tag = root };
        var bc = root.Class.BaseClass;
        if (BaseClasses.IsVisual(bc) && bc is not ("Toolbar" or "FormSet"))
        {
            var c = Create(root)!;
            Canvas.SetLeft(c, 0);
            Canvas.SetTop(c, 0);
            canvas.Children.Add(c);
            canvas.Width = Math.Max(20, N("Width", 100));
            canvas.Height = Math.Max(20, N("Height", 25));
            return canvas;
        }
        // Non-visual classes (Custom, Session, Toolbar…) show their members on a plain surface.
        AddMembers(root, canvas);
        var extentW = canvas.Children.OfType<Control>().Select(ch => Canvas.GetLeft(ch) + (double.IsNaN(ch.Width) ? 70 : ch.Width)).DefaultIfEmpty(0).Max();
        var extentH = canvas.Children.OfType<Control>().Select(ch => Canvas.GetTop(ch) + (double.IsNaN(ch.Height) ? 24 : ch.Height)).DefaultIfEmpty(0).Max();
        canvas.Width = Math.Max(Math.Max(200, N("Width", 200)), extentW + 12);
        canvas.Height = Math.Max(Math.Max(120, N("Height", 120)), extentH + 12);
        canvas.Background = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128));
        root.Native = canvas;
        return canvas;
    }

    private Control CreateDesignPlaceholder(VfpObject o)
    {
        var c = new Border
        {
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
            Width = 70,
            Height = 24,
            Child = new TextBlock { Text = (o.Class.BaseClass == "Timer" ? "⏱ " : "◆ ") + o.Name, FontSize = 11, Margin = new Thickness(3, 2), TextTrimming = TextTrimming.CharacterEllipsis },
        };
        Canvas.SetLeft(c, Prop(o, "Left") is { Kind: ValueKind.Number } l ? l.AsNumber : 0);
        Canvas.SetTop(c, Prop(o, "Top") is { Kind: ValueKind.Number } t ? t.AsNumber : 0);
        return c;
    }

    private Control CreateDesignGrid(VfpObject o)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var columns = o.Members.Where(m => m.Class.BaseClass == "Column").ToList();
        foreach (var col in columns)
        {
            var h = col.Members.FirstOrDefault(m => m.Class.BaseClass == "Header");
            var caption = h != null && Prop(h, "Caption") is { Kind: ValueKind.Character } cap ? cap.AsString : col.Name;
            header.Children.Add(new Border
            {
                Width = Prop(col, "Width") is { Kind: ValueKind.Number } w ? w.AsNumber : 75,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock { Text = caption, Margin = new Thickness(4, 2), FontSize = 12 },
            });
        }
        if (columns.Count == 0)
            header.Children.Add(new TextBlock { Text = Prop(o, "RecordSource") is { Kind: ValueKind.Character } rs && rs.AsString.Trim().Length > 0 ? $"Grid ({rs.AsString.Trim()})" : "Grid", Margin = new Thickness(4, 2), Opacity = 0.7 });
        return new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = new DockPanel { Children = { header } } };
    }

    // ================================================================================
    // Windows
    // ================================================================================

    private Window BuildWindow(VfpObject form)
    {
        var canvas = new Canvas();
        var w = new Window
        {
            Content = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            SizeToContent = SizeToContent.Manual,
        };
        w.Tag = form;
        foreach (var p in new[] { "Caption", "Width", "Height", "Left", "Top", "AutoCenter", "BorderStyle", "BackColor", "Closable", "MaxButton", "MinButton", "TitleBar", "AlwaysOnTop", "WindowState" })
            ApplyWindow(form, w, p);
        AddMembers(form, canvas);
        w.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, e) =>
        {
            if (_rt.KeyLabels.Count == 0 || KeyLabel(e) is not { } label || !_rt.KeyLabels.ContainsKey(label)) return;
            e.Handled = true;
            Guard(() => _rt.RunKeyLabel(label));
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        w.Activated += (_, _) => Guard(() => _rt.Raise(form, "Activate"));
        w.Deactivated += (_, _) => Guard(() => _rt.Raise(form, "Deactivate"));
        w.Closing += (_, e) =>
        {
            if (_closing.Contains(form) || form.Released) return;
            e.Cancel = true;
            Guard(() =>
            {
                _rt.Raise(form, "QueryUnload");
                if (!_rt.LastNoDefault) _rt.Release(form);
            });
        };
        w.SizeChanged += (_, e) =>
        {
            _updating++;
            try
            {
                form.Set("Width", Value.Number(Math.Round(e.NewSize.Width)));
                form.Set("Height", Value.Number(Math.Round(e.NewSize.Height)));
            }
            finally { _updating--; }
            if (e.PreviousSize.Width > 0) Guard(() => _rt.Raise(form, "Resize"));
        };
        form.Native = w;
        return w;
    }

    /// <summary>
    /// A toolbar: its controls side by side in z-order (Left/Top are ignored, separators add a divider) in a small
    /// tool window. Closing it hides it, as in VFP.
    /// </summary>
    private Window BuildToolbarWindow(VfpObject toolbar)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(3) };
        foreach (var m in toolbar.Members.ToList())
        {
            var c = Create(m);
            if (c == null) continue;
            c.VerticalAlignment = VerticalAlignment.Center;
            strip.Children.Add(c);
        }
        var w = new Window
        {
            Title = Prop(toolbar, "Caption") is { Kind: ValueKind.Character } cap ? cap.AsString : "",
            Content = strip,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            Tag = toolbar,
        };
        w.Closing += (_, e) =>
        {
            if (_closing.Contains(toolbar) || toolbar.Released) return;
            e.Cancel = true;
            toolbar.Set("Visible", Value.False);
            w.Hide();
        };
        toolbar.Native = w;
        if (Owner != null) w.Topmost = Owner.Topmost;
        return w;
    }

    private void ApplyWindow(VfpObject form, Window w, string prop)
    {
        Value P(string n) => form.FindProperty(n)?.Value ?? Value.Null;
        switch (prop.ToUpperInvariant())
        {
            case "CAPTION": w.Title = P("Caption").Kind == ValueKind.Character ? P("Caption").AsString : ""; break;
            case "WIDTH": w.Width = Math.Max(50, P("Width").AsNumber); break;
            case "HEIGHT": w.Height = Math.Max(30, P("Height").AsNumber); break;
            case "LEFT" or "TOP":
                if (!ValueText.IsTrue(P("AutoCenter"))) w.Position = new PixelPoint((int)P("Left").AsNumber, (int)P("Top").AsNumber);
                break;
            case "AUTOCENTER":
                w.WindowStartupLocation = ValueText.IsTrue(P("AutoCenter")) ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
                break;
            case "BORDERSTYLE": w.CanResize = P("BorderStyle").Kind != ValueKind.Number || P("BorderStyle").AsNumber is 0 or 3; break;
            case "BACKCOLOR":
                if (IsCustomColor(P("BackColor"))) w.Background = new SolidColorBrush(ValueText.ToColor(P("BackColor")));
                break;
            case "MAXBUTTON": w.CanMaximize = ValueText.IsTrue(P("MaxButton")); break;
            case "MINBUTTON": w.CanMinimize = ValueText.IsTrue(P("MinButton")); break;
            case "ALWAYSONTOP": w.Topmost = ValueText.IsTrue(P("AlwaysOnTop")); break;
            case "WINDOWSTATE":
                w.WindowState = P("WindowState").AsNumber switch { 1 => WindowState.Minimized, 2 => WindowState.Maximized, _ => WindowState.Normal };
                break;
            case "VISIBLE":
                if (ValueText.IsTrue(P("Visible"))) { if (!w.IsVisible) w.Show(); }
                else w.Hide();
                break;
        }
    }

    // ================================================================================
    // Controls
    // ================================================================================

    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase) { "Form", "Container", "Control", "Page" };

    private void AddMembers(VfpObject parent, Panel panel)
    {
        foreach (var m in parent.Members.ToList())
        {
            var control = Create(m);
            if (control != null) panel.Children.Add(control);
        }
    }

    private Control? Create(VfpObject o)
    {
        var bc = o.Class.BaseClass;
        if (_designMode && (bc == "Timer" || (!BaseClasses.IsVisual(bc) && bc is not ("DataEnvironment" or "Cursor" or "Relation"))))
        {
            var placeholder = CreateDesignPlaceholder(o);
            placeholder.Tag = o;
            o.Native = placeholder;
            return placeholder;
        }
        if (bc == "Timer")
        {
            var t = new DispatcherTimer();
            t.Tick += (_, _) => Guard(() => _rt.Raise(o, "Timer"));
            o.Native = t;
            ApplyTimer(o, t);
            return null;
        }
        if (!BaseClasses.IsVisual(bc)) return null;
        Control c = bc switch
        {
            "Label" => new TextBlock { VerticalAlignment = VerticalAlignment.Top },
            "TextBox" => CreateTextBox(o, multiline: false),
            "EditBox" => CreateTextBox(o, multiline: true),
            "CommandButton" => CreateButton(o, o),
            "CheckBox" => CreateCheckBox(o),
            "OptionGroup" => CreateOptionGroup(o),
            "CommandGroup" => CreateCommandGroup(o),
            "ComboBox" => CreateList(o, combo: true),
            "ListBox" => CreateList(o, combo: false),
            "Spinner" => CreateSpinner(o),
            "Shape" => new Border(),
            "Line" => new Line { Stroke = Brushes.Gray, StrokeThickness = 1 },
            "Image" => new Image { Stretch = Stretch.Uniform },
            "PageFrame" => CreatePageFrame(o),
            "Grid" => _designMode ? CreateDesignGrid(o) : CreateGridControl(o),
            "Container" or "Control" => new Border { Child = new Canvas() },
            "Separator" => new Border { BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = Brushes.Gray, Margin = new Thickness(4, 3) },
            _ => new TextBlock { Text = $"({bc})", Opacity = 0.6 },
        };
        o.Native = c;
        c.Name = o.Name;
        c.Tag = o;
        foreach (var p in new[] { "LEFT", "TOP", "WIDTH", "HEIGHT", "VISIBLE", "ENABLED", "TOOLTIPTEXT", "FONTNAME", "FONTSIZE", "FONTBOLD", "FONTITALIC",
                     "FORECOLOR", "BACKCOLOR", "CAPTION", "READONLY", "ALIGNMENT", "PICTURE", "CURVATURE", "BORDERWIDTH", "BORDERCOLOR", "FILLCOLOR",
                     "BACKSTYLE", "LINESLANT", "ROWSOURCE", "INCREMENT", "SPINNERHIGHVALUE", "SPINNERLOWVALUE", "WORDWRAP", "PASSWORDCHAR", "MAXLENGTH", "INPUTMASK" })
            Apply(o, c, p);
        if (c is Border { Child: Canvas inner } && bc is "Container" or "Control") AddMembers(o, inner);
        if (bc == "Separator") { c.Width = 1; c.Height = double.NaN; c.IsVisible = o.Parent?.Class.BaseClass == "Toolbar"; }
        ShowValue(o, c);
        if (_designMode) return c; // no event code runs in the designer
        if (c is Avalonia.Input.InputElement ie && bc is not ("Label" or "Shape" or "Line" or "Image" or "Container" or "Control" or "PageFrame"))
        {
            ie.GotFocus += (_, _) => Guard(() =>
            {
                // When returning .F. refuses the focus: it moves on to the next control, as in VFP.
                var when = _rt.Raise(o, "When");
                if (when.Kind == ValueKind.Logical && !when.AsBool || when.Kind == ValueKind.Number && when.AsNumber == 0)
                {
                    if (_refusing < 20)
                    {
                        _refusing++;
                        try { TopLevel.GetTopLevel(c)?.FocusManager?.TryMoveFocus(Avalonia.Input.NavigationDirection.Next); }
                        finally { _refusing--; }
                    }
                    return;
                }
                _rt.Raise(o, "GotFocus");
            });
        }
        c.DoubleTapped += (_, _) => Guard(() => _rt.Raise(o, "DblClick"));
        if (bc is "Label" or "Image" or "Shape" or "Container")
            c.PointerReleased += (_, _) => Guard(() => _rt.Raise(o, "Click"));
        return c;
    }

    private static Value Prop(VfpObject o, string name) => o.FindProperty(name)?.Value ?? Value.Null;

    private static string? ControlSource(VfpObject o) =>
        Prop(o, "ControlSource") is { Kind: ValueKind.Character } cs && cs.AsString.Trim().Length > 0 ? cs.AsString.Trim() : null;

    private static bool IsCustomColor(Value v) => v.Kind == ValueKind.Number && v.AsNumber is not (15790320 or 16777215 or 0);

    private void Apply(VfpObject o, Control c, string prop)
    {
        var v = Prop(o, prop);
        switch (prop)
        {
            case "LEFT": Canvas.SetLeft(c, v.Kind == ValueKind.Number ? v.AsNumber : 0); break;
            case "TOP": Canvas.SetTop(c, v.Kind == ValueKind.Number ? v.AsNumber : 0); break;
            case "WIDTH":
                if (v.Kind == ValueKind.Number && !(c is TextBlock && ValueText.IsTrue(Prop(o, "AutoSize")))) c.Width = Math.Max(0, v.AsNumber);
                break;
            case "HEIGHT":
                if (v.Kind == ValueKind.Number && !(c is TextBlock && ValueText.IsTrue(Prop(o, "AutoSize")))) c.Height = Math.Max(0, v.AsNumber);
                break;
            case "VISIBLE": c.IsVisible = v.Kind != ValueKind.Logical || v.AsBool; break;
            case "ENABLED": c.IsEnabled = v.Kind != ValueKind.Logical || v.AsBool; break;
            case "TOOLTIPTEXT": ToolTip.SetTip(c, v.Kind == ValueKind.Character && v.AsString.Length > 0 ? v.AsString : null); break;
            case "FONTNAME":
                if (v.Kind == ValueKind.Character && v.AsString.Length > 0 && v.AsString != "Segoe UI") SetFont(c, family: v.AsString);
                break;
            case "FONTSIZE": if (v.Kind == ValueKind.Number && v.AsNumber > 0 && v.AsNumber != 9) SetFont(c, size: v.AsNumber * 96 / 72); break;
            case "FONTBOLD": if (v.Kind == ValueKind.Logical) SetFont(c, bold: v.AsBool); break;
            case "FONTITALIC": if (v.Kind == ValueKind.Logical) SetFont(c, italic: v.AsBool); break;
            case "FORECOLOR":
                if (IsCustomColor(v))
                {
                    var brush = new SolidColorBrush(ValueText.ToColor(v));
                    if (c is TextBlock tb) tb.Foreground = brush;
                    else if (c is TemplatedControl tc) tc.Foreground = brush;
                }
                break;
            case "BACKCOLOR":
                if (IsCustomColor(v) && !(o.Class.BaseClass == "Label" && Prop(o, "BackStyle") is { Kind: ValueKind.Number } bs && bs.AsNumber == 0))
                {
                    var brush = new SolidColorBrush(ValueText.ToColor(v));
                    if (c is TextBlock tb) tb.Background = brush;
                    else if (c is TemplatedControl tc) tc.Background = brush;
                    else if (c is Border b && o.Class.BaseClass != "Shape") b.Background = brush;
                }
                break;
            case "CAPTION":
                switch (c)
                {
                    case TextBlock tb when o.Class.BaseClass == "Label": tb.Text = v.Kind == ValueKind.Character ? v.AsString : ""; break;
                    case ContentControl cc when c is Button or CheckBox: cc.Content = ValueText.Caption(v); break;
                }
                break;
            case "WORDWRAP":
                if (c is TextBlock wtb) wtb.TextWrapping = ValueText.IsTrue(v) ? TextWrapping.Wrap : TextWrapping.NoWrap;
                break;
            case "READONLY":
                if (c is TextBox rtb) rtb.IsReadOnly = ValueText.IsTrue(v);
                else if (c is DataGrid dg) dg.IsReadOnly = ValueText.IsTrue(v);
                break;
            case "PASSWORDCHAR":
                if (c is TextBox ptb && v.Kind == ValueKind.Character && v.AsString.Length > 0) ptb.PasswordChar = v.AsString[0];
                break;
            case "MAXLENGTH":
                if (c is TextBox mtb && v.Kind == ValueKind.Number && v.AsNumber > 0) mtb.MaxLength = (int)v.AsNumber;
                break;
            case "INPUTMASK" or "FORMAT":
                if (c is TextBox) ShowValue(o, c);
                break;
            case "ALIGNMENT":
                if (v.Kind != ValueKind.Number) break;
                var ta = v.AsNumber switch { 1 => TextAlignment.Right, 2 => TextAlignment.Center, _ => TextAlignment.Left };
                if (c is TextBlock atb) atb.TextAlignment = ta;
                else if (c is TextBox atx && v.AsNumber != 3) atx.TextAlignment = ta;
                break;
            case "PICTURE":
                if (c is Image img && v.Kind == ValueKind.Character && v.AsString.Length > 0)
                {
                    var path = ResolveFile(o, v.AsString);
                    if (path != null) { try { img.Source = new Bitmap(path); } catch (Exception) { } }
                }
                break;
            case "CURVATURE":
                if (c is Border sh && o.Class.BaseClass == "Shape" && v.Kind == ValueKind.Number)
                    sh.CornerRadius = new CornerRadius(Math.Min(99, v.AsNumber) / 99.0 * Math.Min(sh.Width is > 0 ? sh.Width : 100, sh.Height is > 0 ? sh.Height : 100) / 2);
                break;
            case "BORDERWIDTH":
                if (c is Border bw && v.Kind == ValueKind.Number) bw.BorderThickness = new Thickness(v.AsNumber);
                else if (c is Line ln && v.Kind == ValueKind.Number) ln.StrokeThickness = Math.Max(1, v.AsNumber);
                break;
            case "BORDERCOLOR":
            {
                var brush = new SolidColorBrush(v.Kind == ValueKind.Number ? ValueText.ToColor(v) : Colors.Gray);
                if (c is Border bc2) bc2.BorderBrush = brush;
                else if (c is Line ln2) ln2.Stroke = brush;
                break;
            }
            case "FILLCOLOR":
                if (c is Border fb && o.Class.BaseClass == "Shape" && v.Kind == ValueKind.Number && Prop(o, "FillStyle") is { Kind: ValueKind.Number } fs && fs.AsNumber == 0)
                    fb.Background = new SolidColorBrush(ValueText.ToColor(v));
                break;
            case "LINESLANT":
                if (c is Line line)
                {
                    var w = Prop(o, "Width").AsNumber;
                    var h = Prop(o, "Height").AsNumber;
                    var up = v.Kind == ValueKind.Character && v.AsString == "/";
                    line.StartPoint = new Point(0, up ? h : 0);
                    line.EndPoint = new Point(w, up ? 0 : h);
                }
                break;
            case "ROWSOURCE" or "ROWSOURCETYPE":
                if (c is SelectingItemsControl list && o.Class.BaseClass is "ListBox" or "ComboBox") LoadListItems(o, list);
                break;
            case "INCREMENT":
                if (c is NumericUpDown nud && v.Kind == ValueKind.Number) nud.Increment = (decimal)v.AsNumber;
                break;
            case "SPINNERHIGHVALUE":
                if (c is NumericUpDown hi && v.Kind == ValueKind.Number) hi.Maximum = (decimal)Math.Min(v.AsNumber, 7.9e27);
                break;
            case "SPINNERLOWVALUE":
                if (c is NumericUpDown lo && v.Kind == ValueKind.Number) lo.Minimum = (decimal)Math.Max(v.AsNumber, -7.9e27);
                break;
            case "VALUE":
                ShowValue(o, c);
                if (_rt.Handles(o, "ProgrammaticChange")) Guard(() => _rt.Raise(o, "ProgrammaticChange"));
                break;
            case "ACTIVEPAGE":
                if (c is TabControl tabs && v.Kind == ValueKind.Number) tabs.SelectedIndex = (int)v.AsNumber - 1;
                break;
            case "RECORDSOURCE" or "COLUMNCOUNT":
                if (c is DataGrid grid) ReloadGrid(o, grid);
                break;
            case "PAGECOUNT" or "BUTTONCOUNT":
                RebuildChildren(o, c);
                break;
        }
    }

    private static void SetFont(Control c, string? family = null, double? size = null, bool? bold = null, bool? italic = null)
    {
        void Set(Action<FontFamily?, double?, FontWeight?, FontStyle?> apply) =>
            apply(family != null ? new FontFamily(family) : null, size, bold.HasValue ? (bold.Value ? FontWeight.Bold : FontWeight.Normal) : null,
                italic.HasValue ? (italic.Value ? FontStyle.Italic : FontStyle.Normal) : null);
        if (c is TextBlock tb)
            Set((f, s, w, st) => { if (f != null) tb.FontFamily = f; if (s != null) tb.FontSize = s.Value; if (w != null) tb.FontWeight = w.Value; if (st != null) tb.FontStyle = st.Value; });
        else if (c is TemplatedControl tc)
            Set((f, s, w, st) => { if (f != null) tc.FontFamily = f; if (s != null) tc.FontSize = s.Value; if (w != null) tc.FontWeight = w.Value; if (st != null) tc.FontStyle = st.Value; });
    }

    private string? ResolveFile(VfpObject o, string file)
    {
        if (File.Exists(file)) return file;
        var dirs = new List<string> { _rt.Options.Default_ };
        if (o.Class.Unit?.File is { } f) dirs.Insert(0, Path.GetDirectoryName(f)!);
        foreach (var d in dirs)
        {
            var p = DataSession.FindIgnoringCase(Path.Combine(d, file.Replace('\\', Path.DirectorySeparatorChar)));
            if (p != null) return p;
        }
        return null;
    }

    private void ApplyTimer(VfpObject o, DispatcherTimer t)
    {
        var interval = Prop(o, "Interval") is { Kind: ValueKind.Number } i ? i.AsNumber : 0;
        var enabled = Prop(o, "Enabled") is { Kind: ValueKind.Logical } e ? e.AsBool : true;
        t.Stop();
        if (interval > 0 && enabled && !o.Released)
        {
            t.Interval = TimeSpan.FromMilliseconds(interval);
            t.Start();
        }
    }

    // ---- Values ------------------------------------------------------------------------

    /// <summary>Displays the object's Value in its control without raising change events.</summary>
    private void ShowValue(VfpObject o, Control c)
    {
        _updating++;
        try
        {
            var v = Prop(o, "Value");
            switch (c)
            {
                case TextBox tb:
                    var text = v.Kind == ValueKind.Character || v.Kind == ValueKind.Null ? (v.IsNull ? "" : v.AsString.TrimEnd(' ')) : ValueText.ToText(v, _rt.Options);
                    if (MaskOf(o) is { } mi && !v.IsNull)
                        text = !tb.IsFocused ? mi.Display(v, _rt.Options)
                            : v.Kind == ValueKind.Character ? mi.EditText(v.AsString.TrimEnd(' '))
                            : v.Kind is ValueKind.Number or ValueKind.Currency ? Library.TransformDefault(v, _rt.Options).Trim() : text;
                    if (tb.Text != text) tb.Text = text;
                    break;
                case CheckBox cb:
                    cb.IsChecked = ValueText.IsTrue(v);
                    break;
                case NumericUpDown nud:
                    nud.Value = v.Kind is ValueKind.Number or ValueKind.Currency ? (decimal)v.AsNumber : 0;
                    break;
                case SelectingItemsControl list when o.Class.BaseClass is "ListBox" or "ComboBox":
                    if (v.Kind == ValueKind.Number) list.SelectedIndex = (int)v.AsNumber - 1;
                    else if (v.Kind == ValueKind.Character)
                    {
                        var s = v.AsString.TrimEnd();
                        list.SelectedIndex = list.Items.Cast<object?>().Select(x => x?.ToString()?.TrimEnd()).ToList().IndexOf(s);
                    }
                    break;
                case Border { Child: Canvas panel } when o.Class.BaseClass == "OptionGroup":
                    foreach (var rb in panel.Children.OfType<RadioButton>())
                    {
                        var opt = (VfpObject)rb.Tag!;
                        var idx = OptionIndex(o, opt);
                        rb.IsChecked = v.Kind == ValueKind.Number ? idx == (int)v.AsNumber
                            : v.Kind == ValueKind.Character && Prop(opt, "Caption") is { Kind: ValueKind.Character } cap && cap.AsString.Replace("\\<", "") == v.AsString.TrimEnd();
                        opt.Set("Value", Value.Number(rb.IsChecked == true ? 1 : 0));
                    }
                    break;
            }
        }
        finally { _updating--; }
    }

    /// <summary>The InputMask/Format of a text box or spinner, or null when it has neither.</summary>
    private static MaskedInput? MaskOf(VfpObject o)
    {
        var format = Prop(o, "Format") is { Kind: ValueKind.Character } f ? f.AsString : "";
        var mask = Prop(o, "InputMask") is { Kind: ValueKind.Character } m ? m.AsString : "";
        if (format.Length == 0 && mask.Length == 0) return null;
        var mi = new MaskedInput(format, mask);
        return mi.IsEmpty ? null : mi;
    }

    private void UserChangedValue(VfpObject o, Value newValue, bool writeNow)
    {
        o.Set("Value", newValue);
        Guard(() =>
        {
            _rt.Raise(o, "InteractiveChange");
            if (writeNow) WriteBack(o);
        });
    }

    private void WriteBack(VfpObject o)
    {
        var cs = ControlSource(o);
        if (cs == null) return;
        _rt.WriteControlSource(o, cs, Prop(o, "Value"));
    }

    private char FieldTypeOf(VfpObject o)
    {
        var cs = ControlSource(o);
        if (cs == null) return '\0';
        try
        {
            var session = _rt.SessionFor(o);
            var e = Parser.ParseExpression(cs);
            (WorkArea? wa, string? field) = e switch
            {
                NameExpr n => (session.Current, n.Name),
                MemberExpr { Target: NameExpr a } m => (session.FindAlias(a.Name), m.Name),
                AliasFieldExpr af => (session.FindAlias(af.Alias), af.Field),
                _ => (null, null),
            };
            if (wa is { InUse: true } && field != null && wa.FieldIndex(field) is var i and >= 0) return wa.Table.Fields[i].Type;
        }
        catch (VfpException) { }
        return '\0';
    }

    // ---- Control builders -------------------------------------------------------------

    private Control CreateTextBox(VfpObject o, bool multiline)
    {
        var tb = new TextBox { Padding = new Thickness(3, 1), MinHeight = 0 };
        if (multiline)
        {
            tb.AcceptsReturn = true;
            tb.TextWrapping = TextWrapping.Wrap;
        }
        // TextChanged may be raised asynchronously; the Text property change is synchronous.
        tb.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _updating > 0) return;
            var like = Prop(o, "Value");
            var typed = tb.Text ?? "";
            var mi = MaskOf(o);
            if (mi != null && !multiline)
            {
                // Apply the mask as the user types: drop characters that do not fit, add literal characters.
                var fixedText = like.Kind is ValueKind.Number or ValueKind.Currency ? MaskedInput.FilterNumber(typed, _rt.Options.Point)
                    : like.Kind is ValueKind.Character or ValueKind.Null ? mi.ApplyCharacterMask(typed) : typed;
                if (fixedText != typed)
                {
                    _updating++;
                    try { tb.Text = fixedText; tb.CaretIndex = fixedText.Length; }
                    finally { _updating--; }
                    typed = fixedText;
                }
                if (like.Kind is ValueKind.Character or ValueKind.Null)
                {
                    UserChangedValue(o, Value.String(mi.StoredText(typed)), writeNow: false);
                    return;
                }
            }
            if (ValueText.TryParse(typed, like, _rt.Options, out var v, FieldTypeOf(o))) UserChangedValue(o, v, writeNow: false);
        };
        tb.GotFocus += (_, _) =>
        {
            if (MaskOf(o) is not { } mi) return;
            ShowValue(o, tb); // the edit form of the value (no display formatting)
            if (mi.SelectOnEntry) tb.SelectAll();
        };
        tb.LostFocus += (_, _) => Guard(() =>
        {
            var valid = _rt.Raise(o, "Valid");
            if (valid.Kind == ValueKind.Logical && !valid.AsBool || valid.Kind == ValueKind.Number && valid.AsNumber == 0)
            {
                Dispatcher.UIThread.Post(() => tb.Focus());
                return;
            }
            try { WriteBack(o); }
            catch (VfpException)
            {
                Dispatcher.UIThread.Post(() => tb.Focus());
                throw;
            }
            if (MaskOf(o) != null) ShowValue(o, tb); // back to the display format
            _rt.Raise(o, "LostFocus");
        });
        return tb;
    }

    private Button CreateButton(VfpObject o, VfpObject clickTarget)
    {
        var b = new Button { Padding = new Thickness(4, 2), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        if (ValueText.IsTrue(Prop(o, "Default"))) b.IsDefault = true;
        if (ValueText.IsTrue(Prop(o, "Cancel"))) b.IsCancel = true;
        b.Click += (_, _) => Guard(() => _rt.Raise(clickTarget, "Click"));
        return b;
    }

    private Control CreateCheckBox(VfpObject o)
    {
        var cb = new CheckBox();
        cb.IsCheckedChanged += (_, _) =>
        {
            if (_updating > 0) return;
            var old = Prop(o, "Value");
            var on = cb.IsChecked == true;
            UserChangedValue(o, old.Kind == ValueKind.Logical ? Value.Logical(on) : Value.Number(on ? 1 : 0), writeNow: true);
            Guard(() => _rt.Raise(o, "Click"));
        };
        return cb;
    }

    private static int OptionIndex(VfpObject group, VfpObject option) =>
        group.Members.Where(m => m.Class.BaseClass == "OptionButton").ToList().IndexOf(option) + 1;

    private Control CreateOptionGroup(VfpObject o)
    {
        var panel = new Canvas();
        var border = new Border { Child = panel };
        BuildOptions(o, panel);
        return border;
    }

    private void BuildOptions(VfpObject o, Canvas panel)
    {
        panel.Children.Clear();
        var group = "opt" + Interlocked.Increment(ref _groupSeq);
        foreach (var opt in o.Members.Where(m => m.Class.BaseClass == "OptionButton"))
        {
            var rb = new RadioButton { GroupName = group, Content = ValueText.Caption(Prop(opt, "Caption")), Tag = opt, Name = opt.Name };
            opt.Native = rb;
            foreach (var p in new[] { "LEFT", "TOP", "WIDTH", "HEIGHT", "VISIBLE", "ENABLED", "FORECOLOR", "FONTBOLD" }) Apply(opt, rb, p);
            rb.IsCheckedChanged += (_, _) =>
            {
                if (_updating > 0 || rb.IsChecked != true) return;
                var old = Prop(o, "Value");
                var newValue = old.Kind == ValueKind.Character ? Value.String(Prop(opt, "Caption").AsString.Replace("\\<", "")) : Value.Number(OptionIndex(o, opt));
                foreach (var other in o.Members.Where(m => m.Class.BaseClass == "OptionButton")) other.Set("Value", Value.Number(other == opt ? 1 : 0));
                UserChangedValue(o, newValue, writeNow: true);
                Guard(() =>
                {
                    if (_rt.Handles(opt, "Click")) _rt.Raise(opt, "Click");
                    _rt.Raise(o, "Click");
                });
            };
            panel.Children.Add(rb);
        }
    }

    private Control CreateCommandGroup(VfpObject o)
    {
        var panel = new Canvas();
        BuildCommands(o, panel);
        return new Border { Child = panel };
    }

    private void BuildCommands(VfpObject o, Canvas panel)
    {
        panel.Children.Clear();
        int i = 0;
        foreach (var cmd in o.Members.Where(m => m.Class.BaseClass == "CommandButton").ToList())
        {
            int index = ++i;
            var b = new Button { Content = ValueText.Caption(Prop(cmd, "Caption")), Tag = cmd, Name = cmd.Name, HorizontalContentAlignment = HorizontalAlignment.Center };
            cmd.Native = b;
            foreach (var p in new[] { "LEFT", "TOP", "WIDTH", "HEIGHT", "VISIBLE", "ENABLED", "FORECOLOR", "FONTBOLD" }) Apply(cmd, b, p);
            b.Click += (_, _) =>
            {
                o.Set("Value", Value.Number(index));
                Guard(() =>
                {
                    WriteBack(o);
                    if (_rt.Handles(cmd, "Click")) _rt.Raise(cmd, "Click");
                    else _rt.Raise(o, "Click");
                });
            };
            panel.Children.Add(b);
        }
    }

    private void RebuildChildren(VfpObject o, Control c)
    {
        switch (c)
        {
            case Border { Child: Canvas p } when o.Class.BaseClass == "OptionGroup": BuildOptions(o, p); ShowValue(o, c); break;
            case Border { Child: Canvas p2 } when o.Class.BaseClass == "CommandGroup": BuildCommands(o, p2); break;
            case TabControl tabs: BuildPages(o, tabs); break;
        }
    }

    private Control CreateList(VfpObject o, bool combo)
    {
        SelectingItemsControl list = combo ? new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch } : new ListBox();
        list.SelectionChanged += (_, _) =>
        {
            if (_updating > 0 || list.SelectedIndex < 0) return;
            var old = Prop(o, "Value");
            var text = list.SelectedItem?.ToString() ?? "";
            o.Set("ListIndex", Value.Number(list.SelectedIndex + 1));
            UserChangedValue(o, old.Kind == ValueKind.Number ? Value.Number(list.SelectedIndex + 1) : Value.String(text), writeNow: true);
            Guard(() => _rt.Raise(o, "Click"));
        };
        return list;
    }

    /// <summary>Fills a ListBox/ComboBox from RowSourceType 0 (AddItem), 1 (values), 2 (alias), 3 (SQL), 5 (array) or 6 (fields).</summary>
    private void LoadListItems(VfpObject o, SelectingItemsControl list)
    {
        var items = new List<string>();
        var type = Prop(o, "RowSourceType") is { Kind: ValueKind.Number } t ? (int)t.AsNumber : 0;
        var source = Prop(o, "RowSource") is { Kind: ValueKind.Character } s ? s.AsString.Trim() : "";
        try
        {
            switch (type)
            {
                case 0: items.AddRange(o.ListItems ?? []); break;
                case 1: items.AddRange(source.Split(',').Select(x => x.Trim())); break;
                case 2 or 6 when source.Length > 0:
                {
                    var session = _rt.SessionFor(o);
                    var alias = type == 2 ? source : source.Split('.', ',')[0];
                    var field = type == 6 && source.Contains('.') ? source.Split('.')[1].Split(',')[0].Trim() : null;
                    var wa = session.FindAlias(alias);
                    if (wa == null) break;
                    var fi = field != null ? wa.FieldIndex(field) : 0;
                    var saved = wa.Eof ? 0 : wa.RecNo;
                    wa.GoTop();
                    while (!wa.Eof) { items.Add(ValueText.ToText(wa.Get(Math.Max(0, fi)), session.Options)); wa.Skip(); }
                    if (saved > 0) wa.Go(saved);
                    break;
                }
                case 3 when source.Length > 0:
                {
                    _rt.InObjectContext(o, () => { _rt.ExecuteCommand(source); return true; });
                    var cur = _rt.SessionFor(o).Current;
                    if (cur.InUse)
                    {
                        cur.GoTop();
                        while (!cur.Eof) { items.Add(ValueText.ToText(cur.Get(0), _rt.Options)); cur.Skip(); }
                    }
                    break;
                }
                case 5 when source.Length > 0:
                {
                    var arr = _rt.InObjectContext(o, () => _rt.FindVariable(source)?.Array);
                    if (arr != null)
                        for (int r = 1; r <= (arr.TwoDimensional ? arr.Rows : arr.Length); r++)
                            items.Add(ValueText.ToText(arr.TwoDimensional ? arr[r, 1] : arr[r], _rt.Options));
                    break;
                }
            }
        }
        catch (VfpException ex) { Error?.Invoke(ex); }
        _updating++;
        try
        {
            list.ItemsSource = items;
            o.Set("ListCount", Value.Number(items.Count));
        }
        finally { _updating--; }
        ShowValue(o, list);
    }

    private Control CreateSpinner(VfpObject o)
    {
        var nud = new NumericUpDown { FormatString = "0.##", Padding = new Thickness(2, 0) };
        nud.ValueChanged += (_, e) =>
        {
            if (_updating > 0) return;
            UserChangedValue(o, Value.Number((double)(e.NewValue ?? 0), Prop(o, "Value").Decimals), writeNow: false);
        };
        nud.LostFocus += (_, _) => Guard(() =>
        {
            var valid = _rt.Raise(o, "Valid");
            if (valid.Kind == ValueKind.Logical && !valid.AsBool) { Dispatcher.UIThread.Post(() => nud.Focus()); return; }
            WriteBack(o);
            _rt.Raise(o, "LostFocus");
        });
        return nud;
    }

    private Control CreatePageFrame(VfpObject o)
    {
        var tabs = new TabControl { Padding = new Thickness(0) };
        BuildPages(o, tabs);
        tabs.SelectionChanged += (_, _) =>
        {
            if (_updating > 0 || tabs.SelectedIndex < 0) return;
            o.Set("ActivePage", Value.Number(tabs.SelectedIndex + 1));
            if (tabs.SelectedItem is TabItem { Tag: VfpObject page }) Guard(() => _rt.Raise(page, "Activate"));
        };
        return tabs;
    }

    private void BuildPages(VfpObject o, TabControl tabs)
    {
        _updating++;
        try
        {
            tabs.Items.Clear();
            var pages = o.Members.Where(m => m.Class.BaseClass == "Page")
                .OrderBy(p => Prop(p, "PageOrder") is { Kind: ValueKind.Number } n ? n.AsNumber : 0).ToList();
            foreach (var page in pages)
            {
                var canvas = new Canvas { Name = page.Name, Tag = page };
                page.Native = canvas;
                AddMembers(page, canvas);
                tabs.Items.Add(new TabItem
                {
                    Header = Prop(page, "Caption").Kind == ValueKind.Character ? Prop(page, "Caption").AsString.Replace("\\<", "") : page.Name,
                    Content = canvas,
                    Tag = page,
                    FontSize = 13,
                    MinHeight = 28,
                    Padding = new Thickness(10, 2),
                });
            }
            var active = Prop(o, "ActivePage") is { Kind: ValueKind.Number } a && a.AsNumber >= 1 ? (int)a.AsNumber : 1;
            tabs.SelectedIndex = Math.Min(active, pages.Count) - 1;
        }
        finally { _updating--; }
    }

    // ---- Grid ---------------------------------------------------------------------------

    private Control CreateGridControl(VfpObject o)
    {
        var grid = new DataGrid();
        ReloadGrid(o, grid);
        return grid;
    }

    private void ReloadGrid(VfpObject o, DataGrid grid)
    {
        var session = _rt.SessionFor(o);
        var source = Prop(o, "RecordSource") is { Kind: ValueKind.Character } rs ? rs.AsString.Trim() : "";
        var wa = source.Length > 0 ? session.FindAlias(source) : session.Current;
        if (wa is not { InUse: true })
        {
            grid.ItemsSource = null;
            return;
        }
        List<string>? fields = null;
        var columns = o.Members.Where(m => m.Class.BaseClass == "Column").ToList();
        if (Prop(o, "ColumnCount") is { Kind: ValueKind.Number } cc && cc.AsNumber >= 0 && columns.Count > 0)
            fields = columns.Select(c => Prop(c, "ControlSource") is { Kind: ValueKind.Character } s ? s.AsString : "").ToList();
        var model = new BrowseModel(_rt, wa, fields, session);
        model.RowStyle = DynamicStyles(o, columns, wa, session);
        model.Load();
        ConfigureGrid(grid, model, ValueText.IsTrue(Prop(o, "ReadOnly")));
        for (int i = 0; i < grid.Columns.Count && fields != null && i < columns.Count; i++)
        {
            var header = columns[i].Members.FirstOrDefault(m => m.Class.BaseClass == "Header");
            if (header != null && Prop(header, "Caption") is { Kind: ValueKind.Character } cap && cap.AsString != "Header1") grid.Columns[i].Header = cap.AsString;
            if (Prop(columns[i], "Width") is { Kind: ValueKind.Number } w) grid.Columns[i].Width = new DataGridLength(w.AsNumber);
        }
        grid.Tag = o;
    }

    /// <summary>
    /// The grid's Dynamic… column properties as a function evaluated on each row (the record pointer is on the row
    /// and the grid's table is the current work area), or null when no column has any.
    /// </summary>
    private Func<CellStyle?[]>? DynamicStyles(VfpObject grid, List<VfpObject> columns, WorkArea wa, DataSession session)
    {
        string? Expr(VfpObject col, string name) => Prop(col, name) is { Kind: ValueKind.Character } v && v.AsString.Trim().Length > 0 ? v.AsString.Trim() : null;
        var specs = columns.Select(c => (Col: c, Back: Expr(c, "DynamicBackColor"), Fore: Expr(c, "DynamicForeColor"), Bold: Expr(c, "DynamicFontBold"), Italic: Expr(c, "DynamicFontItalic"))).ToList();
        if (specs.All(x => x.Back == null && x.Fore == null && x.Bold == null && x.Italic == null)) return null;
        var reported = false;
        return () => _rt.InSession(session, () =>
        {
            var saved = session.CurrentAreaNumber;
            session.Select(wa.Number);
            try
            {
                return specs.Select(x =>
                {
                    if (x.Back == null && x.Fore == null && x.Bold == null && x.Italic == null) return null;
                    try
                    {
                        int? Color(string? e) => e != null && _rt.ReadControlSource(x.Col, e) is { Kind: ValueKind.Number } n ? (int)n.AsNumber : null;
                        bool? Flag(string? e) => e != null && _rt.ReadControlSource(x.Col, e) is { Kind: ValueKind.Logical } l ? l.AsBool : null;
                        return new CellStyle(Color(x.Back), Color(x.Fore), Flag(x.Bold), Flag(x.Italic));
                    }
                    catch (VfpException ex)
                    {
                        if (!reported) { reported = true; Error?.Invoke(ex); }
                        return (CellStyle?)null;
                    }
                }).ToArray();
            }
            finally { session.Select(saved); }
        });
    }

    /// <summary>Applies a row's dynamic styles to its cells (rows are recycled, so every cell is reset).</summary>
    private static void ApplyRowStyles(DataGrid grid, DataGridRow row)
    {
        if (row.DataContext is not BrowseRow data) return;
        for (int i = 1; i < grid.Columns.Count; i++)
        {
            if (grid.Columns[i].GetCellContent(row) is not TextBlock cell) continue;
            var style = data.Styles != null && i - 1 < data.Styles.Length ? data.Styles[i - 1] : null;
            IBrush? back = style?.BackColor is { } b ? new SolidColorBrush(ValueText.ToColor(Value.Number(b))) : null;
            if (cell.Parent is DataGridCell dc) dc.Background = back;
            else cell.Background = back;
            if (style?.ForeColor is { } f) cell.Foreground = new SolidColorBrush(ValueText.ToColor(Value.Number(f)));
            else cell.ClearValue(TextBlock.ForegroundProperty);
            cell.FontWeight = style?.Bold == true ? FontWeight.Bold : FontWeight.Normal;
            cell.FontStyle = style?.Italic == true ? FontStyle.Italic : FontStyle.Normal;
        }
    }

    /// <summary>Creates an editable grid over a Browse model (used by BROWSE and the Grid control).</summary>
    public DataGrid CreateGrid(BrowseModel model, bool readOnly)
    {
        var grid = new DataGrid();
        ConfigureGrid(grid, model, readOnly);
        return grid;
    }

    private void ConfigureGrid(DataGrid grid, BrowseModel model, bool readOnly)
    {
        grid.AutoGenerateColumns = false;
        grid.CanUserResizeColumns = true;
        grid.CanUserSortColumns = false;
        grid.GridLinesVisibility = DataGridGridLinesVisibility.All;
        grid.RowHeight = 26;
        grid.ColumnHeaderHeight = 28;
        grid.FontSize = 13;
        grid.IsReadOnly = readOnly || model.Area.ReadOnly;
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridTextColumn { Header = "", Binding = new Binding(nameof(BrowseRow.Mark)), IsReadOnly = true, Width = new DataGridLength(22) });
        for (int i = 0; i < model.Columns.Count; i++)
        {
            var col = model.Columns[i];
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = col.Header,
                Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay },
                IsReadOnly = col.ReadOnly,
            });
        }
        grid.ItemsSource = model.Rows;
        grid.Tag ??= model;
        if (model.RowStyle != null)
        {
            grid.LoadingRow -= OnLoadingRow;
            grid.LoadingRow += OnLoadingRow;
        }
        grid.CellEditEnded += (_, e) =>
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Row.DataContext is not BrowseRow row) return;
            var ci = grid.Columns.IndexOf(e.Column) - 1;
            if (ci < 0) return;
            var err = model.Commit(model.Rows.IndexOf(row), ci, row[ci]);
            if (err != null) Error?.Invoke(new VfpException(ErrorCodes.DataTypeMismatch, err));
        };
        grid.SelectionChanged += (_, _) => model.Select(grid.SelectedIndex);
        grid.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.T && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && grid.SelectedIndex >= 0)
            {
                model.ToggleDelete(grid.SelectedIndex);
                var idx = grid.SelectedIndex;
                var r = model.Rows[idx];
                model.Rows[idx] = new BrowseRow(r.RecNo, r.Deleted, r);
                grid.SelectedIndex = idx;
                e.Handled = true;
            }
        };
    }

    private static void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (sender is DataGrid grid) ApplyRowStyles(grid, e.Row);
    }

    // ================================================================================
    // Helpers
    // ================================================================================

    private static IEnumerable<VfpObject> Descendants(VfpObject o)
    {
        foreach (var m in o.Members)
        {
            yield return m;
            foreach (var d in Descendants(m)) yield return d;
        }
    }

    /// <summary>Runs FoxPro event code, reporting errors instead of crashing the UI thread.</summary>
    private void Guard(Action action)
    {
        try { action(); }
        catch (VfpException ex) { Error?.Invoke(ex); }
        catch (QuitException) { ClearEvents(); throw; }
    }

    private void Guard(Func<Value> action) => Guard(() => { action(); });

    /// <summary>Finds the object named <paramref name="path"/> (for example "pgfMain.Page1.txtName") inside a form.</summary>
    public static VfpObject? Find(VfpObject root, string path)
    {
        var o = root;
        foreach (var part in path.Split('.'))
        {
            o = o.FindProperty(part)?.Value is { Kind: ValueKind.Object } v ? (VfpObject)v.AsObject : null;
            if (o == null) return null;
        }
        return o;
    }
}
