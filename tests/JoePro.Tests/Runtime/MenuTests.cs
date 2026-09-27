namespace JoePro.Tests.Runtime;

/// <summary>The menu commands as GENMENU writes them into .MPR files, and the menu functions.</summary>
public class MenuTests : RuntimeHarness
{
    private const string Mpr = """
        SET SYSMENU TO
        SET SYSMENU AUTOMATIC
        DEFINE PAD _file OF _MSYSMENU PROMPT "\<File" COLOR SCHEME 3 KEY ALT+F, ""
        DEFINE PAD _edit OF _MSYSMENU PROMPT "\<Edit" COLOR SCHEME 3 KEY ALT+E, ""
        ON PAD _file OF _MSYSMENU ACTIVATE POPUP file
        ON PAD _edit OF _MSYSMENU ACTIVATE POPUP edit
        DEFINE POPUP file MARGIN RELATIVE SHADOW COLOR SCHEME 4
        DEFINE BAR 1 OF file PROMPT "\<New" KEY CTRL+N, "Ctrl+N" MESSAGE "Create a record"
        DEFINE BAR 2 OF file PROMPT "\<Delete" SKIP FOR !glCanDelete
        DEFINE BAR 3 OF file PROMPT "\-"
        DEFINE BAR 4 OF file PROMPT "E\<xit"
        ON SELECTION BAR 1 OF file gcLog = gcLog + "new;"
        ON SELECTION BAR 2 OF file gcLog = gcLog + "delete;"
        ON SELECTION BAR 4 OF file gcLog = gcLog + "exit:" + PROMPT() + ";"
        DEFINE POPUP edit MARGIN RELATIVE
        DEFINE BAR _med_copy OF edit PROMPT "\<Copy" KEY CTRL+C, "Ctrl+C"
        DEFINE BAR _med_paste OF edit
        ON SELECTION POPUP edit gcLog = gcLog + "edit:" + BAR() + ";"
        """;

    [Fact]
    public void Mpr_code_builds_the_system_menu()
    {
        Run("PUBLIC gcLog, glCanDelete\ngcLog = \"\"\nglCanDelete = .F.\n" + Mpr);
        var menus = Rt.Menus;
        Assert.Equal("_MSYSMENU", menus.ActiveMenu);
        var sys = menus.Menus["_MSYSMENU"];
        Assert.Equal(["_file", "_edit"], sys.Pads.Select(p => p.Name));
        Assert.Equal("file", sys.Pad("_file")!.Popup);
        var file = menus.Popup("file")!;
        Assert.Equal(["New", "Delete", "\\-", "Exit"], file.Bars.Select(b => b.Caption));
        Assert.True(file.Bars[2].IsSeparator);
        Assert.Equal(("CTRL+N", "Ctrl+N", "Create a record"), (file.Bars[0].KeyName, file.Bars[0].KeyText, file.Bars[0].Message));
        Assert.Equal("Paste", menus.Popup("edit")!.Bar("_MED_PASTE")!.Caption); // system bar with its default prompt
        var o = Run("? CNTBAR('file'), CNTPAD('_MSYSMENU'), GETPAD('_MSYSMENU', 2), PRMBAR('file', 4), SKPBAR('file', 2), GETBAR('edit', 1)");
        Assert.Equal("4          2 _EDIT Exit .T. _MED_COPY", System.Text.RegularExpressions.Regex.Replace(o.Trim(), @"\s+", " ").Replace("4 2", "4          2"));
    }

    [Fact]
    public void Selecting_items_runs_their_commands_and_sets_the_menu_functions()
    {
        Run("PUBLIC gcLog, glCanDelete\ngcLog = \"\"\nglCanDelete = .F.\n" + Mpr);
        Rt.SelectMenuItem("file", "1", isPad: false);
        Rt.SelectMenuItem("file", "2", isPad: false); // skipped: SKIP FOR !glCanDelete
        Rt.ExecuteCommand("glCanDelete = .T.");
        Rt.SelectMenuItem("file", "2", isPad: false);
        Rt.SelectMenuItem("file", "4", isPad: false);
        Rt.SelectMenuItem("edit", "_MED_COPY", isPad: false); // no bar command: the popup's ON SELECTION POPUP
        Assert.Equal("new;delete;exit:Exit;edit:_MED_COPY;", Rt.Evaluate("gcLog").AsString);
        var o = Run("? POPUP(), BAR(), PAD(), MENU()");
        Assert.Equal("EDIT _MED_COPY _EDIT _MSYSMENU", o.Trim());
    }

