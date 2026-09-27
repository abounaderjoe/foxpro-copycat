using JoePro.Runtime.Builtins;

namespace JoePro.Tests.Runtime;

/// <summary>The VFP 9 command coverage matrix, and the commands added by the coverage sweep.</summary>
public class CommandCoverageTests : RuntimeHarness
{
    private static string[] Lines(string o) => o.Trim().Split('\n').Select(l => string.Join(" ", l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).Where(l => l.Length > 0).ToArray();

    [Fact]
    public void At_least_95_percent_of_VFP9_commands_are_supported_or_listed_as_unsupported()
    {
        var results = CommandCoverage.Run();
        var open = results.Where(r => r.Status is not (CommandCoverage.Status.Supported or CommandCoverage.Status.Unsupported)).ToList();
        Assert.True(results.Count - open.Count >= results.Count * 0.95, "Open: " + string.Join("; ", open.Select(r => $"{r.Command}: {r.Status} {r.Detail}")));
        Assert.DoesNotContain(results, r => r.Status == CommandCoverage.Status.Failed);
    }

    [Fact]
    public void Delete_file_erases_and_reports_a_missing_file()
    {
        File.WriteAllText(Path.Combine(Dir, "a.txt"), "x");
        File.WriteAllText(Path.Combine(Dir, "b.tmp"), "x");
        File.WriteAllText(Path.Combine(Dir, "c.tmp"), "x");
        Run("DELETE FILE a.txt\nERASE *.tmp");
        Assert.Empty(Directory.GetFiles(Dir, "*.t*"));
        Assert.Throws<JoePro.Core.VfpException>(() => Run("DELETE FILE nothere.txt"));
    }

    [Fact]
    public void Sort_total_and_join_write_new_tables()
    {
        var o = Run("""
            CREATE CURSOR sales (region C(5), amount N(8,2), qty I)
            INSERT INTO sales VALUES ("west", 10, 1)
            INSERT INTO sales VALUES ("east", 5, 2)
            INSERT INTO sales VALUES ("west", 2.5, 3)
            INSERT INTO sales VALUES ("east", 1, 4)
            SORT TO sorted ON region /D, amount
            USE sorted IN 0
            SELECT sorted
            SCAN
                ?? ALLTRIM(region) + ":" + TRANSFORM(amount) + " "
            ENDSCAN
            ?
            SELECT sales
            SORT TO bytotal ON region
            USE bytotal IN 0
            SELECT bytotal
            TOTAL TO totals ON region FIELDS amount
            USE totals IN 0
            SELECT totals
            SCAN
                ?? ALLTRIM(region) + "=" + TRANSFORM(amount) + "/" + TRANSFORM(qty) + " "
            ENDSCAN
            ?
            CREATE CURSOR regions (code C(5), name C(10))
            INSERT INTO regions VALUES ("west", "Western")
            INSERT INTO regions VALUES ("east", "Eastern")
            SELECT totals
            JOIN WITH regions TO joined FOR region = regions.code FIELDS region, regions.name, amount
            USE joined IN 0
            SELECT joined
            ? RECCOUNT(), FCOUNT(), ALLTRIM(name)
            """);
        var lines = Lines(o);
        Assert.Equal("west:2.50 west:10.00 east:1.00 east:5.00", lines[0]);
        Assert.Equal("east=6.00/2 west=12.50/1", lines[1]);
        Assert.Equal("2 3 Eastern", lines[2]);
    }

    [Fact]
    public void Replace_from_array_memo_files_and_export()
    {
        File.WriteAllText(Path.Combine(Dir, "note.txt"), "from file");
        var o = Run("""
            CREATE CURSOR t (a I, b C(5), m M)
            APPEND BLANK
            DIMENSION arr[2]
            arr[1] = 7
            arr[2] = "seven"
            REPLACE FROM ARRAY arr
            APPEND MEMO m FROM note.txt
            APPEND MEMO m FROM note.txt
            COPY MEMO m TO out.txt
            ? a, b, m
            APPEND MEMO m FROM note.txt OVERWRITE
            ? m
            EXPORT TO sheet TYPE XLS
            """);
        var lines = Lines(o);
        Assert.Equal("7 seven from filefrom file", lines[0]);
        Assert.Equal("from file", lines[1]);
        Assert.Equal("from filefrom file", File.ReadAllText(Path.Combine(Dir, "out.txt")));
        var xls = File.ReadAllText(Path.Combine(Dir, "sheet.xls"));
        Assert.Contains("urn:schemas-microsoft-com:office:spreadsheet", xls);
        Assert.Contains(">seven<", xls);
    }

    [Fact]
    public void Save_to_and_restore_from_memory_variable_files()
    {
        var o = Run("""
            PUBLIC gName
            gName = "Ann"
            nCount = 42
            dWhen = {^2024-05-06}
            DIMENSION aList[2]
            aList[1] = "x"
            aList[2] = .T.
            SAVE TO vars
            SAVE TO onlyn ALL LIKE n*
            RELEASE ALL
            RESTORE FROM vars
            ? gName, nCount, dWhen = {^2024-05-06}, aList[1], aList[2]
            RESTORE FROM onlyn
            ? TYPE("gName"), nCount
            """);
        var lines = Lines(o);
        Assert.Equal("Ann 42 .T. x .T.", lines[0]);
        Assert.Equal("U 42", lines[1]);
    }

    [Fact]
    public void Accept_input_and_keyboard()
    {
        var answers = new Queue<string>(["typed text", "6 * 7"]);
        Rt.ReadLine = _ => answers.Dequeue();
        var o = Run("""
            ACCEPT "Name: " TO cName
            INPUT "Number: " TO nNum
            ? cName, nNum
            KEYBOARD "hi{ENTER}"
            ACCEPT TO cKeys
            ? cKeys, LASTKEY()
            KEYBOARD "Z"
            ? INKEY(), INKEY()
            """);
        var lines = Lines(o);
        Assert.Equal("typed text 42", lines[^3]);
        Assert.Equal("hi 13", lines[^2]);
        Assert.Equal("90 0", lines[^1]);
    }

    [Fact]
    public void On_key_label_on_shutdown_and_push_pop_key()
    {
        var o = Run("""
            ON KEY LABEL CTRL+F2 x = "pressed"
            ? ON("KEY", "ctrl+f2")
            PUSH KEY CLEAR
            ? ON("KEY", "CTRL+F2") == ""
            POP KEY
            ? ON("KEY", "CTRL+F2")
            ON SHUTDOWN y = "shutdown ran"
            QUIT
            ? y, ON("SHUTDOWN")
            ON ESCAPE z = 1
            ? ON("ESCAPE")
            """);
        var lines = Lines(o);
        Assert.Equal("x = \"pressed\"", lines[0]);
        Assert.Equal(".T.", lines[1]);
        Assert.Equal("x = \"pressed\"", lines[2]);
        Assert.Equal("shutdown ran y = \"shutdown ran\"", lines[3]);
        Assert.Equal("z = 1", lines[4]);
        Assert.True(Rt.RunKeyLabel("Ctrl+F2"));
        Assert.Equal("pressed", Eval("x"));
        Assert.False(Rt.RunKeyLabel("F9"));
    }

    [Fact]
    public void Release_procedure_and_classlib()
    {
        File.WriteAllText(Path.Combine(Dir, "lib1.prg"), "FUNCTION hello\nRETURN \"hi\"");
        var o = Run("""
            SET PROCEDURE TO lib1
            ? hello()
            RELEASE PROCEDURE lib1
            ? SET("PROCEDURE") == ""
            """);
        Assert.Equal(["hi", ".T."], Lines(o));
    }

    [Fact]
    public void Drop_table_delete_and_pack_database()
    {
        Run("""
            CREATE DATABASE shop
            CREATE TABLE items (id I)
            CREATE TABLE gone (id I)
            CLOSE TABLES
            DROP TABLE gone
            PACK DATABASE
            """);
        Assert.Equal(".T. .F.", Run("? INDBC('items', 'TABLE'), INDBC('gone', 'TABLE')").Trim());
        Run("CLOSE DATABASES ALL\nDELETE DATABASE shop");
        Assert.False(File.Exists(Path.Combine(Dir, "shop.jpdb")));
        Run("CREATE TABLE freet FREE (a I)\nUSE\nDROP TABLE freet");
        Assert.False(File.Exists(Path.Combine(Dir, "freet.jpt")));
    }

    [Fact]
    public void List_memory_status_dir_type_and_run()
    {
        File.WriteAllText(Path.Combine(Dir, "readme.txt"), "line one\nline two");
        var o = Run("""
            cName = "Bob"
            LIST MEMORY LIKE c*
            TYPE readme.txt NUMBER
            DIR *.txt
            LIST STATUS TO FILE status.txt
            """);
        Assert.Contains("CNAME", o);
        Assert.Contains("Priv", o);
        Assert.Contains("\"Bob\"", o);
        Assert.Contains("1: line one", o);
        Assert.Contains("README.TXT", o);
        Assert.Contains("Default directory", File.ReadAllText(Path.Combine(Dir, "status.txt")));
        if (!OperatingSystem.IsWindows())
        {
            var r = Run("RUN echo hello-from-shell");
            Assert.Contains("hello-from-shell", r);
        }
    }

    [Fact]
    public void Push_and_pop_menu_and_build_project_from_programs()
    {
        File.WriteAllText(Path.Combine(Dir, "main.prg"), "? 1");
        var o = Run("""
            DEFINE POPUP pp
            DEFINE BAR 1 OF pp PROMPT "One"
            PUSH POPUP pp
            DEFINE BAR 2 OF pp PROMPT "Two"
            ? CNTBAR("pp")
            POP POPUP pp
            ? CNTBAR("pp")
            BUILD PROJECT app1 FROM main
            """);
        Assert.Equal(["2", "1"], Lines(o));
        var project = JoePro.Documents.Projects.ProjectDocument.Load(Path.Combine(Dir, "app1.jpproj"));
        Assert.Equal("main.prg", project.Main);
    }

    [Fact]
    public void Unsupported_commands_say_why()
    {
        Run("EJECT");
        Assert.Contains(StatusMessages, m => m.Contains("EJECT is not supported") && m.Contains("REPORT FORM"));
    }
}
