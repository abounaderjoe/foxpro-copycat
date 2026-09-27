using Avalonia.Controls;
using Avalonia.Input;
using JoePro.Core;
using JoePro.Runtime;

namespace JoePro.Ui.Runtime;

/// <summary>Builds Avalonia menus from the FoxPro menu system; choosing an item runs its ON SELECTION code.</summary>
public static class MenuBuilder
{
    /// <summary>Menu items for the pads of a menu bar (each pad with its popup as a submenu).</summary>
    public static List<Control> Pads(Interpreter rt, MenuBarDef menu, Action<VfpException> error)
    {
        var list = new List<Control>();
        foreach (var pad in menu.Pads)
        {
            var item = Item(rt, pad, error, depth: 0);
            if (pad.Popup is { } popupName && rt.Menus.Popup(popupName) is { } popup)
            {
                foreach (var c in Bars(rt, popup, error, 1)) item.Items.Add(c);
                // SKIP FOR is evaluated when the pad opens, as in VFP.
                item.SubmenuOpened += (_, _) => Refresh(rt, item);
            }
            else item.Click += (_, _) => Run(() => rt.SelectMenuItem(menu.Name, pad.Name, isPad: true), error);
            list.Add(item);
        }
        return list;
    }

    /// <summary>Menu items for the bars of a popup (separators for "\-", submenus for ON BAR … ACTIVATE POPUP).</summary>
    public static List<Control> Bars(Interpreter rt, PopupDef popup, Action<VfpException> error, int depth = 0)
    {
        var list = new List<Control>();
        foreach (var bar in popup.Bars)
        {
            if (bar.IsSeparator) { list.Add(new Separator()); continue; }
            var item = Item(rt, bar, error, depth);
            if (bar.Popup is { } sub && rt.Menus.Popup(sub) is { } subPopup && depth < 10)
            {
                foreach (var c in Bars(rt, subPopup, error, depth + 1)) item.Items.Add(c);
                item.SubmenuOpened += (_, _) => Refresh(rt, item);
            }
            else item.Click += (_, _) => Run(() => rt.SelectMenuItem(popup.Name, bar.Id, isPad: false), error);
            list.Add(item);
        }
        return list;
    }

    private static MenuItem Item(Interpreter rt, MenuItemDef def, Action<VfpException> error, int depth)
    {
        var item = new MenuItem
        {
            Header = def.Prompt.Replace("_", "__").Replace("\\<", "_").Replace("\\\\", "\\"),
            Tag = def,
            IsEnabled = !rt.IsMenuItemSkipped(def),
        };
        if (def.Mark) item.Icon = new TextBlock { Text = "✓" };
        if (def.Message is { Length: > 0 } msg) ToolTip.SetTip(item, msg);
        if (Gesture(def.KeyName) is { } g)
        {
            item.InputGesture = g;
            if (depth > 0) item.HotKey = g;
        }
        return item;
    }

    /// <summary>Re-evaluates SKIP FOR and marks of the items in an opened submenu.</summary>
    private static void Refresh(Interpreter rt, MenuItem parent)
    {
        foreach (var child in parent.Items.OfType<MenuItem>())
            if (child.Tag is MenuItemDef def)
            {
                child.IsEnabled = !rt.IsMenuItemSkipped(def);
                child.Icon = def.Mark ? new TextBlock { Text = "✓" } : null;
            }
    }

    /// <summary>A FoxPro key label (CTRL+N, ALT+F4, F5, SHIFT+CTRL+S) as a key gesture.</summary>
    public static KeyGesture? Gesture(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return null;
        var parts = keyName.ToUpperInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = KeyModifiers.None;
        Key? key = null;
        foreach (var p in parts)
        {
            switch (p)
            {
                case "CTRL": mods |= KeyModifiers.Control; break;
                case "ALT": mods |= KeyModifiers.Alt; break;
                case "SHIFT": mods |= KeyModifiers.Shift; break;
                default:
                    key = p switch
                    {
                        "INS" => Key.Insert, "DEL" => Key.Delete, "PGUP" => Key.PageUp, "PGDN" => Key.PageDown, "ENTER" => Key.Enter,
                        "ESC" => Key.Escape, "SPACEBAR" => Key.Space, "BACKSPACE" => Key.Back,
                        _ when p.Length == 1 && char.IsDigit(p[0]) => Key.D0 + (p[0] - '0'),
                        _ => Enum.TryParse<Key>(p, true, out var k) ? k : null,
                    };
                    break;
            }
        }
        return key is { } kk ? new KeyGesture(kk, mods) : null;
    }

    private static void Run(Action action, Action<VfpException> error)
    {
        try { action(); }
        catch (VfpException ex) { error(ex); }
    }
}
