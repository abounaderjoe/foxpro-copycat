using JoePro.Core;

namespace JoePro.Language;

/// <summary>The FoxPro menu commands (the code GENMENU writes into .MPR files and that programs use directly).</summary>
public sealed partial class Parser
{
    private static readonly string[] MenuClauses =
    [
        "OF", "PROMPT", "AT", "BEFORE", "AFTER", "NEGOTIATE", "KEY", "MARK", "SKIP", "MESSAGE", "COLOR", "PICTURE", "PICTRES",
        "FROM", "TO", "IN", "FOOTER", "MARGIN", "MOVER", "MULTISELECT", "RELATIVE", "SCROLL", "SHADOW", "SHORTCUT", "TITLE",
        "BAR", "NOMARGIN", "INVERT", "STYLE", "FONT", "NOWAIT", "PAD", "REST", "EXTENDED", "ACTIVATE",
    ];

    private MenuName MenuNameArg()
    {
        var t = Peek() ?? throw Error("Missing name.");
        if (t.IsOp("(")) return new MenuName(null, Primary());
        if (t.Kind == TokenKind.Macro) { _p++; return new MenuName(null, new MacroExpr(t.Text)); }
        if (t.Kind == TokenKind.String) { _p++; return new MenuName(t.Text); }
        // A bar number or name computed by an expression (BAR THIS.nBar, BAR n + 1).
        if (t.Kind is TokenKind.Ident or TokenKind.Number && Peek(1) is { } next && (next.IsOp(".") || next.IsOp("(") || next.IsOp("+") || next.IsOp("-") || next.IsOp("*") || next.IsOp("/")))
            return new MenuName(null, Expression());
        if (t.Kind == TokenKind.Number) { _p++; return new MenuName(t.Text); }
        _p++;
        return new MenuName(t.Text);
    }

    /// <summary>Skips a COLOR clause (SCHEME n, or a color pair list): colors come from the theme.</summary>
    private void SkipColor()
    {
        if (AcceptKw("SCHEME")) { Expression(); return; }
        while (!AtEnd && !MenuClauses.Any(c => Kw(c) && !Kw("COLOR"))) _p++;
    }

    private MenuItemOptions ItemOptions(bool pad)
    {
        var o = new MenuItemOptions();
        while (!AtEnd)
        {
            if (AcceptKw("BEFORE")) o = o with { Before = MenuNameArg() };
            else if (AcceptKw("AFTER")) o = o with { After = MenuNameArg() };
            else if (AcceptKw("KEY"))
            {
                var start = _p;
                while (!AtEnd && !IsOp(",") && !MenuClauses.Any(Kw)) _p++;
                o = o with { KeyName = RawText(start, _p).Replace(" ", "") };
                if (AcceptOp(",")) o = o with { KeyText = Expression() };
            }
            else if (AcceptKw("MARK")) o = o with { Mark = Expression() };
            else if (AcceptKw("SKIP"))
            {
                if (AcceptKw("FOR")) { var start = _p; while (!AtEnd && !MenuClauses.Any(c => Kw(c) && c is not ("FOR" or "OF" or "TO" or "IN" or "AT"))) _p++; o = o with { SkipFor = RawText(start, _p) }; }
                else o = o with { SkipFor = "" };
            }
            else if (AcceptKw("MESSAGE")) o = o with { Message = Expression() };
            else if (AcceptKw("PICTURE")) o = o with { Picture = Expression() };
            else if (AcceptKw("PICTRES")) o = o with { PictRes = Ident() };
            else if (AcceptKw("COLOR")) SkipColor();
            else if (AcceptKw("AT")) { Expression(); if (AcceptOp(",")) Expression(); }
            else if (AcceptKw("NEGOTIATE")) { while (!AtEnd && !MenuClauses.Any(c => Kw(c) && c != "NEGOTIATE")) _p++; }
            else if (AcceptKw("FONT")) { Expression(); if (AcceptOp(",")) Expression(); if (AcceptOp(",")) Expression(); }
            else if (AcceptKw("STYLE") || AcceptKw("INVERT")) { if (!AtEnd && !MenuClauses.Any(Kw)) Expression(); }
            else throw Error($"Unrecognized phrase/keyword in DEFINE {(pad ? "PAD" : "BAR")}: {Peek()?.Text}");
        }
        return o;
    }

