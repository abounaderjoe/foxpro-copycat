using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime;

/// <summary>A menu bar (DEFINE MENU, or the system menu _MSYSMENU) and its pads.</summary>
public sealed class MenuBarDef
{
    public required string Name { get; init; }
    public List<PadDef> Pads { get; } = new();
    public string? OnSelection { get; set; }
    public string? Message { get; set; }
    public bool Skip { get; set; }
    public PadDef? Pad(string name) => Pads.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public MenuBarDef Clone()
    {
        var m = new MenuBarDef { Name = Name, OnSelection = OnSelection, Message = Message, Skip = Skip };
        m.Pads.AddRange(Pads.Select(p => (PadDef)p.CloneItem()));
        return m;
    }
}

public abstract class MenuItemDef
{
    public string Prompt { get; set; } = "";
    public string? KeyName { get; set; }
    public string? KeyText { get; set; }
    public bool Mark { get; set; }
    /// <summary>SKIP FOR expression; "" for SKIP (always disabled).</summary>
    public string? SkipFor { get; set; }
    public string? Message { get; set; }
    public string? Picture { get; set; }
    public string? PictRes { get; set; }
    /// <summary>ON SELECTION command (code run when the item is chosen).</summary>
    public string? Command { get; set; }
    /// <summary>ON PAD/BAR … ACTIVATE POPUP name: the item opens a submenu.</summary>
    public string? Popup { get; set; }
    public bool IsSeparator => Prompt.Trim() == "\\-";
    /// <summary>The prompt with the \&lt; hot-key marker removed.</summary>
    public string Caption => Prompt.Replace("\\<", "").Replace("\\\\", "\\");

    internal MenuItemDef CloneItem() => (MenuItemDef)MemberwiseClone();
}

public sealed class PadDef : MenuItemDef
{
    public required string Name { get; init; }
}

