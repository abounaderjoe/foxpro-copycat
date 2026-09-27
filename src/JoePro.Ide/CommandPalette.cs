using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace JoePro.Ide;

public sealed record PaletteAction(string Title, string Shortcut, Action Run)
{
    /// <summary>The title in the user's language (<see cref="Title"/> stays English: it identifies the action).</summary>
    public string DisplayTitle => Strings.T(Title);

    public override string ToString() => Shortcut.Length > 0 ? $"{DisplayTitle}    {Shortcut}" : DisplayTitle;
}

/// <summary>
/// Ctrl+Shift+P: search IDE actions by name. Text that matches no action runs as a FoxPro command,
/// so the palette doubles as a quick Command Window.
/// </summary>
public sealed class CommandPalette : Border
{
    private readonly List<PaletteAction> _actions;
    private readonly Action<string> _runCommand;

    /// <summary>Every action the palette offers (each menu command is one of them).</summary>
    public IReadOnlyList<PaletteAction> Actions => _actions;

    public CommandPalette(List<PaletteAction> actions, Action<string> runCommand)
    {
        _actions = actions;
        _runCommand = runCommand;
        IsVisible = false;
        Width = 520;
        MaxHeight = 360;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 48, 0, 0);
        Padding = new Thickness(8);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 6 24 0 #50000000");
        Input = new TextBox { Watermark = Strings.T("Type an action, or a FoxPro command and press Enter") };
        Results = new ListBox { MaxHeight = 280 };
        Child = new DockPanel { Children = { Input, Results } };
        DockPanel.SetDock(Input, Dock.Top);
        Input.TextChanged += (_, _) => Filter();
        Input.KeyDown += OnKey;
        Results.DoubleTapped += (_, _) => Accept();
        Filter();
    }

    public TextBox Input { get; }
    public ListBox Results { get; }

    public void Open()
    {
        Background = (IBrush?)Application.Current?.FindResource("SystemControlBackgroundAltHighBrush") ?? Brushes.White;
        BorderBrush = (IBrush?)Application.Current?.FindResource("SystemControlForegroundBaseLowBrush") ?? Brushes.Gray;
        IsVisible = true;
        Input.Text = "";
        Filter();
        Input.Focus();
    }

    public void Close() => IsVisible = false;

    private void Filter()
    {
        var q = Input.Text ?? "";
        var hits = _actions.Where(a => q.Length == 0 || a.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || a.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Results.ItemsSource = hits;
        Results.SelectedIndex = hits.Count > 0 ? 0 : -1;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Down: Results.SelectedIndex = Math.Min(Results.SelectedIndex + 1, Results.ItemCount - 1); e.Handled = true; break;
            case Key.Up: Results.SelectedIndex = Math.Max(Results.SelectedIndex - 1, 0); e.Handled = true; break;
            case Key.Enter: Accept(); e.Handled = true; break;
        }
    }

    /// <summary>Runs the selected action, or the typed text as a FoxPro command.</summary>
    public void Accept()
    {
        var text = Input.Text ?? "";
        Close();
        if (Results.SelectedItem is PaletteAction a && (text.Length == 0 || a.Title.Contains(text, StringComparison.OrdinalIgnoreCase) || a.DisplayTitle.Contains(text, StringComparison.OrdinalIgnoreCase))) a.Run();
        else if (text.Trim().Length > 0) _runCommand(text);
    }
}