    /// <summary>DEFINE MENU/PAD/POPUP/BAR. Returns null when the verb is something else (DEFINE WINDOW, DEFINE BOX…).</summary>
    private Stmt? DefineMenuCommand()
    {
        if (AcceptKw("MENU"))
        {
            var name = MenuNameArg();
            bool bar = false;
            Expr? message = null;
            while (!AtEnd)
            {
                if (AcceptKw("BAR")) { bar = true; if (AcceptKw("AT")) { AcceptKw("LINE"); Expression(); } }
                else if (AcceptKw("MESSAGE")) message = Expression();
                else if (AcceptKw("IN")) { if (!AcceptKw("SCREEN")) { AcceptKw("WINDOW"); MenuNameArg(); } }
                else if (AcceptKw("KEY")) { while (!AtEnd && !MenuClauses.Any(c => Kw(c) && c != "KEY")) _p++; }
                else if (AcceptKw("COLOR")) SkipColor();
                else if (AcceptKw("NOMARGIN") || AcceptKw("MARGIN")) { }
                else if (AcceptKw("FONT")) { Expression(); if (AcceptOp(",")) Expression(); if (AcceptOp(",")) Expression(); }
                else throw Error($"Unrecognized phrase/keyword in DEFINE MENU: {Peek()?.Text}");
            }
            return new DefineMenuStmt(name, bar, message);
        }
        if (AcceptKw("PAD"))
        {
            var name = MenuNameArg();
            ExpectKw("OF");
            var menu = MenuNameArg();
            ExpectKw("PROMPT");
            var prompt = Expression();
            return new DefinePadStmt(name, menu, prompt, ItemOptions(pad: true));
        }
        if (AcceptKw("POPUP"))
        {
            var stmt = new DefinePopupStmt(MenuNameArg());
            while (!AtEnd)
            {
                if (AcceptKw("SHORTCUT")) stmt = stmt with { Shortcut = true };
                else if (AcceptKw("RELATIVE")) stmt = stmt with { Relative = true };
                else if (AcceptKw("MARGIN")) stmt = stmt with { Margin = true };
                else if (AcceptKw("MULTISELECT")) stmt = stmt with { MultiSelect = true };
                else if (AcceptKw("TITLE")) stmt = stmt with { Title = Expression() };
                else if (AcceptKw("MESSAGE")) stmt = stmt with { Message = Expression() };
                else if (AcceptKw("FROM") || AcceptKw("TO") || AcceptKw("AT")) { Expression(); if (AcceptOp(",")) Expression(); }
                else if (AcceptKw("IN")) { if (!AcceptKw("SCREEN")) { AcceptKw("WINDOW"); MenuNameArg(); } }
                else if (AcceptKw("PROMPT"))
                {
                    if (AcceptKw("FIELD")) stmt = stmt with { PromptKind = "FIELD", PromptExpr = Expression() };
                    else if (AcceptKw("FILES")) stmt = stmt with { PromptKind = "FILES", PromptExpr = AcceptKw("LIKE") ? NameArg(MenuClauses) : null };
                    else if (AcceptKw("STRUCTURE")) stmt = stmt with { PromptKind = "STRUCTURE" };
                    else throw Error("PROMPT FIELD, FILES or STRUCTURE expected.");
                }
                else if (AcceptKw("COLOR")) SkipColor();
                else if (AcceptKw("FONT")) { Expression(); if (AcceptOp(",")) Expression(); if (AcceptOp(",")) Expression(); }
                else if (AcceptKw("FOOTER")) Expression();
                else if (AcceptKw("KEY")) { while (!AtEnd && !MenuClauses.Any(c => Kw(c) && c != "KEY")) _p++; }
                else if (AcceptKw("MOVER") || AcceptKw("SCROLL") || AcceptKw("SHADOW") || AcceptKw("NOMARGIN")) { }
                else throw Error($"Unrecognized phrase/keyword in DEFINE POPUP: {Peek()?.Text}");
            }
            return stmt;
        }
        if (AcceptKw("BAR"))
        {
            var bar = MenuNameArg();
            ExpectKw("OF");
            var popup = MenuNameArg();
            Expr? prompt = AcceptKw("PROMPT") ? Expression() : null;
            return new DefineBarStmt(bar, popup, prompt, ItemOptions(pad: false));
        }
        return null;
    }

