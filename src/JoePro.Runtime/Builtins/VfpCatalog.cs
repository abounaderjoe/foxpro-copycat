using System.Text;

namespace JoePro.Runtime.Builtins;

/// <summary>
/// The Visual FoxPro 9 functions (from the product's language reference), for the coverage matrix: which Joe Pro
/// implements, and why the others are not.
/// </summary>
public static class VfpCatalog
{
    public static readonly string[] Functions =
    [
        "ABS", "ACLASS", "ACOPY", "ACOS", "ADATABASES", "ADBOBJECTS", "ADDBS", "ADDPROPERTY", "ADEL", "ADIR", "ADLLS", "ADOCKSTATE",
        "AELEMENT", "AERROR", "AEVENTS", "AFIELDS", "AFONT", "AGETCLASS", "AGETFILEVERSION", "AINS", "AINSTANCE", "ALANGUAGE", "ALEN",
        "ALIAS", "ALINES", "ALLTRIM", "AMEMBERS", "AMOUSEOBJ", "ANETRESOURCES", "APRINTERS", "APROCINFO", "ASC", "ASCAN", "ASELOBJ",
        "ASESSIONS", "ASIN", "ASORT", "ASQLHANDLES", "ASTACKINFO", "ASUBSCRIPT", "AT", "AT_C", "ATAGINFO", "ATAN", "ATC", "ATCC",
        "ATCLINE", "ATLINE", "ATN2", "AUSED", "AVCXCLASSES", "BAR", "BETWEEN", "BINDEVENT", "BINTOC", "BITAND", "BITCLEAR",
        "BITLSHIFT", "BITNOT", "BITOR", "BITRSHIFT", "BITSET", "BITTEST", "BITXOR", "BOF", "CANDIDATE", "CAPSLOCK", "CDOW", "CDX",
        "CEILING", "CHR", "CHRSAW", "CHRTRAN", "CHRTRANC", "CLEARRESULTSET", "CMONTH", "CNTBAR", "CNTPAD", "COL", "COMARRAY",
        "COMCLASSINFO", "COMPOBJ", "COMPROP", "COMRETURNERROR", "COS", "CPCONVERT", "CPCURRENT", "CPDBF", "CREATEBINARY",
        "CREATEOBJECT", "CREATEOBJECTEX", "CREATEOFFLINE", "CTOBIN", "CTOD", "CTOT", "CURDIR", "CURSORGETPROP", "CURSORSETPROP",
        "CURSORTOXML", "CURVAL", "DATE", "DATETIME", "DAY", "DBC", "DBF", "DBGETPROP", "DBSETPROP", "DBUSED", "DDEABORTTRANS",
        "DDEADVISE", "DDEENABLED", "DDEEXECUTE", "DDEINITIATE", "DDELASTERROR", "DDEPOKE", "DDEREQUEST", "DDESETOPTION",
        "DDESETSERVICE", "DDESETTOPIC", "DDETERMINATE", "DEFAULTEXT", "DELETED", "DESCENDING", "DIFFERENCE", "DIRECTORY",
        "DISKSPACE", "DISPLAYPATH", "DMY", "DODEFAULT", "DOW", "DRIVETYPE", "DROPOFFLINE", "DTOC", "DTOR", "DTOS", "DTOT",
        "EDITSOURCE", "EMPTY", "EOF", "ERROR", "EVALUATE", "EVENTHANDLER", "EVL", "EXECSCRIPT", "EXP", "FCHSIZE", "FCLOSE", "FCOUNT",
        "FCREATE", "FDATE", "FEOF", "FERROR", "FFLUSH", "FGETS", "FIELD", "FILE", "FILETOSTR", "FILTER", "FKLABEL", "FKMAX", "FLDLIST",
        "FLOCK", "FLOOR", "FONTMETRIC", "FOPEN", "FOR", "FORCEEXT", "FORCEPATH", "FOUND", "FPUTS", "FREAD", "FSEEK", "FSIZE", "FTIME",
        "FULLPATH", "FV", "FWRITE", "GETAUTOINCVALUE", "GETBAR", "GETCOLOR", "GETCP", "GETCURSORADAPTER", "GETDIR", "GETENV",
        "GETFILE", "GETFLDSTATE", "GETFONT", "GETINTERFACE", "GETNEXTMODIFIED", "GETOBJECT", "GETPAD", "GETPEM", "GETPICT",
        "GETPRINTER", "GETRESULTSET", "GETWORDCOUNT", "GETWORDNUM", "GOMONTH", "HEADER", "HOME", "HOUR", "ICASE", "IDXCOLLATE", "IIF",
        "IMESTATUS", "INDBC", "INDEXSEEK", "INKEY", "INLIST", "INPUTBOX", "INSMODE", "INT", "ISALPHA", "ISBLANK", "ISCOLOR", "ISDIGIT",
        "ISEXCLUSIVE", "ISFLOCKED", "ISHOSTED", "ISLEADBYTE", "ISLOWER", "ISMEMOFETCHED", "ISMOUSE", "ISNULL", "ISPEN", "ISREADONLY",
        "ISRLOCKED", "ISTRANSACTABLE", "ISUPPER", "JUSTDRIVE", "JUSTEXT", "JUSTFNAME", "JUSTPATH", "JUSTSTEM", "KEY", "KEYMATCH",
        "LASTKEY", "LEFT", "LEFTC", "LEN", "LENC", "LIKE", "LIKEC", "LINENO", "LOADPICTURE", "LOCFILE", "LOCK", "LOG", "LOG10",
        "LOOKUP", "LOWER", "LTRIM", "LUPDATE", "MAKETRANSACTABLE", "MAX", "MCOL", "MDOWN", "MDX", "MDY", "MEMLINES", "MEMORY", "MENU",
        "MESSAGE", "MESSAGEBOX", "MIN", "MINUTE", "MLINE", "MOD", "MONTH", "MRKBAR", "MRKPAD", "MROW", "MTON", "MWINDOW", "NDX",
        "NEWOBJECT", "NODEFAULT", "NORMALIZE", "NTOM", "NUMLOCK", "NVL", "OBJNUM", "OBJTOCLIENT", "OBJVAR", "OCCURS", "OEMTOANSI",
        "OLDVAL", "ON", "ORDER", "OS", "PAD", "PADC", "PADL", "PADR", "PARAMETERS", "PAYMENT", "PCOL", "PCOUNT", "PEMSTATUS", "PI",
        "POPUP", "PRIMARY", "PRINTSTATUS", "PRMBAR", "PRMPAD", "PROGRAM", "PROMPT", "PROPER", "PROW", "PRTINFO", "PUTFILE", "PV",
        "QUARTER", "RAISEEVENT", "RAND", "RAT", "RATC", "RATLINE", "RDLEVEL", "READKEY", "RECCOUNT", "RECNO", "RECSIZE", "REFRESH",
        "RELATION", "REMOVEPROPERTY", "REPLICATE", "REQUERY", "RGB", "RGBSCHEME", "RIGHT", "RIGHTC", "RLOCK", "ROUND", "ROW", "RTOD",
        "RTRIM", "SAVEPICTURE", "SCHEME", "SCOLS", "SEC", "SECONDS", "SEEK", "SELECT", "SET", "SETFLDSTATE", "SETRESULTSET", "SIGN",
        "SIN", "SKPBAR", "SKPPAD", "SOUNDEX", "SPACE", "SQLCANCEL", "SQLCOLUMNS", "SQLCOMMIT", "SQLCONNECT", "SQLDISCONNECT", "SQLEXEC",
        "SQLGETPROP", "SQLIDLEDISCONNECT", "SQLMORERESULTS", "SQLPREPARE", "SQLROLLBACK", "SQLSETPROP", "SQLSTRINGCONNECT",
        "SQLTABLES", "SQRT", "SROWS", "STR", "STRCONV", "STREXTRACT", "STRTOFILE", "STRTRAN", "STUFF", "STUFFC", "SUBSTR", "SUBSTRC",
        "SYS", "SYSMETRIC", "TABLEREVERT", "TABLEUPDATE", "TAG", "TAGCOUNT", "TAGNO", "TAN", "TARGET", "TEXTMERGE", "TIME",
        "TRANSFORM", "TRIM", "TTOC", "TTOD", "TXNLEVEL", "TXTWIDTH", "TYPE", "UNBINDEVENTS", "UNIQUE", "UPDATED", "UPPER", "USED",
        "VAL", "VARREAD", "VARTYPE", "VERSION", "WBORDER", "WCHILD", "WCOLS", "WDOCKABLE", "WEEK", "WEXIST", "WFONT", "WLAST",
        "WLCOL", "WLROW", "WMAXIMUM", "WMINIMUM", "WONTOP", "WOUTPUT", "WPARENT", "WREAD", "WROWS", "WTITLE", "WVISIBLE",
        "XMLTOCURSOR", "XMLUPDATEGRAM", "YEAR",
    ];

