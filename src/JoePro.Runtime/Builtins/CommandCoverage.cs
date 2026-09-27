using JoePro.Core;
using JoePro.Language;

namespace JoePro.Runtime.Builtins;

/// <summary>
/// The VFP 9 command coverage matrix: each command runs a representative sample in a scratch folder with a small
/// database, and is classified by what happens.
/// </summary>
public static class CommandCoverage
{
    public enum Status { Supported, Unsupported, Ignored, NotSupported, Unrecognized, Failed }

    public sealed record Result(string Command, Status Status, string? Detail);

    private const string Setup = """
        CREATE DATABASE covdb
        CREATE TABLE t1 (id I, name C(10), notes M, amount N(8,2))
        INSERT INTO t1 VALUES (1, "a", "memo", 10)
        INSERT INTO t1 VALUES (2, "b", "", 20)
        INDEX ON id TAG id
        CREATE TABLE t2 (pid I, x C(5))
        INSERT INTO t2 VALUES (1, "x")
        INDEX ON pid TAG pid
        CREATE TABLE t3 FREE (id I, b C(5))
        CLOSE TABLES ALL
        USE t1
        SET ORDER TO id
        GO TOP
        STRTOFILE("x = 1", "prog1.prg")
        STRTOFILE("hello", "file1.txt")
        """;