    /// <summary>ON PAD / ON BAR / ON SELECTION …; null for other ON commands.</summary>
    private Stmt? OnMenuCommand()
    {
        if (Kw("PAD") || Kw("BAR"))
        {
            var kind = Next().Text.ToUpperInvariant();
            var name = MenuNameArg();
            ExpectKw("OF");
            var of = MenuNameArg();
            string? popup = null, menu = null;
            if (AcceptKw("ACTIVATE"))
            {
                if (AcceptKw("POPUP")) popup = MenuNameText();
                else { ExpectKw("MENU"); menu = MenuNameText(); }
            }
            return new OnMenuStmt(kind, name, of, popup, menu, null);
        }
        if (Kw("SELECTION"))
        {
            _p++;
            if (!(Kw("PAD") || Kw("BAR") || Kw("POPUP") || Kw("MENU"))) throw Error("ON SELECTION PAD, BAR, POPUP or MENU expected.");
            var kind = "SELECTION " + Next().Text.ToUpperInvariant();
            bool all = false;
            MenuName name;
            MenuName? of = null;
            if ((kind is "SELECTION POPUP" or "SELECTION MENU") && AcceptKw("ALL")) { all = true; name = new MenuName("ALL"); }
            else name = MenuNameArg();
            if (kind is "SELECTION PAD" or "SELECTION BAR") { ExpectKw("OF"); of = MenuNameArg(); }
            var command = AtEnd ? null : RawText(_p, _t.Count);
            _p = _t.Count;
            return new OnMenuStmt(kind, name, of, null, null, command, all);
        }
        return null;
    }

    private string MenuNameText() => Peek() is { Kind: TokenKind.Ident } t ? Next().Text : throw Error("Missing name.");

    /// <summary>ACTIVATE MENU/POPUP, DEACTIVATE/HIDE/SHOW MENU(S)/POPUP(S); null for other forms (ACTIVATE WINDOW…).</summary>
    private Stmt? MenuVerb(string verb)
    {
        if (verb == "ACTIVATE" && (Kw("MENU") || Kw("POPUP")))
        {
            var popup = Next().Text.Equals("POPUP", StringComparison.OrdinalIgnoreCase);
            var name = MenuNameArg();
            bool noWait = false;
            Expr? item = null;
            while (!AtEnd)
            {
                if (AcceptKw("NOWAIT")) noWait = true;
                else if (AcceptKw("PAD") || AcceptKw("BAR")) item = Kw("_") ? new LiteralExpr(Value.String(Next().Text)) : Peek()?.Kind == TokenKind.Ident ? new LiteralExpr(Value.String(Next().Text)) : Expression();
                else if (AcceptKw("AT")) { Expression(); if (AcceptOp(",")) Expression(); }
                else if (AcceptKw("REST")) { }
                else throw Error($"Unrecognized phrase/keyword in ACTIVATE: {Peek()?.Text}");
            }
            return new ActivateMenuStmt(popup, name, noWait, item);
        }
        if (verb is "DEACTIVATE" or "HIDE" or "SHOW" or "RELEASE" && (Kw("MENU") || Kw("MENUS") || Kw("POPUP") || Kw("POPUPS") || Kw("PAD") || Kw("BAR")))
        {
            var kind = Next().Text.ToUpperInvariant().TrimEnd('S');
            var names = new List<MenuName>();
            bool all = false, extended = false;
            MenuName? of = null;
            if (AcceptKw("ALL")) all = true;
            while (!AtEnd && !Kw("OF") && !Kw("EXTENDED") && !Kw("SAVE"))
            {
                names.Add(MenuNameArg());
                if (!AcceptOp(",")) break;
            }
            if (AcceptKw("OF")) of = MenuNameArg();
            if (AcceptKw("EXTENDED")) extended = true;
            AcceptKw("SAVE");
            if (names.Count == 0 && !all && kind is "MENU" or "POPUP") all = true; // RELEASE MENUS / DEACTIVATE MENU: all of them
            return new MenuControlStmt(verb, kind, names, all, of, extended);
        }
        return null;
    }

    /// <summary>SET MARK OF MENU|PAD|POPUP|BAR … TO lExpr, SET SKIP OF … lExpr.</summary>
    private Stmt SetMenuFlag(string flag)
    {
        ExpectKw("OF");
        var kind = Next().Text.ToUpperInvariant();
        MenuName? name = null, of = null;
        if (kind is "MENU" or "POPUP") name = MenuNameArg();
        else { name = MenuNameArg(); ExpectKw("OF"); of = MenuNameArg(); }
        AcceptKw("TO");
        return new SetMenuFlagStmt(flag, kind, name, of, Expression());
    }
}
