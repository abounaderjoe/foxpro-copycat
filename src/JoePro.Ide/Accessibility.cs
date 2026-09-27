using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace JoePro.Ide;

/// <summary>
/// Accessible names for the IDE's controls. Screen readers announce a control by its automation name; icon buttons,
/// text boxes and lists have none of their own. When a control is loaded without a usable name, it gets one from its
/// tooltip or from the label next to it. Controls with no such text get explicit names where they are built.
/// </summary>
public static class Accessibility
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Control.LoadedEvent.AddClassHandler<Control>((c, _) => Fill(c), RoutingStrategies.Direct, handledEventsToo: true);
    }

    /// <summary>Sets a control's accessible name (and returns the control, for use in initializers).</summary>
    public static T Named<T>(this T control, string name) where T : Control
    {
        AutomationProperties.SetName(control, name);
        return control;
    }

    private static bool IsInteractive(Control c) =>
        c is Button or ToggleButton or TextBox or ComboBox or ListBox or TreeView or Slider or NumericUpDown or AutoCompleteBox or AvaloniaEdit.TextEditor;

    private static void Fill(Control c)
    {
        if (!IsInteractive(c)) return;
        var current = AutomationProperties.GetName(c);
        if (!string.IsNullOrWhiteSpace(current) && current.Trim().Length > 2) return;
        if (c is ContentControl { Content: string s } && s.Trim().Length > 2 && ToolTip.GetTip(c) == null) return; // text buttons speak for themselves
        var name = TipText(ToolTip.GetTip(c)) ?? LabelFor(c) ?? TabName(c) ?? (c as TextBox)?.Watermark?.Trim()
            ?? (c is ContentControl { Content: string g } ? GlyphName(g) : null);
        if (!string.IsNullOrWhiteSpace(name)) AutomationProperties.SetName(c, name);
    }

    private static string? TipText(object? tip) => tip switch
    {
        string s when s.Trim().Length > 0 => s.Trim(),
        TextBlock t when !string.IsNullOrWhiteSpace(t.Text) => t.Text.Trim(),
        _ => null,
    };

    /// <summary>What the IDE's icon glyphs mean, for buttons that show only a glyph.</summary>
    public static string? GlyphName(string glyph) => glyph.Trim() switch
    {
        "▲" or "↑" => "Move up", "▼" or "↓" => "Move down", "✕" or "×" => "Remove", "+" => "Add", "−" or "-" => "Remove",
        "↶" => "Undo", "↷" => "Redo", "⟳" or "↻" => "Refresh", "…" or "..." => "Browse",
        _ => null,
    };

    /// <summary>The name of the tab a control fills (the "Locals" tree in the Locals tab).</summary>
    private static string? TabName(Control c)
    {
        var p = c.GetLogicalParent();
        for (int i = 0; i < 3 && p != null; i++, p = p.GetLogicalParent())
        {
            if (p is TabItem tab)
            {
                var n = AutomationProperties.GetName(tab);
                return !string.IsNullOrWhiteSpace(n) ? n : tab.Header as string;
            }
            if (p is Panel { Children.Count: > 1 }) return null; // one of several controls on the tab: not the tab's own
        }
        return null;
    }

    /// <summary>The text of the label just before the control in the same panel (a "Name:" beside a text box).</summary>
    private static string? LabelFor(Control c)
    {
        if (c.GetLogicalParent() is not Panel panel) return null;
        var i = panel.Children.IndexOf(c);
        for (int j = i - 1; j >= 0; j--)
        {
            switch (panel.Children[j])
            {
                case TextBlock { Text: { Length: > 0 } t }:
                    return t.Trim().TrimEnd(':').Trim();
                case Label { Content: string l } when l.Length > 0:
                    return l.Trim().TrimEnd(':').Trim();
                case Control other when IsInteractive(other):
                    return null; // another input sits between: the label is not ours
            }
        }
        return null;
    }
}