    /// <summary>A representative use of each command (the key is the name ALANGUAGE and the help use).</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["?"] = "? 1", ["??"] = "?? 1", ["???"] = "??? \"\"",
        ["@...BOX"] = "@ 1,1 TO 5,5", ["@...CLEAR"] = "@ 1,1 CLEAR", ["@...EDIT"] = "@ 1,1 EDIT m.x SIZE 2,10", ["@...FILL"] = "@ 1,1 FILL TO 2,2",
        ["@...GET"] = "x = 1\n@ 1,1 GET x", ["@...MENU"] = "@ 1,1 MENU a, 1", ["@...PROMPT"] = "@ 1,1 PROMPT \"x\"", ["@...SAY"] = "@ 1,1 SAY \"x\"",
        ["@...SCROLL"] = "@ 1,1,5,5 SCROLL", ["@...TO"] = "@ 1,1 TO 5,5 DOUBLE",
        ["ACCEPT"] = "ACCEPT \"x\" TO y", ["ACTIVATE MENU"] = "DEFINE MENU m1\nDEFINE PAD p1 OF m1 PROMPT \"x\"\nACTIVATE MENU m1 NOWAIT",
        ["ACTIVATE POPUP"] = "DEFINE POPUP pp\nDEFINE BAR 1 OF pp PROMPT \"x\"\nACTIVATE POPUP pp NOWAIT", ["ACTIVATE SCREEN"] = "ACTIVATE SCREEN",
        ["ACTIVATE WINDOW"] = "DEFINE WINDOW w1 FROM 1,1 TO 10,40\nACTIVATE WINDOW w1", ["ADD CLASS"] = "CREATE CLASS c1 OF lib1 AS Custom\nADD CLASS c1 OF lib1 TO lib2",
        ["ADD TABLE"] = "ADD TABLE t3", ["ALTER TABLE"] = "ALTER TABLE t2 ADD COLUMN y I",
        ["APPEND"] = "APPEND BLANK", ["APPEND FROM"] = "COPY TO t2copy\nAPPEND FROM t2copy", ["APPEND FROM ARRAY"] = "DIMENSION a[1,2]\na[1,1] = 3\na[1,2] = \"z\"\nUSE t2\nAPPEND FROM ARRAY a",
        ["APPEND GENERAL"] = "CREATE CURSOR g (p G)\nAPPEND BLANK\nAPPEND GENERAL p FROM file1.txt", ["APPEND MEMO"] = "APPEND MEMO notes FROM file1.txt",
        ["APPEND PROCEDURES"] = "APPEND PROCEDURES FROM prog1.prg", ["ASSERT"] = "SET ASSERTS OFF\nASSERT .T.", ["ASSIST"] = "ASSIST",
        ["AVERAGE"] = "AVERAGE amount TO x", ["BEGIN TRANSACTION"] = "BEGIN TRANSACTION\nROLLBACK", ["BLANK"] = "BLANK FIELDS name",
        ["BROWSE"] = "BROWSE NOWAIT", ["BUILD APP"] = "BUILD PROJECT p1 FROM prog1\nBUILD APP p1 FROM p1", ["BUILD DLL"] = "BUILD PROJECT p1 FROM prog1\nBUILD DLL d1 FROM p1",
        ["BUILD EXE"] = "BUILD PROJECT p1 FROM prog1\nBUILD EXE p1 FROM p1", ["BUILD MTDLL"] = "BUILD PROJECT p1 FROM prog1\nBUILD MTDLL d1 FROM p1", ["BUILD PROJECT"] = "BUILD PROJECT p1 FROM prog1",
        ["CALCULATE"] = "CALCULATE SUM(amount) TO x", ["CANCEL"] = "", ["CD"] = "MD sub1\nCD sub1", ["CHANGE"] = "CHANGE NOWAIT", ["CLEAR"] = "CLEAR",
        ["CLOSE"] = "CLOSE ALL", ["CLOSE MEMO"] = "CLOSE MEMO ALL", ["CLOSE TABLES"] = "CLOSE TABLES", ["COMPILE"] = "COMPILE prog1.prg",
        ["COMPILE DATABASE"] = "COMPILE DATABASE covdb", ["CONTINUE"] = "LOCATE FOR id > 0\nCONTINUE", ["COPY FILE"] = "COPY FILE file1.txt TO file2.txt",
        ["COPY INDEXES"] = "COPY INDEXES ALL TO x", ["COPY MEMO"] = "COPY MEMO notes TO memo1.txt", ["COPY PROCEDURES"] = "COPY PROCEDURES TO procs1.prg",
        ["COPY STRUCTURE"] = "COPY STRUCTURE TO t4", ["COPY STRUCTURE EXTENDED"] = "COPY STRUCTURE EXTENDED TO t5", ["COPY TAG"] = "COPY TAG id TO x",
        ["COPY TO"] = "COPY TO t6", ["COPY TO ARRAY"] = "COPY TO ARRAY a1", ["COUNT"] = "COUNT TO n",
        ["CREATE"] = "CREATE t7", ["CREATE CLASS"] = "CREATE CLASS c2 OF lib3 AS Custom", ["CREATE CLASSLIB"] = "CREATE CLASSLIB lib4",
        ["CREATE COLOR SET"] = "CREATE COLOR SET cs1", ["CREATE CONNECTION"] = "CREATE CONNECTION cn1 CONNSTRING \"Driver=x\"", ["CREATE CURSOR"] = "CREATE CURSOR c1 (a I)",
        ["CREATE DATABASE"] = "CREATE DATABASE db2", ["CREATE FORM"] = "CREATE FORM f1", ["CREATE FROM"] = "COPY STRUCTURE EXTENDED TO se1\nCREATE t8 FROM se1",
        ["CREATE LABEL"] = "CREATE LABEL l1", ["CREATE MENU"] = "CREATE MENU m2", ["CREATE PROJECT"] = "CREATE PROJECT p2", ["CREATE QUERY"] = "CREATE QUERY q1",
        ["CREATE REPORT"] = "CREATE REPORT r1", ["CREATE SQL VIEW"] = "CREATE SQL VIEW v1 AS SELECT * FROM t1", ["CREATE TABLE"] = "CREATE TABLE t9 (a I)",
        ["CREATE TRIGGER"] = "CREATE TRIGGER ON t1 FOR INSERT AS .T.", ["DEACTIVATE MENU"] = "DEFINE MENU m1\nDEACTIVATE MENU m1", ["DEACTIVATE POPUP"] = "DEFINE POPUP pp\nDEACTIVATE POPUP pp",
        ["DEACTIVATE WINDOW"] = "DEFINE WINDOW w1 FROM 1,1 TO 10,40\nDEACTIVATE WINDOW w1", ["DEBUG"] = "DEBUG", ["DEBUGOUT"] = "DEBUGOUT \"x\"",
        ["DECLARE"] = "DECLARE a[3]", ["DEFINE BAR"] = "DEFINE POPUP pp\nDEFINE BAR 1 OF pp PROMPT \"x\"", ["DEFINE BOX"] = "DEFINE BOX FROM 1 TO 10 HEIGHT 5",
        ["DEFINE CLASS"] = "RETURN\nDEFINE CLASS k1 AS Custom\nENDDEFINE", ["DEFINE MENU"] = "DEFINE MENU m1", ["DEFINE PAD"] = "DEFINE MENU m1\nDEFINE PAD p1 OF m1 PROMPT \"x\"",
        ["DEFINE POPUP"] = "DEFINE POPUP pp", ["DEFINE WINDOW"] = "DEFINE WINDOW w1 FROM 1,1 TO 10,40", ["DELETE"] = "DELETE", ["DELETE CONNECTION"] = "CREATE CONNECTION cn1 CONNSTRING \"x\"\nDELETE CONNECTION cn1",
        ["DELETE DATABASE"] = "CREATE DATABASE db3\nCLOSE DATABASES\nDELETE DATABASE db3", ["DELETE FILE"] = "DELETE FILE file1.txt", ["DELETE TAG"] = "DELETE TAG id",
        ["DELETE TRIGGER"] = "DELETE TRIGGER ON t1 FOR INSERT", ["DELETE VIEW"] = "CREATE SQL VIEW v2 AS SELECT * FROM t1\nDELETE VIEW v2", ["DIMENSION"] = "DIMENSION a[2]",
        ["DIR"] = "DIR", ["DISPLAY"] = "DISPLAY", ["DISPLAY CONNECTIONS"] = "DISPLAY CONNECTIONS", ["DISPLAY DATABASE"] = "DISPLAY DATABASE", ["DISPLAY DLLS"] = "DISPLAY DLLS",
        ["DISPLAY FILES"] = "DISPLAY FILES", ["DISPLAY MEMORY"] = "DISPLAY MEMORY", ["DISPLAY OBJECTS"] = "DISPLAY OBJECTS", ["DISPLAY PROCEDURES"] = "DISPLAY PROCEDURES",
        ["DISPLAY STATUS"] = "DISPLAY STATUS", ["DISPLAY STRUCTURE"] = "DISPLAY STRUCTURE", ["DISPLAY TABLES"] = "DISPLAY TABLES", ["DISPLAY VIEWS"] = "DISPLAY VIEWS",
        ["DO"] = "DO prog1", ["DO CASE"] = "DO CASE\nCASE .T.\nENDCASE", ["DO FORM"] = "", ["DO WHILE"] = "DO WHILE .F.\nENDDO", ["DOEVENTS"] = "DOEVENTS",
        ["DROP TABLE"] = "DROP TABLE t2", ["DROP VIEW"] = "CREATE SQL VIEW v3 AS SELECT * FROM t1\nDROP VIEW v3", ["EDIT"] = "EDIT NOWAIT", ["EJECT"] = "EJECT",
        ["EJECT PAGE"] = "EJECT PAGE", ["END TRANSACTION"] = "BEGIN TRANSACTION\nEND TRANSACTION", ["ERASE"] = "ERASE file1.txt", ["ERROR"] = "TRY\nERROR 1\nCATCH\nENDTRY",
        ["EXIT"] = "DO WHILE .T.\nEXIT\nENDDO", ["EXPORT"] = "EXPORT TO x1 TYPE XLS", ["EXTERNAL"] = "EXTERNAL PROCEDURE x", ["FLUSH"] = "FLUSH", ["FOR"] = "FOR i = 1 TO 2\nENDFOR",
        ["FOR EACH"] = "DIMENSION a[2]\nFOR EACH x IN a\nENDFOR", ["FREE TABLE"] = "CLOSE ALL\nFREE TABLE t3", ["FUNCTION"] = "RETURN\nFUNCTION f1\nENDFUNC",
        ["GATHER"] = "SCATTER MEMVAR\nGATHER MEMVAR", ["GETEXPR"] = "GETEXPR TO x", ["GO"] = "GO BOTTOM", ["HELP"] = "HELP", ["HIDE MENU"] = "DEFINE MENU m1\nHIDE MENU m1",
        ["HIDE POPUP"] = "DEFINE POPUP pp\nHIDE POPUP pp", ["HIDE WINDOW"] = "DEFINE WINDOW w1 FROM 1,1 TO 10,40\nHIDE WINDOW w1", ["IF"] = "IF .T.\nENDIF",
        ["IMPORT"] = "IMPORT FROM x.dbf", ["INDEX"] = "INDEX ON name TAG name", ["INPUT"] = "INPUT \"x\" TO y", ["INSERT"] = "INSERT BLANK",
        ["INSERT INTO"] = "INSERT INTO t2 VALUES (5, \"y\")", ["JOIN"] = "SELECT 0\nUSE t2 ALIAS j2\nSELECT t1\nJOIN WITH j2 TO t10 FOR id = j2.pid", ["KEYBOARD"] = "KEYBOARD \"x\"",
        ["LABEL"] = "LABEL FORM l2 TO PRINTER NOCONSOLE", ["LIST"] = "LIST", ["LIST CONNECTIONS"] = "LIST CONNECTIONS", ["LIST DATABASE"] = "LIST DATABASE", ["LIST DLLS"] = "LIST DLLS",
        ["LIST FILES"] = "LIST FILES", ["LIST MEMORY"] = "LIST MEMORY", ["LIST OBJECTS"] = "LIST OBJECTS", ["LIST PROCEDURES"] = "LIST PROCEDURES", ["LIST STATUS"] = "LIST STATUS",
        ["LIST STRUCTURE"] = "LIST STRUCTURE", ["LIST TABLES"] = "LIST TABLES", ["LIST VIEWS"] = "LIST VIEWS", ["LOAD"] = "LOAD x", ["LOCAL"] = "LOCAL x",
        ["LOCATE"] = "LOCATE FOR id = 2", ["LPARAMETERS"] = "LPARAMETERS a", ["MD"] = "MD sub2", ["MENU"] = "MENU BAR a, 1", ["MENU TO"] = "MENU TO x",
        ["MODIFY CLASS"] = "MODIFY CLASS c3 OF lib5", ["MODIFY COMMAND"] = "MODIFY COMMAND prog1 NOWAIT", ["MODIFY CONNECTION"] = "MODIFY CONNECTION", ["MODIFY DATABASE"] = "MODIFY DATABASE",
        ["MODIFY FILE"] = "MODIFY FILE file1.txt NOWAIT", ["MODIFY FORM"] = "MODIFY FORM f2", ["MODIFY GENERAL"] = "MODIFY GENERAL x", ["MODIFY LABEL"] = "MODIFY LABEL l3",
        ["MODIFY MEMO"] = "MODIFY MEMO notes NOWAIT", ["MODIFY MENU"] = "MODIFY MENU m3", ["MODIFY PROCEDURE"] = "MODIFY PROCEDURE", ["MODIFY PROJECT"] = "MODIFY PROJECT p3 NOWAIT",
        ["MODIFY QUERY"] = "MODIFY QUERY q2", ["MODIFY REPORT"] = "MODIFY REPORT r2", ["MODIFY STRUCTURE"] = "MODIFY STRUCTURE", ["MODIFY VIEW"] = "CREATE SQL VIEW v4 AS SELECT * FROM t1\nMODIFY VIEW v4",
        ["MODIFY WINDOW"] = "MODIFY WINDOW SCREEN TITLE \"x\"", ["MOUSE"] = "MOUSE CLICK AT 1,1", ["MOVE POPUP"] = "DEFINE POPUP pp\nMOVE POPUP pp TO 1,1",
        ["MOVE WINDOW"] = "DEFINE WINDOW w1 FROM 1,1 TO 10,40\nMOVE WINDOW w1 TO 2,2", ["NOTE"] = "NOTE a comment", ["ON BAR"] = "DEFINE POPUP pp\nDEFINE BAR 1 OF pp PROMPT \"x\"\nDEFINE POPUP p2\nON BAR 1 OF pp ACTIVATE POPUP p2",
        ["ON ERROR"] = "ON ERROR x = 1\nON ERROR", ["ON ESCAPE"] = "ON ESCAPE x = 1", ["ON EXIT BAR"] = "ON EXIT BAR 1 OF pp x = 1", ["ON EXIT MENU"] = "ON EXIT MENU m1 x = 1",
        ["ON EXIT PAD"] = "ON EXIT PAD p1 OF m1 x = 1", ["ON EXIT POPUP"] = "ON EXIT POPUP pp x = 1", ["ON KEY"] = "ON KEY x = 1", ["ON KEY LABEL"] = "ON KEY LABEL F2 x = 1\nON KEY LABEL F2",
        ["ON PAD"] = "DEFINE MENU m1\nDEFINE PAD p1 OF m1 PROMPT \"x\"\nDEFINE POPUP pp\nON PAD p1 OF m1 ACTIVATE POPUP pp", ["ON PAGE"] = "ON PAGE AT LINE 50 EJECT PAGE",
        ["ON READERROR"] = "ON READERROR x = 1", ["ON SELECTION BAR"] = "DEFINE POPUP pp\nDEFINE BAR 1 OF pp PROMPT \"x\"\nON SELECTION BAR 1 OF pp x = 1",
        ["ON SELECTION MENU"] = "DEFINE MENU m1\nON SELECTION MENU m1 x = 1", ["ON SELECTION PAD"] = "DEFINE MENU m1\nDEFINE PAD p1 OF m1 PROMPT \"x\"\nON SELECTION PAD p1 OF m1 x = 1",
        ["ON SELECTION POPUP"] = "DEFINE POPUP pp\nON SELECTION POPUP pp x = 1", ["ON SHUTDOWN"] = "ON SHUTDOWN x = 1\nON SHUTDOWN", ["OPEN DATABASE"] = "CLOSE DATABASES\nOPEN DATABASE covdb",
        ["PACK"] = "USE t1 EXCLUSIVE\nDELETE FOR id = 2\nPACK", ["PACK DATABASE"] = "CLOSE ALL\nOPEN DATABASE covdb EXCLUSIVE\nPACK DATABASE", ["PARAMETERS"] = "PARAMETERS a",
        ["PLAY MACRO"] = "PLAY MACRO x", ["POP KEY"] = "PUSH KEY\nPOP KEY", ["POP MENU"] = "PUSH MENU _MSYSMENU\nPOP MENU _MSYSMENU", ["POP POPUP"] = "DEFINE POPUP pp\nPUSH POPUP pp\nPOP POPUP pp",
        ["PRINTJOB"] = "PRINTJOB\nENDPRINTJOB", ["PRIVATE"] = "PRIVATE x", ["PROCEDURE"] = "RETURN\nPROCEDURE p1\nENDPROC", ["PUBLIC"] = "PUBLIC pubx",
        ["PUSH KEY"] = "PUSH KEY CLEAR\nPOP KEY", ["PUSH MENU"] = "PUSH MENU _MSYSMENU\nPOP MENU _MSYSMENU", ["PUSH POPUP"] = "DEFINE POPUP pp\nPUSH POPUP pp", ["QUIT"] = "",
        ["RD"] = "MD sub3\nRD sub3", ["READ"] = "READ", ["READ EVENTS"] = "", ["READ MENU"] = "READ MENU", ["RECALL"] = "DELETE\nRECALL", ["REINDEX"] = "REINDEX",
        ["RELEASE"] = "x = 1\nRELEASE x", ["RELEASE BAR"] = "DEFINE POPUP pp\nDEFINE BAR 1 OF pp PROMPT \"x\"\nRELEASE BAR 1 OF pp", ["RELEASE CLASSLIB"] = "CREATE CLASSLIB lib6\nSET CLASSLIB TO lib6\nRELEASE CLASSLIB lib6",
        ["RELEASE LIBRARY"] = "RELEASE LIBRARY x.fll", ["RELEASE MENUS"] = "DEFINE MENU m1\nRELEASE MENUS m1", ["RELEASE PAD"] = "DEFINE MENU m1\nDEFINE PAD p1 OF m1 PROMPT \"x\"\nRELEASE PAD p1 OF m1",
        ["RELEASE POPUPS"] = "DEFINE POPUP pp\nRELEASE POPUPS pp", ["RELEASE PROCEDURE"] = "SET PROCEDURE TO prog1\nRELEASE PROCEDURE prog1", ["RELEASE WINDOWS"] = "DEFINE WINDOW w1 FROM 1,1 TO 10,40\nRELEASE WINDOWS w1",
        ["REMOVE CLASS"] = "CREATE CLASS c4 OF lib7 AS Custom\nREMOVE CLASS c4 OF lib7", ["REMOVE TABLE"] = "REMOVE TABLE t2", ["RENAME"] = "RENAME file1.txt TO file3.txt",
        ["RENAME CLASS"] = "CREATE CLASS c5 OF lib8 AS Custom\nRENAME CLASS c5 OF lib8 TO c6", ["RENAME CONNECTION"] = "CREATE CONNECTION cn2 CONNSTRING \"x\"\nRENAME CONNECTION cn2 TO cn3",
        ["RENAME TABLE"] = "RENAME TABLE t2 TO t2b", ["RENAME VIEW"] = "CREATE SQL VIEW v5 AS SELECT * FROM t1\nRENAME VIEW v5 TO v6", ["REPLACE"] = "REPLACE name WITH \"z\"",
        ["REPLACE FROM ARRAY"] = "DIMENSION a[2]\na[1] = 9\na[2] = \"n\"\nREPLACE FROM ARRAY a", ["REPORT FORM"] = "", ["RESTORE FROM"] = "x = 1\nSAVE TO mem1\nRESTORE FROM mem1",
        ["RESTORE MACROS"] = "RESTORE MACROS", ["RESTORE SCREEN"] = "SAVE SCREEN\nRESTORE SCREEN", ["RESTORE WINDOW"] = "RESTORE WINDOW ALL FROM x", ["RESUME"] = "RESUME",
        ["RETRY"] = "", ["RETURN"] = "RETURN", ["ROLLBACK"] = "BEGIN TRANSACTION\nROLLBACK", ["RUN"] = "RUN echo", ["SAVE MACROS"] = "SAVE MACROS TO x",
        ["SAVE SCREEN"] = "SAVE SCREEN", ["SAVE TO"] = "x = 1\nSAVE TO mem2", ["SAVE WINDOWS"] = "SAVE WINDOWS ALL TO x", ["SCAN"] = "SCAN\nENDSCAN",
        ["SCATTER"] = "SCATTER MEMVAR", ["SCROLL"] = "SCROLL 1,1,5,5,1", ["SEEK"] = "SEEK 2", ["SELECT"] = "SELECT 0\nUSE t2\nSELECT t1\nSELECT t2", ["SELECT - SQL"] = "SELECT * FROM t1 INTO CURSOR q",
        ["SET"] = "SET EXACT ON", ["SET CLASSLIB"] = "CREATE CLASSLIB lib9\nSET CLASSLIB TO lib9", ["SET DATABASE"] = "SET DATABASE TO covdb", ["SET DATASESSION"] = "SET DATASESSION TO 1",
        ["SET DEFAULT"] = "MD sub4\nSET DEFAULT TO sub4", ["SET FILTER"] = "SET FILTER TO id > 1", ["SET INDEX"] = "SET INDEX TO", ["SET LIBRARY"] = "SET LIBRARY TO",
        ["SET ORDER"] = "SET ORDER TO id", ["SET PATH"] = "SET PATH TO sub", ["SET PROCEDURE"] = "SET PROCEDURE TO prog1", ["SET RELATION"] = "SELECT 0\nUSE t2\nSELECT t1\nSET RELATION TO id INTO t2",
        ["SET SKIP"] = "SELECT 0\nUSE t2\nSELECT t1\nSET RELATION TO id INTO t2\nSET SKIP TO t2", ["SHOW GET"] = "SHOW GET x", ["SHOW GETS"] = "SHOW GETS",
        ["SHOW MENU"] = "DEFINE MENU m1\nSHOW MENU m1", ["SHOW OBJECT"] = "SHOW OBJECT 1", ["SHOW POPUP"] = "DEFINE POPUP pp\nSHOW POPUP pp",
        ["SHOW WINDOW"] = "DEFINE WINDOW w1 FROM 1,1 TO 10,40\nSHOW WINDOW w1", ["SIZE POPUP"] = "DEFINE POPUP pp\nSIZE POPUP pp TO 5,5",
        ["SIZE WINDOW"] = "DEFINE WINDOW w1 FROM 1,1 TO 10,40\nSIZE WINDOW w1 TO 5,5", ["SKIP"] = "SKIP", ["SORT"] = "SORT TO t11 ON name", ["STORE"] = "STORE 1 TO x",
        ["SUM"] = "SUM amount TO x", ["SUSPEND"] = "SUSPEND", ["SYS"] = "x = SYS(2015)", ["TEXT"] = "TEXT TO x NOSHOW\nabc\nENDTEXT",
        ["TOTAL"] = "TOTAL TO t12 ON id FIELDS amount", ["TYPE"] = "TYPE file1.txt", ["UNLOCK"] = "UNLOCK ALL", ["UPDATE"] = "UPDATE t1 SET name = \"q\" WHERE id = 1",
        ["USE"] = "USE t2 IN 0", ["VALIDATE DATABASE"] = "CLOSE ALL\nOPEN DATABASE covdb EXCLUSIVE\nVALIDATE DATABASE", ["WAIT"] = "WAIT \"x\" WINDOW NOWAIT",
        ["WITH"] = "o = CREATEOBJECT(\"Custom\")\nWITH o\nENDWITH", ["ZAP"] = "USE t2 EXCLUSIVE\nZAP", ["ZOOM WINDOW"] = "ZOOM WINDOW SCREEN MAX",
    };

    /// <summary>Commands deliberately not supported, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> Unsupported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["???"] = Printer, ["EJECT"] = Printer, ["EJECT PAGE"] = Printer, ["PRINTJOB"] = Printer, ["ON PAGE"] = Printer,
        ["@...BOX"] = Screen, ["@...CLEAR"] = Screen, ["@...EDIT"] = Read, ["@...FILL"] = Screen, ["@...GET"] = Read, ["@...MENU"] = Read,
        ["@...PROMPT"] = Read, ["@...SAY"] = Screen, ["@...SCROLL"] = Screen, ["@...TO"] = Screen, ["SCROLL"] = Screen,
        ["READ"] = Read, ["READ MENU"] = Read, ["MENU"] = Read, ["MENU TO"] = Read, ["SHOW GET"] = Read, ["SHOW GETS"] = Read, ["SHOW OBJECT"] = Read,
        ["ON READERROR"] = Read, ["DEFINE BOX"] = Printer,
        ["DEFINE WINDOW"] = Win, ["ACTIVATE WINDOW"] = Win, ["DEACTIVATE WINDOW"] = Win, ["HIDE WINDOW"] = Win, ["SHOW WINDOW"] = Win,
        ["MOVE WINDOW"] = Win, ["SIZE WINDOW"] = Win, ["RELEASE WINDOWS"] = Win, ["RESTORE WINDOW"] = Win, ["SAVE WINDOWS"] = Win,
        ["SAVE SCREEN"] = Screen, ["RESTORE SCREEN"] = Screen,
        ["MOVE POPUP"] = "Popups are placed by the operating system's menu system; position them with ACTIVATE POPUP … AT.",
        ["SIZE POPUP"] = "Popups are sized by the operating system's menu system to fit their bars.",
        ["ON EXIT BAR"] = MenuExit, ["ON EXIT MENU"] = MenuExit, ["ON EXIT PAD"] = MenuExit, ["ON EXIT POPUP"] = MenuExit,
        ["ASSIST"] = "The dBASE-style Assistant was removed from FoxPro long ago.",
        ["CREATE COLOR SET"] = "Color sets belong to character-mode color schemes; forms use ForeColor and BackColor.",
        ["COPY INDEXES"] = Index, ["COPY TAG"] = Index,
        ["FREE TABLE"] = "Joe Pro tables carry no backlink to a database, so there is nothing to free; REMOVE TABLE detaches a table.",
        ["APPEND GENERAL"] = Ole, ["MODIFY GENERAL"] = Ole,
        ["LOAD"] = "Binary .BIN modules (LOAD/CALL) are 16-bit code; use .NET assemblies through CREATEOBJECT(\"net:…\").",
        ["PLAY MACRO"] = Macro, ["SAVE MACROS"] = Macro, ["RESTORE MACROS"] = Macro,
        ["MOUSE"] = "Simulated mouse input is not provided; drive forms through their methods (Click(), SetFocus()) in tests.",
        ["DISPLAY DLLS"] = Dll, ["LIST DLLS"] = Dll,
    };

    private const string Printer = "Direct printer streaming (???, EJECT, PRINTJOB, ON PAGE, SET PRINTER) is not supported; print through REPORT FORM.";
    private const string Screen = "Character-mode screen drawing is not supported; use forms and their controls.";
    private const string Read = "The character-mode @…GET / READ loop is not supported; use forms and their controls' events.";
    private const string Win = "User-defined DEFINE WINDOW windows are not supported; use forms.";
    private const string MenuExit = "Menu exit events are not raised; put the code in the menu's selection handlers.";
    private const string Index = "Index files are part of each table in Joe Pro; there are no separate .IDX/.CDX files to copy.";
    private const string Ole = "General fields hold OLE objects, which need Windows COM; store files in Blob fields instead.";
    private const string Macro = "Keyboard macros are not supported; use ON KEY LABEL or a program.";
    private const string Dll = "Win32 DLL declarations (DECLARE … IN) are not available in the cross-platform runtime.";

    /// <summary>The reason a command verb is unsupported, for the runtime's notice.</summary>
    public static string ReasonFor(string verb) =>
        Unsupported.FirstOrDefault(kv => kv.Key.StartsWith(verb, StringComparison.OrdinalIgnoreCase)).Value ?? "see the command coverage matrix.";

    /// <summary>Commands that end the program or wait for the user; they are covered by their own tests.</summary>
    private static readonly HashSet<string> Interactive = new(StringComparer.OrdinalIgnoreCase)
        { "CANCEL", "QUIT", "READ EVENTS", "RETRY", "DO FORM", "REPORT FORM", "LABEL", "IMPORT" };

    /// <summary>Statements the parser keeps as NoOpStmt that the interpreter nevertheless carries out.</summary>
    private static readonly HashSet<string> HandledNoOps = new(StringComparer.OrdinalIgnoreCase) { "SUSPEND", "RESUME" };

    public static List<Result> Run()
    {
        var results = new List<Result>();
        foreach (var cmd in VfpCatalog.Commands)
        {
            if (Unsupported.TryGetValue(cmd, out var why)) { results.Add(new(cmd, Status.Unsupported, why)); continue; }
            if (Interactive.Contains(cmd)) { results.Add(new(cmd, Status.Supported, "covered by tests")); continue; }
            if (!Samples.TryGetValue(cmd, out var sample)) { results.Add(new(cmd, Status.Failed, "no sample")); continue; }
            results.Add(RunSample(cmd, sample));
        }
        return results;
    }

    private static Result RunSample(string cmd, string sample)
    {
        var dir = Path.Combine(Path.GetTempPath(), "joepro-cov-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var rt = new Interpreter(new TextWriterOutput(TextWriter.Null), dir) { Ui = new DesignerProbe(), ReadLine = _ => "1" };
        try
        {
            rt.ExecuteCommand("SET TALK OFF\n" + Setup);
            ProgramUnit unit;
            try { unit = Parser.ParseProgram(sample, "sample"); }
            catch (CompileException ex) when (ex.Message.Contains("Unrecognized command verb")) { return new(cmd, Status.Unrecognized, ex.Message); }
            catch (CompileException ex) { return new(cmd, Status.Failed, ex.Message); }
            var noop = unit.Main.OfType<NoOpStmt>().FirstOrDefault(n => !HandledNoOps.Contains(n.Verb));
            if (noop != null) return new(cmd, Status.Ignored, noop.Verb);
            var notices = new List<string>();
            rt.Status += notices.Add;
            try { rt.ExecuteCommand(sample); }
            catch (CompileException ex) { return new(cmd, Status.Failed, ex.Message); }
            catch (VfpException ex) when (ex.Number == ErrorCodes.FeatureNotAvailable) { return new(cmd, Status.NotSupported, ex.Message); }
            catch (VfpException ex) { return new(cmd, Status.Failed, ex.Message); }
            var skipped = notices.FirstOrDefault(n => n.Contains("not available in this build") || n.Contains("not supported yet"));
            return skipped != null ? new(cmd, Status.Ignored, skipped) : new(cmd, Status.Supported, null);
        }
        catch (VfpException ex) { return new(cmd, Status.Failed, "setup: " + ex.Message); }
        finally
        {
            try { rt.Session.Dispose(); } catch (Exception) { }
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A stand-in for the IDE: designers and the code editor open (as they do in the IDE) for the kinds the IDE provides.</summary>
    private sealed class DesignerProbe : IUiHost
    {
        private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
            { "CLASS", "CLASSLIB", "DATABASE", "FORM", "MENU", "PROCEDURE", "PROJECT", "REPORT", "LABEL", "TABLE", "QUERY", "VIEW", "DEBUGGER" };
        public void PropertyChanged(VfpObject o, string property) { }
        public void Show(VfpObject form, bool modal) { }
        public void Hide(VfpObject form) { }
        public void Release(VfpObject form) { }
        public void Refresh(VfpObject o) { }
        public void SetFocus(VfpObject control) { }
        public void ReadEvents() { }
        public void ClearEvents() { }
        public bool Browse(JoePro.Data.WorkArea area, IReadOnlyList<string>? fields) => true;
        public bool ModifyFile(string path) => true;
        public bool OpenDesigner(DesignerRequest request) => Kinds.Contains(request.Kind);
    }
}