    private const string Dde = "Dynamic Data Exchange is a Windows-only interprocess mechanism that the cross-platform runtime does not provide.";
    private const string Com = "COM/ActiveX interop is not available in the cross-platform runtime (a Windows COM bridge is planned).";

    /// <summary>Functions deliberately not implemented, with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> Unsupported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DDEABORTTRANS"] = Dde, ["DDEADVISE"] = Dde, ["DDEENABLED"] = Dde, ["DDEEXECUTE"] = Dde, ["DDEINITIATE"] = Dde, ["DDELASTERROR"] = Dde,
        ["DDEPOKE"] = Dde, ["DDEREQUEST"] = Dde, ["DDESETOPTION"] = Dde, ["DDESETSERVICE"] = Dde, ["DDESETTOPIC"] = Dde, ["DDETERMINATE"] = Dde,
        ["COMARRAY"] = Com, ["COMCLASSINFO"] = Com, ["COMPROP"] = Com, ["COMRETURNERROR"] = Com, ["GETINTERFACE"] = Com, ["EVENTHANDLER"] = Com,
        ["ADLLS"] = "Win32 DLL declarations (DECLARE … IN) are not available in the cross-platform runtime.",
        ["MDX"] = "dBASE .MDX indexes are not supported; tables use Joe Pro index tags.",
        ["NDX"] = "dBASE .NDX indexes are not supported; tables use Joe Pro index tags.",
        ["ISPEN"] = "Pen computing support was removed from Windows.",
        ["IMESTATUS"] = "Input Method Editor status is controlled by the operating system's input settings.",
        ["LOADPICTURE"] = "OLE picture objects are Windows COM objects; use Image controls' Picture property instead.",
        ["SAVEPICTURE"] = "OLE picture objects are Windows COM objects; use Image controls' Picture property instead.",
        ["CREATEOFFLINE"] = "Offline views were superseded; use the Data Server or local views.",
        ["DROPOFFLINE"] = "Offline views were superseded; use the Data Server or local views.",
        ["XMLUPDATEGRAM"] = "SQL Server updategrams are not generated; use TABLEUPDATE against the Data Server or remote views.",
        ["AINSTANCE"] = "Lists running VFP instances through Windows COM.",
        ["ADOCKSTATE"] = "The IDE's docking layout is not exposed to programs.",
        ["EDITSOURCE"] = "Opens the VFP IDE's editor; use MODIFY COMMAND in the Joe Pro IDE.",
        ["OBJNUM"] = "Obsolete @…GET object numbering; forms use object references.",
        ["OBJVAR"] = "Obsolete @…GET object numbering; forms use object references.",
        ["WBORDER"] = Win, ["WCHILD"] = Win, ["WCOLS"] = Win, ["WDOCKABLE"] = Win, ["WFONT"] = Win, ["WLAST"] = Win, ["WLCOL"] = Win,
        ["WLROW"] = Win, ["WMAXIMUM"] = Win, ["WMINIMUM"] = Win, ["WPARENT"] = Win, ["WREAD"] = Win, ["WROWS"] = Win,
        ["READKEY"] = Read, ["UPDATED"] = Read, ["VARREAD"] = Read, ["RDLEVEL"] = Read,
        ["MCOL"] = Mouse, ["MROW"] = Mouse, ["MDOWN"] = Mouse, ["MWINDOW"] = Mouse, ["AMOUSEOBJ"] = Mouse,
        ["SCHEME"] = "Character-mode color schemes (SET COLOR OF SCHEME) are not supported; forms use ForeColor and BackColor.",
        ["RGBSCHEME"] = "Character-mode color schemes (SET COLOR OF SCHEME) are not supported; forms use ForeColor and BackColor.",
        ["FLDLIST"] = "SET FIELDS lists are not supported; name the fields in the command or use a SELECT-SQL field list.",
    };

    private const string Win = "User-defined DEFINE WINDOW windows are not supported; use forms (WEXIST, WONTOP, WOUTPUT and WVISIBLE report no such windows).";
    private const string Read = "The character-mode @…GET / READ loop is not supported; use forms and their controls' events.";
    private const string Mouse = "Character-mode mouse position is not supported; use the nXCoord and nYCoord parameters of the MouseMove, MouseDown and MouseUp events.";

    /// <summary>The Visual FoxPro 9 commands (first keywords, as ALANGUAGE(a, 1) lists them).</summary>
    public static readonly string[] Commands =
    [
        "?", "??", "???", "@...BOX", "@...CLEAR", "@...EDIT", "@...FILL", "@...GET", "@...MENU", "@...PROMPT", "@...SAY", "@...SCROLL", "@...TO",
        "ACCEPT", "ACTIVATE MENU", "ACTIVATE POPUP", "ACTIVATE SCREEN", "ACTIVATE WINDOW", "ADD CLASS", "ADD TABLE", "ALTER TABLE", "APPEND",
        "APPEND FROM", "APPEND FROM ARRAY", "APPEND GENERAL", "APPEND MEMO", "APPEND PROCEDURES", "ASSERT", "ASSIST", "AVERAGE", "BEGIN TRANSACTION",
        "BLANK", "BROWSE", "BUILD APP", "BUILD DLL", "BUILD EXE", "BUILD MTDLL", "BUILD PROJECT", "CALCULATE", "CANCEL", "CD", "CHANGE", "CLEAR",
        "CLOSE", "CLOSE MEMO", "CLOSE TABLES", "COMPILE", "COMPILE DATABASE", "CONTINUE", "COPY FILE", "COPY INDEXES", "COPY MEMO",
        "COPY PROCEDURES", "COPY STRUCTURE", "COPY STRUCTURE EXTENDED", "COPY TAG", "COPY TO", "COPY TO ARRAY", "COUNT", "CREATE",
        "CREATE CLASS", "CREATE CLASSLIB", "CREATE COLOR SET", "CREATE CONNECTION", "CREATE CURSOR", "CREATE DATABASE", "CREATE FORM",
        "CREATE FROM", "CREATE LABEL", "CREATE MENU", "CREATE PROJECT", "CREATE QUERY", "CREATE REPORT", "CREATE SQL VIEW", "CREATE TABLE",
        "CREATE TRIGGER", "DEACTIVATE MENU", "DEACTIVATE POPUP", "DEACTIVATE WINDOW", "DEBUG", "DEBUGOUT", "DECLARE", "DEFINE BAR",
        "DEFINE BOX", "DEFINE CLASS", "DEFINE MENU", "DEFINE PAD", "DEFINE POPUP", "DEFINE WINDOW", "DELETE", "DELETE CONNECTION",
        "DELETE DATABASE", "DELETE FILE", "DELETE TAG", "DELETE TRIGGER", "DELETE VIEW", "DIMENSION", "DIR", "DISPLAY", "DISPLAY CONNECTIONS",
        "DISPLAY DATABASE", "DISPLAY DLLS", "DISPLAY FILES", "DISPLAY MEMORY", "DISPLAY OBJECTS", "DISPLAY PROCEDURES", "DISPLAY STATUS",
        "DISPLAY STRUCTURE", "DISPLAY TABLES", "DISPLAY VIEWS", "DO", "DO CASE", "DO FORM", "DO WHILE", "DOEVENTS", "DROP TABLE", "DROP VIEW",
        "EDIT", "EJECT", "EJECT PAGE", "END TRANSACTION", "ERASE", "ERROR", "EXIT", "EXPORT", "EXTERNAL", "FLUSH", "FOR", "FOR EACH", "FREE TABLE",
        "FUNCTION", "GATHER", "GETEXPR", "GO", "HELP", "HIDE MENU", "HIDE POPUP", "HIDE WINDOW", "IF", "IMPORT", "INDEX", "INPUT", "INSERT",
        "INSERT INTO", "JOIN", "KEYBOARD", "LABEL", "LIST", "LIST CONNECTIONS", "LIST DATABASE", "LIST DLLS", "LIST FILES", "LIST MEMORY",
        "LIST OBJECTS", "LIST PROCEDURES", "LIST STATUS", "LIST STRUCTURE", "LIST TABLES", "LIST VIEWS", "LOAD", "LOCAL", "LOCATE", "LPARAMETERS",
        "MD", "MENU", "MENU TO", "MODIFY CLASS", "MODIFY COMMAND", "MODIFY CONNECTION", "MODIFY DATABASE", "MODIFY FILE", "MODIFY FORM",
        "MODIFY GENERAL", "MODIFY LABEL", "MODIFY MEMO", "MODIFY MENU", "MODIFY PROCEDURE", "MODIFY PROJECT", "MODIFY QUERY", "MODIFY REPORT",
        "MODIFY STRUCTURE", "MODIFY VIEW", "MODIFY WINDOW", "MOUSE", "MOVE POPUP", "MOVE WINDOW", "NOTE", "ON BAR", "ON ERROR", "ON ESCAPE",
        "ON EXIT BAR", "ON EXIT MENU", "ON EXIT PAD", "ON EXIT POPUP", "ON KEY", "ON KEY LABEL", "ON PAD", "ON PAGE", "ON READERROR",
        "ON SELECTION BAR", "ON SELECTION MENU", "ON SELECTION PAD", "ON SELECTION POPUP", "ON SHUTDOWN", "OPEN DATABASE", "PACK",
        "PACK DATABASE", "PARAMETERS", "PLAY MACRO", "POP KEY", "POP MENU", "POP POPUP", "PRINTJOB", "PRIVATE", "PROCEDURE", "PUBLIC",
        "PUSH KEY", "PUSH MENU", "PUSH POPUP", "QUIT", "RD", "READ", "READ EVENTS", "READ MENU", "RECALL", "REINDEX", "RELEASE",
        "RELEASE BAR", "RELEASE CLASSLIB", "RELEASE LIBRARY", "RELEASE MENUS", "RELEASE PAD", "RELEASE POPUPS", "RELEASE PROCEDURE",
        "RELEASE WINDOWS", "REMOVE CLASS", "REMOVE TABLE", "RENAME", "RENAME CLASS", "RENAME CONNECTION", "RENAME TABLE", "RENAME VIEW",
        "REPLACE", "REPLACE FROM ARRAY", "REPORT FORM", "RESTORE FROM", "RESTORE MACROS", "RESTORE SCREEN", "RESTORE WINDOW", "RESUME",
        "RETRY", "RETURN", "ROLLBACK", "RUN", "SAVE MACROS", "SAVE SCREEN", "SAVE TO", "SAVE WINDOWS", "SCAN", "SCATTER", "SCROLL", "SEEK",
        "SELECT", "SELECT - SQL", "SET", "SET CLASSLIB", "SET DATABASE", "SET DATASESSION", "SET DEFAULT", "SET FILTER", "SET INDEX", "SET LIBRARY",
        "SET ORDER", "SET PATH", "SET PROCEDURE", "SET RELATION", "SET SKIP", "SHOW GET", "SHOW GETS", "SHOW MENU", "SHOW OBJECT", "SHOW POPUP",
        "SHOW WINDOW", "SIZE POPUP", "SIZE WINDOW", "SKIP", "SORT", "STORE", "SUM", "SUSPEND", "SYS", "TEXT", "TOTAL", "TYPE", "UNLOCK",
        "UPDATE", "USE", "VALIDATE DATABASE", "WAIT", "WITH", "ZAP", "ZOOM WINDOW",
    ];

    private static void AppendCommands(StringBuilder sb)
    {
        var rows = CommandCoverage.Run();
        int Count(CommandCoverage.Status st) => rows.Count(r => r.Status == st);
        int supported = Count(CommandCoverage.Status.Supported), unsupported = Count(CommandCoverage.Status.Unsupported);
        int missing = rows.Count - supported - unsupported;
        sb.Append("## Commands\n\n| | Commands | Share |\n|---|---:|---:|\n");
        sb.Append($"| Supported | {supported} | {100.0 * supported / rows.Count:F1}% |\n");
        sb.Append($"| Unsupported, with a reason | {unsupported} | {100.0 * unsupported / rows.Count:F1}% |\n");
        sb.Append($"| Missing | {missing} | {100.0 * missing / rows.Count:F1}% |\n");
        sb.Append($"| **Total** | **{rows.Count}** | |\n\n");
        sb.Append("Designer commands (CREATE/MODIFY FORM, REPORT, …) count as supported when they open the IDE's designer; without the IDE they report that the designer is unavailable.\n\n");
        sb.Append("### Unsupported commands\n\n| Command | Reason |\n|---|---|\n");
        foreach (var r in rows.Where(r => r.Status == CommandCoverage.Status.Unsupported)) sb.Append($"| {r.Command.Replace("|", "\\|")} | {r.Detail} |\n");
        sb.Append('\n');
        if (missing > 0)
        {
            sb.Append("### Missing commands\n\n| Command | What happens |\n|---|---|\n");
            foreach (var r in rows.Where(r => r.Status is not (CommandCoverage.Status.Supported or CommandCoverage.Status.Unsupported)))
                sb.Append($"| {r.Command} | {r.Status}: {r.Detail?.Replace("|", "\\|")} |\n");
            sb.Append('\n');
        }
        sb.Append("### Supported commands\n\n");
        sb.Append(string.Join(", ", rows.Where(r => r.Status == CommandCoverage.Status.Supported).Select(r => r.Command))).Append("\n\n");
    }

    public enum SetStatus { Supported, NoEffect, Missing, Unsupported }

    private const string Unicode = "Text is Unicode, so there are no code pages to set.";
    private const string CharMode = "Character-mode display settings have no meaning for forms.";

    /// <summary>The VFP 9 SET commands: what Joe Pro does with each (SET() reports every value that was set).</summary>
    public static readonly IReadOnlyDictionary<string, (SetStatus Status, string? Reason)> SetCommands = new Dictionary<string, (SetStatus, string?)>(StringComparer.OrdinalIgnoreCase)
    {
        ["ALTERNATE"] = (SetStatus.Supported, null), ["ANSI"] = (SetStatus.Supported, null), ["ASSERTS"] = (SetStatus.Supported, null),
        ["CENTURY"] = (SetStatus.Supported, null), ["CLASSLIB"] = (SetStatus.Supported, null), ["COLLATE"] = (SetStatus.Supported, null),
        ["CONSOLE"] = (SetStatus.Supported, null), ["COVERAGE"] = (SetStatus.Supported, null), ["DATABASE"] = (SetStatus.Supported, null),
        ["DATASESSION"] = (SetStatus.Supported, null), ["DATE"] = (SetStatus.Supported, null), ["DECIMALS"] = (SetStatus.Supported, null),
        ["DEFAULT"] = (SetStatus.Supported, null), ["DELETED"] = (SetStatus.Supported, null), ["ENGINEBEHAVIOR"] = (SetStatus.Supported, null),
        ["EVENTTRACKING"] = (SetStatus.Supported, null), ["EXACT"] = (SetStatus.Supported, null), ["EXCLUSIVE"] = (SetStatus.Supported, null),
        ["FDOW"] = (SetStatus.Supported, null), ["FILTER"] = (SetStatus.Supported, null), ["FIXED"] = (SetStatus.Supported, null),
        ["FWEEK"] = (SetStatus.Supported, null), ["HEADINGS"] = (SetStatus.Supported, null), ["HOURS"] = (SetStatus.Supported, null),
        ["MARK"] = (SetStatus.Supported, null), ["MARK OF"] = (SetStatus.Supported, null), ["MEMOWIDTH"] = (SetStatus.Supported, null),
        ["MESSAGE"] = (SetStatus.Supported, null), ["NEAR"] = (SetStatus.Supported, null), ["NULL"] = (SetStatus.Supported, null),
        ["NULLDISPLAY"] = (SetStatus.Supported, null), ["OPTIMIZE"] = (SetStatus.Supported, null), ["ORDER"] = (SetStatus.Supported, null),
        ["PATH"] = (SetStatus.Supported, null), ["POINT"] = (SetStatus.Supported, null), ["PROCEDURE"] = (SetStatus.Supported, null),
        ["RELATION"] = (SetStatus.Supported, null), ["SAFETY"] = (SetStatus.Supported, null), ["SECONDS"] = (SetStatus.Supported, null),
        ["SEPARATOR"] = (SetStatus.Supported, null), ["SKIP OF"] = (SetStatus.Supported, null), ["SPACE"] = (SetStatus.Supported, null),
        ["STEP"] = (SetStatus.Supported, null), ["SYSMENU"] = (SetStatus.Supported, null), ["TALK"] = (SetStatus.Supported, null),
        ["TEXTMERGE"] = (SetStatus.Supported, null), ["UNIQUE"] = (SetStatus.Supported, null),

        ["AUTOSAVE"] = (SetStatus.NoEffect, "Every change is committed by the engine; there are no buffers to flush."),
        ["BELL"] = (SetStatus.NoEffect, CharMode), ["CLOCK"] = (SetStatus.NoEffect, CharMode), ["CURSOR"] = (SetStatus.NoEffect, CharMode),
        ["STATUS"] = (SetStatus.NoEffect, CharMode), ["STATUS BAR"] = (SetStatus.NoEffect, "The IDE's status bar is always shown."),
        ["NOTIFY"] = (SetStatus.NoEffect, "System messages go to the host's status area."), ["ECHO"] = (SetStatus.NoEffect, "Use the debugger's trace instead."),
        ["ESCAPE"] = (SetStatus.NoEffect, "Programs are interrupted from the IDE's Stop command."), ["TYPEAHEAD"] = (SetStatus.NoEffect, "The keyboard buffer has no fixed size."),
        ["CONFIRM"] = (SetStatus.NoEffect, CharMode), ["CARRY"] = (SetStatus.NoEffect, CharMode),
        ["BLOCKSIZE"] = (SetStatus.NoEffect, "Memo storage is managed by the engine."), ["BROWSEIME"] = (SetStatus.NoEffect, "Input methods are controlled by the operating system."),
        ["CPCOMPILE"] = (SetStatus.NoEffect, Unicode), ["CPDIALOG"] = (SetStatus.NoEffect, Unicode), ["NOCPTRANS"] = (SetStatus.NoEffect, Unicode),
        ["DOHISTORY"] = (SetStatus.NoEffect, "The Command Window keeps its own history."), ["DEVELOPMENT"] = (SetStatus.NoEffect, "Programs always reload when their source changes."),
        ["FULLPATH"] = (SetStatus.NoEffect, "DBF() always returns the full path."), ["HELP"] = (SetStatus.NoEffect, "Help comes from the documentation and editor hover."),
        ["RESOURCE"] = (SetStatus.NoEffect, "IDE settings are stored per user automatically."), ["KEYCOMP"] = (SetStatus.NoEffect, "Keyboard behavior follows the platform."),
        ["LOGERRORS"] = (SetStatus.NoEffect, "Compile errors are reported in the editor and build output."), ["MULTILOCKS"] = (SetStatus.NoEffect, "Table buffering works without it."),
        ["OLEOBJECT"] = (SetStatus.NoEffect, "There is no OLE object search."), ["SQLBUFFERING"] = (SetStatus.Missing, null),
        ["TABLEVALIDATE"] = (SetStatus.NoEffect, "The engine validates tables itself."), ["TOPIC"] = (SetStatus.NoEffect, "Help comes from the documentation and editor hover."),
        ["TRBETWEEN"] = (SetStatus.NoEffect, "The debugger's trace pane shows what it steps through."), ["DEBUG"] = (SetStatus.NoEffect, "The debugger is always available in the IDE."),
        ["INDEX"] = (SetStatus.NoEffect, "Every index tag of a table is always open."), ["LOCK"] = (SetStatus.NoEffect, "Reads never take locks; each sees committed data."),
        ["REFRESH"] = (SetStatus.NoEffect, "Changes by other users are visible at once."), ["ODOMETER"] = (SetStatus.NoEffect, CharMode),

        ["COMPATIBLE"] = (SetStatus.Missing, null), ["CURRENCY"] = (SetStatus.Missing, null), ["KEY"] = (SetStatus.Missing, null),
        ["REPROCESS"] = (SetStatus.Missing, null), ["SKIP"] = (SetStatus.Missing, null), ["STRICTDATE"] = (SetStatus.Missing, null),
        ["UDFPARMS"] = (SetStatus.Missing, null), ["VARCHARMAPPING"] = (SetStatus.Missing, null), ["AUTOINCERROR"] = (SetStatus.Missing, null),
        ["SYSFORMATS"] = (SetStatus.Missing, null),

        ["PRINTER"] = (SetStatus.Unsupported, "Direct printer streaming is not supported; print through REPORT FORM."),
        ["DEVICE"] = (SetStatus.Unsupported, "Direct printer streaming is not supported; print through REPORT FORM."),
        ["PDSETUP"] = (SetStatus.Unsupported, "Printer drivers are chosen in the report's page setup."),
        ["MARGIN"] = (SetStatus.Unsupported, "Printer margins are set in the report's page setup."),
        ["LIBRARY"] = (SetStatus.Unsupported, "FoxPro API libraries (.FLL) cannot be loaded; use .NET code."),
        ["COLOR"] = (SetStatus.Unsupported, CharMode), ["COLOR OF SCHEME"] = (SetStatus.Unsupported, CharMode), ["COLOR SET"] = (SetStatus.Unsupported, CharMode),
        ["BORDER"] = (SetStatus.Unsupported, CharMode), ["DELIMITERS"] = (SetStatus.Unsupported, CharMode), ["DISPLAY"] = (SetStatus.Unsupported, CharMode),
        ["FORMAT"] = (SetStatus.Unsupported, CharMode), ["FUNCTION"] = (SetStatus.Unsupported, "Function-key macros are not supported; use ON KEY LABEL."),
        ["INTENSITY"] = (SetStatus.Unsupported, CharMode), ["MACKEY"] = (SetStatus.Unsupported, "Keyboard macros are not supported; use ON KEY LABEL."),
        ["MOUSE"] = (SetStatus.Unsupported, CharMode), ["PALETTE"] = (SetStatus.Unsupported, CharMode), ["READBORDER"] = (SetStatus.Unsupported, CharMode),
        ["WINDOW OF MEMO"] = (SetStatus.Unsupported, "Memos are edited in the IDE's editor."),
    };

    private static void AppendSets(StringBuilder sb)
    {
        var rows = SetCommands.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        int Count(SetStatus st) => rows.Count(r => r.Value.Status == st);
        sb.Append("## SET commands\n\n| | SET commands | Share |\n|---|---:|---:|\n");
        foreach (var (st, label) in new[] { (SetStatus.Supported, "Supported"), (SetStatus.NoEffect, "Accepted, no effect needed (with a reason)"), (SetStatus.Unsupported, "Unsupported, with a reason"), (SetStatus.Missing, "Missing (accepted, stored for SET(), not acted on)") })
            sb.Append($"| {label} | {Count(st)} | {100.0 * Count(st) / rows.Count:F1}% |\n");
        sb.Append($"| **Total** | **{rows.Count}** | |\n\n");
        sb.Append("### No effect needed\n\n| SET | Why |\n|---|---|\n");
        foreach (var r in rows.Where(r => r.Value.Status == SetStatus.NoEffect)) sb.Append($"| SET {r.Key} | {r.Value.Reason} |\n");
        sb.Append("\n### Unsupported SET commands\n\n| SET | Reason |\n|---|---|\n");
        foreach (var r in rows.Where(r => r.Value.Status == SetStatus.Unsupported)) sb.Append($"| SET {r.Key} | {r.Value.Reason} |\n");
        sb.Append("\n### Missing SET commands\n\n").Append(string.Join(", ", rows.Where(r => r.Value.Status == SetStatus.Missing).Select(r => "SET " + r.Key))).Append("\n\n");
        sb.Append("### Supported SET commands\n\n").Append(string.Join(", ", rows.Where(r => r.Value.Status == SetStatus.Supported).Select(r => "SET " + r.Key))).Append("\n\n");
    }

    /// <summary>Each VFP 9 function with its status: Supported, Unsupported (with the reason) or Missing.</summary>
    public static IEnumerable<(string Name, string Status, string? Reason)> Coverage()
    {
        var implemented = Library.Names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Functions.Distinct().Order(StringComparer.Ordinal))
            yield return implemented.Contains(f) ? (f, "Supported", null)
                : Unsupported.TryGetValue(f, out var why) ? (f, "Unsupported", why) : (f, "Missing", null);
    }

    /// <summary>The coverage matrix as a Markdown page (docs/reference/coverage.md; `joepro functions --coverage`).</summary>
    public static string CoverageMarkdown()
    {
        var rows = Coverage().ToList();
        int supported = rows.Count(r => r.Status == "Supported"), unsupported = rows.Count(r => r.Status == "Unsupported"), missing = rows.Count(r => r.Status == "Missing");
        var sb = new StringBuilder();
        sb.Append("# VFP 9 coverage\n\n");
        sb.Append("Generated by `joepro functions --coverage`: functions are checked against the runtime's registry; commands are checked by running a sample of each in a scratch folder.\n\n");
        sb.Append("## Functions\n\n");
        sb.Append($"| | Functions | Share |\n|---|---:|---:|\n");
        sb.Append($"| Supported | {supported} | {100.0 * supported / rows.Count:F1}% |\n");
        sb.Append($"| Unsupported, with a reason | {unsupported} | {100.0 * unsupported / rows.Count:F1}% |\n");
        sb.Append($"| Missing | {missing} | {100.0 * missing / rows.Count:F1}% |\n");
        sb.Append($"| **Total** | **{rows.Count}** | |\n\n");
        if (unsupported > 0)
        {
            sb.Append("### Unsupported functions\n\n| Function | Reason |\n|---|---|\n");
            foreach (var r in rows.Where(r => r.Status == "Unsupported")) sb.Append($"| {r.Name}() | {r.Reason} |\n");
            sb.Append('\n');
        }
        if (missing > 0)
        {
            sb.Append("### Missing functions\n\n");
            sb.Append(string.Join(", ", rows.Where(r => r.Status == "Missing").Select(r => r.Name + "()"))).Append("\n\n");
        }
        AppendCommands(sb);
        AppendSets(sb);
        sb.Append("## Supported functions\n\n");
        foreach (var g in rows.Where(r => r.Status == "Supported").GroupBy(r => r.Name[0]))
            sb.Append($"**{g.Key}** ").Append(string.Join(", ", g.Select(r => r.Name + "()"))).Append("\n\n");
        return sb.ToString();
    }
}