public sealed class BarDef : MenuItemDef
{
    /// <summary>The bar number, or 0 for a system bar (see <see cref="SystemBar"/>).</summary>
    public int Number { get; init; }
    /// <summary>A VFP system bar such as _MFI_OPEN (DEFINE BAR _MFI_OPEN OF …).</summary>
    public string? SystemBar { get; init; }
    public string Id => SystemBar ?? Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class PopupDef
{
    public required string Name { get; init; }
    public List<BarDef> Bars { get; } = new();
    public bool Shortcut { get; set; }
    public string? Title { get; set; }
    public string? OnSelection { get; set; }
    public bool Skip { get; set; }
    public BarDef? Bar(string id) => Bars.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public PopupDef Clone()
    {
        var p = new PopupDef { Name = Name, Shortcut = Shortcut, Title = Title, OnSelection = OnSelection, Skip = Skip };
        p.Bars.AddRange(Bars.Select(b => (BarDef)b.CloneItem()));
        return p;
    }
}

/// <summary>
/// The FoxPro menu system: menu bars with pads, popups with bars, what selecting them runs, marks and SKIP FOR,
/// and the current selection (for BAR(), POPUP(), PROMPT(), PAD(), MENU()). A UI host shows the active menu bar
/// and shortcut popups and calls <see cref="Interpreter.SelectMenuItem"/> when the user chooses an item.
/// </summary>
public sealed class MenuSystem
{
    public const string SystemMenu = "_MSYSMENU";
    public Dictionary<string, MenuBarDef> Menus { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PopupDef> Popups { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The menu bar shown at the top of the application window (null: the host's own menu).</summary>
    public string? ActiveMenu { get; set; }
    private readonly Dictionary<string, Stack<MenuBarDef?>> _menuStack = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Stack<PopupDef?>> _popupStack = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PUSH MENU / PUSH POPUP: saves the current definition.</summary>
    public void Push(bool popup, string name)
    {
        if (popup) StackOf(_popupStack, name).Push(Popups.TryGetValue(name, out var p) ? p.Clone() : null);
        else StackOf(_menuStack, name).Push(Menus.TryGetValue(name, out var m) ? m.Clone() : null);
    }

    /// <summary>POP MENU / POP POPUP: restores the last saved definition. False if nothing was pushed.</summary>
    public bool Pop(bool popup, string name)
    {
        if (popup)
        {
            if (!_popupStack.TryGetValue(name, out var ps) || ps.Count == 0) return false;
            if (ps.Pop() is { } p) Popups[name] = p; else Popups.Remove(name);
        }
        else
        {
            if (!_menuStack.TryGetValue(name, out var ms) || ms.Count == 0) return false;
            if (ms.Pop() is { } m) Menus[name] = m; else Menus.Remove(name);
        }
        return true;
    }

    private static Stack<T> StackOf<T>(Dictionary<string, Stack<T>> d, string name)
    {
        if (!d.TryGetValue(name, out var s)) d[name] = s = new Stack<T>();
        return s;
    }

    public string? LastPad { get; set; }
    public string? LastMenu { get; set; }
    public string? LastPopup { get; set; }
    public string? LastBar { get; set; }
    public string LastPrompt { get; set; } = "";
    /// <summary>SET SYSMENU OFF hides the menu bar; AUTOMATIC/ON shows it.</summary>
    public bool Visible { get; set; } = true;
    public string? DefaultSnapshot { get; set; }

    public MenuBarDef Menu(string name) => Menus.TryGetValue(name, out var m) ? m : Menus[name] = new MenuBarDef { Name = name.ToUpperInvariant() == SystemMenu ? SystemMenu : name };

    public PopupDef? Popup(string name) => Popups.GetValueOrDefault(name);
}

public sealed partial class Interpreter
{
    public MenuSystem Menus { get; } = new();

    private string MenuNameValue(MenuName n) => n.Expr != null ? Eval(n.Expr) switch
    {
        { Kind: ValueKind.Character } s => s.AsString.Trim(),
        var v => Formatter.ToDisplay(v, Options).Trim(),
    } : n.Text!;

    private void MenusChanged() => Ui?.MenusChanged();

    private string Text(Expr e) => Eval(e) switch
    {
        { Kind: ValueKind.Character } s => s.AsString,
        var v => Formatter.ToDisplay(v, Options).Trim(),
    };

    private static int Position<T>(List<T> list, Func<T, bool> matches) => list.FindIndex(x => matches(x));

    private void ApplyItemOptions(MenuItemDef item, MenuItemOptions o)
    {
        if (o.KeyName != null) item.KeyName = o.KeyName;
        if (o.KeyText != null) item.KeyText = Text(o.KeyText);
        if (o.Mark != null) item.Mark = Eval(o.Mark) is { Kind: ValueKind.Character } mk ? mk.AsString.Length > 0 : Truthy(Eval(o.Mark));
        if (o.SkipFor != null) item.SkipFor = o.SkipFor;
        if (o.Message != null) item.Message = Text(o.Message);
        if (o.Picture != null) item.Picture = Text(o.Picture);
        if (o.PictRes != null) item.PictRes = o.PictRes;
    }

    internal void ExecDefineMenu(DefineMenuStmt s)
    {
        var name = MenuNameValue(s.Name);
        var menu = new MenuBarDef { Name = name, Message = s.Message != null ? Text(s.Message) : null };
        Menus.Menus[name] = menu;
        MenusChanged();
    }

    internal void ExecDefinePad(DefinePadStmt s)
    {
        var menu = Menus.Menu(MenuNameValue(s.Menu));
        var name = MenuNameValue(s.Name);
        menu.Pads.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var pad = new PadDef { Name = name, Prompt = Text(s.Prompt) };
        ApplyItemOptions(pad, s.Options);
        var at = s.Options.Before != null ? Position(menu.Pads, p => p.Name.Equals(MenuNameValue(s.Options.Before), StringComparison.OrdinalIgnoreCase))
            : s.Options.After != null ? Position(menu.Pads, p => p.Name.Equals(MenuNameValue(s.Options.After), StringComparison.OrdinalIgnoreCase)) + 1 : -1;
        if (at >= 0 && at <= menu.Pads.Count) menu.Pads.Insert(at, pad); else menu.Pads.Add(pad);
        MenusChanged();
    }

    internal void ExecDefinePopup(DefinePopupStmt s)
    {
        var name = MenuNameValue(s.Name);
        var popup = new PopupDef { Name = name, Shortcut = s.Shortcut, Title = s.Title != null ? Text(s.Title) : null };
        switch (s.PromptKind)
        {
            case "FIELD" when Session.Current.InUse:
            {
                // One bar per record of the current table.
                var wa = Session.Current;
                var n = 0;
                foreach (var r in RecordsInScope(wa, new Scope("ALL")))
                {
                    wa.Go(r);
                    popup.Bars.Add(new BarDef { Number = ++n, Prompt = Text(s.PromptExpr!).TrimEnd() });
                }
                break;
            }
            case "FILES":
            {
                var pattern = s.PromptExpr != null ? Text(s.PromptExpr) : "*.*";
                var dir = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? Path.Combine(Options.Default_, d) : Options.Default_;
                var n = 0;
                if (Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir, Path.GetFileName(pattern)).Order(StringComparer.OrdinalIgnoreCase))
                        popup.Bars.Add(new BarDef { Number = ++n, Prompt = Path.GetFileName(f).ToUpperInvariant() });
                break;
            }
            case "STRUCTURE" when Session.Current.InUse:
            {
                var n = 0;
                foreach (var f in Session.Current.Table.Fields) popup.Bars.Add(new BarDef { Number = ++n, Prompt = f.Name.ToUpperInvariant() });
                break;
            }
        }
        Menus.Popups[name] = popup;
        MenusChanged();
    }

    internal void ExecDefineBar(DefineBarStmt s)
    {
        var popupName = MenuNameValue(s.Popup);
        var popup = Menus.Popup(popupName) ?? (Menus.Popups[popupName] = new PopupDef { Name = popupName });
        var id = MenuNameValue(s.Bar);
        var isNumber = int.TryParse(id, out var number);
        popup.Bars.RemoveAll(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var bar = new BarDef
        {
            Number = isNumber ? number : 0,
            SystemBar = isNumber ? null : id.ToUpperInvariant(),
            Prompt = s.Prompt != null ? Text(s.Prompt) : SystemBarPrompt(id),
        };
        ApplyItemOptions(bar, s.Options);
        if (!isNumber) bar.Command ??= SystemBarCommand(id);
        var at = s.Options.Before != null ? Position(popup.Bars, b => b.Id.Equals(MenuNameValue(s.Options.Before), StringComparison.OrdinalIgnoreCase))
            : s.Options.After != null ? Position(popup.Bars, b => b.Id.Equals(MenuNameValue(s.Options.After), StringComparison.OrdinalIgnoreCase)) + 1 : -1;
        if (at >= 0 && at <= popup.Bars.Count) popup.Bars.Insert(at, bar);
        else if (isNumber)
        {
            // Numbered bars are ordered by number unless placed with BEFORE/AFTER.
            var i = popup.Bars.FindIndex(b => b.Number > number && b.SystemBar == null);
            if (i >= 0) popup.Bars.Insert(i, bar); else popup.Bars.Add(bar);
        }
        else popup.Bars.Add(bar);
        MenusChanged();
    }

    /// <summary>Prompts and actions for the common VFP system bars used in application menus.</summary>
    private static string SystemBarPrompt(string id) => id.ToUpperInvariant() switch
    {
        "_MED_CUT" => "Cu\\<t", "_MED_COPY" => "\\<Copy", "_MED_PASTE" => "\\<Paste", "_MED_CLEAR" => "Cle\\<ar", "_MED_SLCTA" => "Se\\<lect All",
        "_MED_UNDO" => "\\<Undo", "_MED_REDO" => "Re\\<do", "_MED_FIND" => "\\<Find...", "_MFI_OPEN" => "\\<Open...", "_MFI_CLOSE" => "\\<Close",
        "_MFI_SAVE" => "\\<Save", "_MFI_SAVAS" => "Save \\<As...", "_MFI_QUIT" => "E\\<xit", "_MFI_PRINT" => "\\<Print...",
        "_MWI_ARRAN" => "\\<Arrange All", "_MWI_CASCADE" => "\\<Cascade", "_MWI_CMD" => "Co\\<mmand Window", "_MST_HELP" => "\\<Contents",
        "_MST_ABOUT" => "\\<About...", "_MWINDOW" => "\\<Window",
        _ => id,
    };

    private static string? SystemBarCommand(string id) => id.ToUpperInvariant() switch
    {
        "_MFI_QUIT" => "QUIT",
        _ => null,
    };

    internal void ExecOnMenu(OnMenuStmt s)
    {
        var name = MenuNameValue(s.Name);
        switch (s.Kind)
        {
            case "PAD":
            {
                var pad = Menus.Menu(MenuNameValue(s.Of!)).Pad(name) ?? throw new VfpException(1638, $"Pad {name.ToUpperInvariant()} is not defined.");
                pad.Popup = s.ActivatePopup ?? s.ActivateMenu;
                break;
            }
            case "BAR":
            {
                var popup = Menus.Popup(MenuNameValue(s.Of!)) ?? throw new VfpException(1639, $"Popup {MenuNameValue(s.Of!).ToUpperInvariant()} is not defined.");
                var bar = popup.Bar(name) ?? throw new VfpException(1640, $"Bar {name} is not defined.");
                bar.Popup = s.ActivatePopup ?? s.ActivateMenu;
                break;
            }
            case "SELECTION PAD":
            {
                var pad = Menus.Menu(MenuNameValue(s.Of!)).Pad(name) ?? throw new VfpException(1638, $"Pad {name.ToUpperInvariant()} is not defined.");
                pad.Command = s.Command;
                break;
            }
            case "SELECTION BAR":
            {
                var popup = Menus.Popup(MenuNameValue(s.Of!)) ?? throw new VfpException(1639, $"Popup {MenuNameValue(s.Of!).ToUpperInvariant()} is not defined.");
                var bar = popup.Bar(name) ?? throw new VfpException(1640, $"Bar {name} is not defined.");
                bar.Command = s.Command;
                break;
            }
            case "SELECTION POPUP":
                foreach (var p in s.All ? Menus.Popups.Values.ToList() : [Menus.Popup(name) ?? throw new VfpException(1639, $"Popup {name.ToUpperInvariant()} is not defined.")])
                    p.OnSelection = s.Command;
                break;
            case "SELECTION MENU":
                foreach (var m in s.All ? Menus.Menus.Values.ToList() : [Menus.Menu(name)])
                    m.OnSelection = s.Command;
                break;
        }
        MenusChanged();
    }

    internal void ExecActivateMenu(ActivateMenuStmt s)
    {
        var name = MenuNameValue(s.Name);
        if (s.Popup)
        {
            if (Menus.Popup(name) == null) throw new VfpException(1639, $"Popup {name.ToUpperInvariant()} is not defined.");
            if (Ui?.ActivatePopup(name) != true)
                Notify($"ACTIVATE POPUP {name}: no user interface is attached.");
            return;
        }
        if (!Menus.Menus.ContainsKey(name)) throw new VfpException(1637, $"Menu {name.ToUpperInvariant()} is not defined.");
        Menus.ActiveMenu = name;
        MenusChanged();
    }

    internal void ExecMenuControl(MenuControlStmt s)
    {
        var names = s.Names.Select(MenuNameValue).ToList();
        switch (s.Verb, s.Kind)
        {
            case ("RELEASE", "MENU"):
                foreach (var n in s.All ? Menus.Menus.Keys.Where(k => !k.Equals(MenuSystem.SystemMenu, StringComparison.OrdinalIgnoreCase)).ToList() : names)
                    Menus.Menus.Remove(n);
                if (Menus.ActiveMenu != null && !Menus.Menus.ContainsKey(Menus.ActiveMenu)) Menus.ActiveMenu = null;
                break;
            case ("RELEASE", "POPUP"):
                foreach (var n in s.All ? Menus.Popups.Keys.ToList() : names)
                {
                    if (s.Extended && Menus.Popup(n) is { } p)
                        foreach (var sub in p.Bars.Select(b => b.Popup).OfType<string>()) Menus.Popups.Remove(sub);
                    Menus.Popups.Remove(n);
                }
                break;
            case ("RELEASE", "PAD"):
            {
                var menu = Menus.Menu(s.Of != null ? MenuNameValue(s.Of) : MenuSystem.SystemMenu);
                if (s.All) menu.Pads.Clear();
                foreach (var n in names) menu.Pads.RemoveAll(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                break;
            }
            case ("RELEASE", "BAR"):
            {
                var popup = s.Of != null ? Menus.Popup(MenuNameValue(s.Of)) : null;
                if (popup == null) break;
                if (s.All) popup.Bars.Clear();
                foreach (var n in names) popup.Bars.RemoveAll(b => b.Id.Equals(n, StringComparison.OrdinalIgnoreCase));
                break;
            }
            case ("DEACTIVATE" or "HIDE", "MENU"):
                if (s.All || names.Any(n => n.Equals(Menus.ActiveMenu, StringComparison.OrdinalIgnoreCase))) Menus.ActiveMenu = null;
                break;
            case ("SHOW", "MENU"):
                if (names.Count > 0 && Menus.Menus.ContainsKey(names[0])) Menus.ActiveMenu = names[0];
                break;
        }
        MenusChanged();
    }

    internal void ExecSetMenuFlag(SetMenuFlagStmt s)
    {
        var value = Truthy(Eval(s.Value));
        var name = s.Name != null ? MenuNameValue(s.Name) : "";
        switch (s.Kind)
        {
            case "MENU":
                Menus.Menu(name).Skip = s.Flag == "SKIP" && value;
                break;
            case "POPUP":
                if (Menus.Popup(name) is { } p && s.Flag == "SKIP") p.Skip = value;
                break;
            case "PAD":
            {
                var pad = Menus.Menu(MenuNameValue(s.Of!)).Pad(name) ?? throw new VfpException(1638, $"Pad {name.ToUpperInvariant()} is not defined.");
                if (s.Flag == "MARK") pad.Mark = value; else pad.SkipFor = value ? "" : null;
                break;
            }
            case "BAR":
            {
                var bar = Menus.Popup(MenuNameValue(s.Of!))?.Bar(name) ?? throw new VfpException(1640, $"Bar {name} is not defined.");
                if (s.Flag == "MARK") bar.Mark = value; else bar.SkipFor = value ? "" : null;
                break;
            }
        }
        MenusChanged();
    }

    /// <summary>SET SYSMENU ON | OFF | AUTOMATIC | TO [DEFAULT] | SAVE | NOSAVE.</summary>
    internal void ExecSetSysMenu(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        switch (t)
        {
            case "OFF": Menus.Visible = false; break;
            case "ON" or "AUTOMATIC": Menus.Visible = true; break;
            case "TO DEFAULT" or "TO DEFA":
                Menus.Menus.Remove(MenuSystem.SystemMenu);
                Menus.ActiveMenu = null;
                break;
            case "TO":
                // An empty system menu bar: the application defines its own pads next.
                Menus.Menus[MenuSystem.SystemMenu] = new MenuBarDef { Name = MenuSystem.SystemMenu };
                Menus.ActiveMenu = MenuSystem.SystemMenu;
                break;
            case "SAVE" or "NOSAVE": break;
            default:
                if (t.StartsWith("TO ", StringComparison.Ordinal))
                {
                    // SET SYSMENU TO _MFILE, _MEDIT…: keep only those pads.
                    var keep = t[3..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    var menu = Menus.Menu(MenuSystem.SystemMenu);
                    menu.Pads.RemoveAll(p => !keep.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
                    Menus.ActiveMenu = MenuSystem.SystemMenu;
                }
                break;
        }
        MenusChanged();
    }

    /// <summary>True when an item's SKIP FOR condition disables it now.</summary>
    public bool IsMenuItemSkipped(MenuItemDef item)
    {
        if (item.SkipFor == null) return false;
        if (item.SkipFor.Trim().Length == 0) return true;
        try { return Truthy(Evaluate(item.SkipFor)); }
        catch (VfpException) { return false; }
    }

    /// <summary>
    /// The user chose a menu item: records it for BAR()/POPUP()/PROMPT()/PAD()/MENU() and runs its ON SELECTION
    /// command (else the popup's, else the menu's). <paramref name="owner"/> is the menu (for a pad) or popup (for a bar).
    /// </summary>
    public void SelectMenuItem(string owner, string item, bool isPad)
    {
        string? command;
        if (isPad)
        {
            var menu = Menus.Menu(owner);
            var pad = menu.Pad(item) ?? throw new VfpException(1638, $"Pad {item.ToUpperInvariant()} is not defined.");
            if (IsMenuItemSkipped(pad)) return;
            Menus.LastMenu = menu.Name;
            Menus.LastPad = pad.Name;
            Menus.LastPrompt = pad.Caption;
            command = pad.Command ?? menu.OnSelection;
        }
        else
        {
            var popup = Menus.Popup(owner) ?? throw new VfpException(1639, $"Popup {owner.ToUpperInvariant()} is not defined.");
            var bar = popup.Bar(item) ?? throw new VfpException(1640, $"Bar {item} is not defined.");
            if (IsMenuItemSkipped(bar)) return;
            Menus.LastPopup = popup.Name;
            Menus.LastBar = bar.Id;
            Menus.LastPrompt = bar.Caption;
            var pad = Menus.Menus.Values.SelectMany(m => m.Pads.Select(p => (Menu: m, Pad: p))).FirstOrDefault(x => x.Pad.Popup?.Equals(popup.Name, StringComparison.OrdinalIgnoreCase) == true);
            if (pad.Pad != null) { Menus.LastMenu = pad.Menu.Name; Menus.LastPad = pad.Pad.Name; }
            command = bar.Command ?? popup.OnSelection ?? pad.Menu?.OnSelection;
        }
        if (string.IsNullOrWhiteSpace(command)) return;
        ExecuteCommand(command);
    }
}