    [Fact]
    public void Marks_skip_release_and_restoring_the_default_menu()
    {
        Run(Mpr + """

            SET MARK OF BAR 1 OF file TO .T.
            SET SKIP OF PAD _edit OF _MSYSMENU .T.
            """);
        Assert.True(Rt.Menus.Popup("file")!.Bars[0].Mark);
        Assert.True(Rt.Evaluate("MRKBAR('file', 1)").AsBool);
        Assert.True(Rt.Evaluate("SKPPAD('_MSYSMENU', '_edit')").AsBool);
        Run("RELEASE BAR 3 OF file\nRELEASE PAD _edit OF _MSYSMENU");
        Assert.Equal(3, Rt.Menus.Popup("file")!.Bars.Count);
        Assert.Single(Rt.Menus.Menus["_MSYSMENU"].Pads);
        Run("RELEASE POPUPS file");
        Assert.Null(Rt.Menus.Popup("file"));
        Run("SET SYSMENU TO DEFAULT");
        Assert.Null(Rt.Menus.ActiveMenu);
    }

    [Fact]
    public void User_menus_and_shortcut_popups()
    {
        Run("""
            DEFINE MENU main BAR
            DEFINE PAD pReports OF main PROMPT "\<Reports"
            DEFINE PAD pQuit OF main PROMPT "\<Quit" AFTER pReports
            DEFINE PAD pFirst OF main PROMPT "First" BEFORE pReports
            ON SELECTION PAD pQuit OF main gcDone = "quit"
            ACTIVATE MENU main NOWAIT
            DEFINE POPUP ctx SHORTCUT RELATIVE FROM MROW(), MCOL()
            DEFINE BAR 1 OF ctx PROMPT "Open"
            DEFINE BAR 2 OF ctx PROMPT "Rename"
            DEFINE BAR 1 OF ctx PROMPT "Open it" BEFORE 2
            """);
        Assert.Equal("main", Rt.Menus.ActiveMenu);
        Assert.Equal(["pFirst", "pReports", "pQuit"], Rt.Menus.Menus["main"].Pads.Select(p => p.Name));
        Assert.True(Rt.Menus.Popup("ctx")!.Shortcut);
        Assert.Equal(["Open it", "Rename"], Rt.Menus.Popup("ctx")!.Bars.Select(b => b.Caption));
        Rt.ExecuteCommand("PUBLIC gcDone");
        Rt.SelectMenuItem("main", "pQuit", isPad: true);
        Assert.Equal("quit", Rt.Evaluate("gcDone").AsString);
        Run("ACTIVATE POPUP ctx"); // no UI: reported, not an error
        Assert.Contains(StatusMessages, m => m.Contains("ACTIVATE POPUP ctx"));
    }

    [Fact]
    public void Do_runs_jpmenu_files_and_falls_back_from_mpr_to_the_menu_definition()
    {
        JoePro.Tests.Documents.MenuDocumentTests.Sample().Save(Path.Combine(Dir, "main.jpmenu"));
        Run("DO main.mpr"); // no main.mpr: main.jpmenu runs
        var sys = Rt.Menus.Menus["_MSYSMENU"];
        Assert.Equal(["_file", "_edit", "_help"], sys.Pads.Select(p => p.Name));
        Assert.Equal("ready;", Rt.Evaluate("gcLog").AsString);
        Rt.SelectMenuItem("file", "4", isPad: false); // a procedure item
        Rt.SelectMenuItem("recent", "1", isPad: false);
        Rt.SelectMenuItem("_MSYSMENU", "_help", isPad: true);
        Assert.Equal("ready;exit;Exit;one;help;", Rt.Evaluate("gcLog").AsString);

        JoePro.Tests.Documents.MenuDocumentTests.WriteSampleMnx(Path.Combine(Dir, "legacy.mnx"));
        Run("DO legacy.mpr");
        Assert.Equal(["_file", "_quit"], Rt.Menus.Menus["_MSYSMENU"].Pads.Select(p => p.Name));
        Rt.SelectMenuItem("more", "1", isPad: false);
        Rt.SelectMenuItem("file", "1", isPad: false);
        Assert.Equal("deep;open;", Rt.Evaluate("gcLog").AsString);
    }
}
