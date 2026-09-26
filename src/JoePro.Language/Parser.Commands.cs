using JoePro.Core;

namespace JoePro.Language;

public sealed partial class Parser
{
    private static readonly string[] SqlStopWords =
    [
        "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "INTO", "UNION", "TO", "NOFILTER", "READWRITE", "JOIN", "INNER",
        "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "ON", "NOCONSOLE", "PLAIN", "NOWAIT", "AS",
    ];

    /// <summary>Parses xBase data commands, SET commands and SQL statements. Returns null for an unknown verb.</summary>
    private Stmt? DataCommand(Token verb)
    {
        bool V(string kw) => KwMatch(verb, kw);

        if (V("USE")) return Use();
        if (V("SELECT")) return IsSqlSelect() ? SelectStatement() : new SelectAreaStmt(AliasArg());
        if (V("GO") || V("GOTO")) return Go();
        if (V("SKIP"))
        {
            Expr? n = AtEnd || Kw("IN") ? null : Expression();
            Expr? inA = AcceptKw("IN") ? AliasArg() : null;
            return new SkipStmt(n, inA);
        }
        if (V("SEEK"))
        {
            var key = Expression();
            Expr? order = null, inA = null;
            while (!AtEnd)
            {
                if (AcceptKw("ORDER")) { AcceptKw("TAG"); order = NameArg("IN", "ASCENDING", "DESCENDING", "OF"); }
                else if (AcceptKw("IN")) inA = AliasArg();
                else if (AcceptKw("OF")) NameArg("IN", "ASCENDING", "DESCENDING");
                else if (AcceptKw("ASCENDING") || AcceptKw("DESCENDING")) { }
                else break;
            }
            return new SeekStmt(key, order, inA);
        }
        if (V("CONTINUE")) return new ContinueStmt();
        if (V("REPLACE")) return Replace();
        if (V("APPEND"))
        {
            if (AcceptKw("BLANK")) return new AppendBlankStmt(AcceptKw("IN") ? AliasArg() : null);
            if (AcceptKw("FROM"))
            {
                if (AcceptKw("ARRAY")) { var arr = Ident(); _p = _t.Count; return new AppendFromStmt(new LiteralExpr(Value.String(arr)), null, "ARRAY", null); }
                var src = NameArg("FOR", "FIELDS", "TYPE", "DELIMITED", "SDF", "XLS", "CSV");
                Expr? @for = null;
                string? type = null;
                List<string>? fields = null;
                while (!AtEnd)
                {
                    if (AcceptKw("FOR")) @for = Expression();
                    else if (AcceptKw("FIELDS")) { fields = []; do fields.Add(Ident()); while (AcceptOp(",")); }
                    else if (AcceptKw("TYPE")) type = Ident().ToUpperInvariant();
                    else type = Ident().ToUpperInvariant();
                }
                return new AppendFromStmt(src, @for, type, fields);
            }
            return new AppendBlankStmt(null); // APPEND (interactive) behaves like APPEND BLANK without a UI
        }
        if (V("DELETE"))
        {
            if (AcceptKw("VIEW")) return new DeleteDbObjectStmt("VIEW", NameArg());
            if (AcceptKw("CONNECTION")) return new DeleteDbObjectStmt("CONNECTION", NameArg());
            if (AcceptKw("TAG"))
            {
                if (AcceptKw("ALL")) return new DeleteTagStmt([], true);
                var tags = new List<string>();
                do tags.Add(Ident()); while (AcceptOp(","));
                return new DeleteTagStmt(tags, false);
            }
            if (AcceptKw("FILE")) { NameArg(); return new NoOpStmt("DELETE FILE"); }
            if (IsSqlDelete()) return SqlDelete();
            return new DeleteStmt(ParseScope(), false);
        }
        if (V("RECALL")) return new DeleteStmt(ParseScope(), true);
        if (V("PACK")) { AcceptKw("MEMO"); AcceptKw("DBF"); return new PackStmt(AcceptKw("IN") ? AliasArg() : null); }
        if (V("ZAP")) return new ZapStmt(AcceptKw("IN") ? AliasArg() : null);
        if (V("INDEX")) return Index();
        if (V("REINDEX")) { AcceptKw("COMPACT"); return new ReindexStmt(); }
        if (V("SET")) return Set();
        if (V("CLOSE"))
        {
            var what = AtEnd ? "ALL" : Next().Text.ToUpperInvariant();
            what = what.StartsWith("TABL") ? "TABLES" : what.StartsWith("DATA") ? "DATABASES" : what.StartsWith("INDE") ? "INDEXES" : what;
            AcceptKw("ALL");
            return new CloseStmt(what);
        }
        if (V("CREATE")) return Create();
        if (V("ALTER")) return AlterTable();
        if (V("OPEN"))
        {
            ExpectKw("DATABASE");
            var name = NameArg("EXCLUSIVE", "SHARED", "NOUPDATE", "VALIDATE");
            bool excl = false;
            while (!AtEnd) { if (AcceptKw("EXCLUSIVE")) excl = true; else _p++; }
            return new OpenDatabaseStmt(name, excl);
        }
        if (V("LIST") || V("DISPLAY"))
        {
            bool display = V("DISPLAY");
            if (AcceptKw("STRUCTURE")) { ParseScope(); return new ListStmt(display, null, Scope.Default, true, false); }
            if (AcceptKw("MEMORY") || AcceptKw("STATUS") || AcceptKw("OBJECTS") || AcceptKw("FILES") || AcceptKw("DATABASE") || AcceptKw("TABLES") || AcceptKw("CONNECTIONS") || AcceptKw("PROCEDURES") || AcceptKw("VIEWS"))
            {
                _p = _t.Count;
                return new NoOpStmt(verb.Text.ToUpperInvariant());
            }
            List<Expr>? fields = null;
            bool off = false;
            Scope scope = Scope.Default;
            while (!AtEnd)
            {
                if (AcceptKw("FIELDS")) { fields = ExprList(); continue; }
                if (AcceptKw("OFF")) { off = true; continue; }
                if (AcceptKw("NOCONSOLE") || AcceptKw("NOOPTIMIZE")) continue;
                if (AcceptKw("TO")) { AcceptKw("PRINTER"); AcceptKw("FILE"); if (!AtEnd && !ScopeWords.Any(Kw)) NameArg(ScopeWords); continue; }
                if (ScopeWords.Any(Kw)) { scope = MergeScope(scope, ParseScope(true, "FIELDS", "OFF", "TO", "NOCONSOLE")); continue; }
                fields = ExprList();
            }
            return new ListStmt(display, fields, scope, false, off);
        }
        if (V("COUNT") || V("SUM") || V("AVERAGE") || V("CALCULATE")) return Aggregate(verb.Text);
        if (V("SCATTER")) return Scatter();
        if (V("GATHER")) return Gather();
        if (V("BEGIN")) { ExpectKw("TRANSACTION"); return new TransactionStmt("BEGIN"); }
        if (V("END") && Kw("TRANSACTION")) { _p++; return new TransactionStmt("END"); }
        if (V("ROLLBACK")) return new TransactionStmt("ROLLBACK");
        if (V("BROWSE") || V("EDIT") || V("CHANGE"))
        {
            List<Expr>? fields = null;
            Scope scope = Scope.Default;
            while (!AtEnd)
            {
                if (AcceptKw("FIELDS")) fields = ExprList();
                else if (ScopeWords.Any(Kw)) scope = ParseScope(true, "FIELDS", "NOEDIT", "NOAPPEND", "NODELETE", "TITLE", "NORMAL", "NOWAIT", "LAST");
                else _p++;
            }
            return new BrowseStmt(fields, scope);
        }
        if (V("COPY")) return Copy();
        if (V("IMPORT"))
        {
            bool db = AcceptKw("DATABASE");
            AcceptKw("FOXPRO");
            var src = NameArg("TO", "TYPE");
            Expr? to = AcceptKw("TO") ? NameArg() : null;
            if (AcceptKw("TYPE")) Ident();
            return new ImportStmt(src, to, db);
        }
        if (V("INSERT")) return SqlInsert();
        if (V("UPDATE")) return SqlUpdate();
        if (V("DEFINE")) { _p = _t.Count; return new NoOpStmt("DEFINE"); }
        if (V("CD") || V("CHDIR")) return new ChdirStmt(NameArg());
        if ((Kw("RENAME") || Kw("DROP")) && _p + 1 < _t.Count && (_t[_p + 1].Text.Equals("VIEW", StringComparison.OrdinalIgnoreCase) || _t[_p + 1].Text.Equals("CONNECTION", StringComparison.OrdinalIgnoreCase)))
        {
            var rename = V("RENAME");
            if (!rename) V("DROP");
            var kind = Next().Text.ToUpperInvariant();
            var from = NameArg("TO");
            if (!rename) return new DeleteDbObjectStmt(kind, from);
            ExpectKw("TO");
            return new RenameDbObjectStmt(kind, from, NameArg());
        }
        if (V("MODIFY") || V("MODI") || V("BUILD") || V("REPORT") || V("LABEL") || V("KEYBOARD") || V("ACTIVATE") || V("DEACTIVATE")
            || V("HIDE") || V("SHOW") || V("MOVE") || V("PUSH") || V("POP") || V("RESTORE") || V("SAVE") || V("RUN") || V("FLUSH")
            || V("UNLOCK") || V("DOEVENTS") || V("RETRY") || V("EXTERNAL") || V("SLEEP") || V("LOCK") || V("VALIDATE") || V("ASSERT")
            || V("DEBUGOUT") || V("ERASE") || V("RENAME") || V("MD") || V("MKDIR") || V("RD") || V("RMDIR"))
        {
            var rawVerb = verb.Text.ToUpperInvariant();
            var rest = RawText(_p, _t.Count);
            _p = _t.Count;
            return new SetStmt("__" + rawVerb, rest, null, []);
        }
        if (V("COMPILE")) { AcceptKw("FORM"); AcceptKw("CLASSLIB"); AcceptKw("REPORT"); AcceptKw("DATABASE"); return new CompileStmt(NameArg()); }
        if (V("TABLEUPDATE")) return null;
        return null;
    }

    private static Scope MergeScope(Scope a, Scope b) =>
        new(b.Kind != "DEFAULT" ? b.Kind : a.Kind, b.Count ?? a.Count, b.For ?? a.For, b.While ?? a.While, b.In ?? a.In);

    private Stmt Use()
    {
        Expr? table = null, inA = null, alias = null, order = null;
        bool again = false, noUpdate = false, noData = false;
        bool? excl = null;
        string[] stops = ["IN", "ALIAS", "AGAIN", "EXCLUSIVE", "SHARED", "ORDER", "NOUPDATE", "INDEX", "NODATA", "NOREQUERY", "CONNSTRING", "ONLINE", "ADMIN"];
        if (!AtEnd && !stops.Any(Kw)) table = NameArg(stops);
        while (!AtEnd)
        {
            if (AcceptKw("IN")) inA = AliasArg();
            else if (AcceptKw("ALIAS")) alias = AliasArg();
            else if (AcceptKw("AGAIN")) again = true;
            else if (AcceptKw("EXCLUSIVE")) excl = true;
            else if (AcceptKw("SHARED")) excl = false;
            else if (AcceptKw("NOUPDATE")) noUpdate = true;
            else if (AcceptKw("NODATA")) noData = true;
            else if (AcceptKw("ORDER")) { AcceptKw("TAG"); order = NameArg(stops); AcceptKw("ASCENDING"); AcceptKw("DESCENDING"); }
            else if (AcceptKw("INDEX")) NameArg(stops);
            else _p++;
        }
        return new UseStmt(table, inA, alias, again, excl, order, noUpdate, noData);
    }

    private Stmt Go()
    {
        if (AcceptKw("TOP")) return new GoStmt("TOP", null, AcceptKw("IN") ? AliasArg() : null);
        if (AcceptKw("BOTTOM")) return new GoStmt("BOTTOM", null, AcceptKw("IN") ? AliasArg() : null);
        AcceptKw("RECORD");
        var n = Expression();
        return new GoStmt("RECORD", n, AcceptKw("IN") ? AliasArg() : null);
    }

    private Stmt Replace()
    {
        // Scope clauses may appear before or after the field list: REPLACE ALL x WITH 1, REPLACE x WITH 1 FOR …
        var leading = ParseScope();
        var items = new List<(Expr, Expr, bool)>();
        do
        {
            if (AtEnd || ScopeWords.Any(Kw)) break;
            var field = Postfix(Primary());
            ExpectKw("WITH");
            var value = Expression();
            bool additive = AcceptKw("ADDITIVE");
            items.Add((field, value, additive));
        } while (AcceptOp(","));
        return new ReplaceStmt(items, MergeScope(leading, ParseScope()));
    }

    private Stmt Index()
    {
        ExpectKw("ON");
        var key = Expression();
        string? tag = null;
        Expr? @for = null;
        bool desc = false, additive = false;
        string kind = "REGULAR";
        while (!AtEnd)
        {
            if (AcceptKw("TAG")) { tag = Ident(); if (AcceptKw("OF")) NameArg("FOR", "ASCENDING", "DESCENDING", "UNIQUE", "CANDIDATE", "ADDITIVE", "COMPACT"); }
            else if (AcceptKw("TO")) tag = NameArg("FOR", "ASCENDING", "DESCENDING", "UNIQUE", "CANDIDATE", "ADDITIVE", "COMPACT") is LiteralExpr l ? System.IO.Path.GetFileNameWithoutExtension(l.Value.AsString) : "IDX";
            else if (AcceptKw("FOR")) @for = Expression();
            else if (AcceptKw("DESCENDING")) desc = true;
            else if (AcceptKw("ASCENDING")) desc = false;
            else if (AcceptKw("UNIQUE")) kind = "UNIQUE";
            else if (AcceptKw("CANDIDATE")) kind = "CANDIDATE";
            else if (AcceptKw("ADDITIVE")) additive = true;
            else if (AcceptKw("COMPACT") || AcceptKw("BINARY")) { }
            else if (AcceptKw("COLLATE")) Expression();
            else throw Error($"Unrecognized INDEX clause '{Peek()}'.");
        }
        return new IndexStmt(key, tag ?? throw Error("Missing TAG name."), @for, desc, kind, additive);
    }

    // ---- SET ------------------------------------------------------------------------

    private static readonly string[] SetOptionNames =
    [
        "EXACT", "ANSI", "DELETED", "NEAR", "TALK", "CENTURY", "NULL", "NULLDISPLAY", "SECONDS", "EXCLUSIVE", "SAFETY", "OPTIMIZE",
        "HOURS", "DECIMALS", "DATE", "MARK", "POINT", "SEPARATOR", "ENGINEBEHAVIOR", "COLLATE", "DEFAULT", "PATH", "ORDER",
        "FILTER", "RELATION", "DATABASE", "PROCEDURE", "CLASSLIB", "MULTILOCKS", "REPROCESS", "STATUS", "ECHO", "STEP",
        "CONSOLE", "ESCAPE", "BELL", "NOTIFY", "CPDIALOG", "STRICTDATE", "FIXED", "UDFPARMS", "COMPATIBLE", "MEMOWIDTH",
        "TEXTMERGE", "LIBRARY", "HELP", "RESOURCE", "SYSMENU", "CURSOR", "TYPEAHEAD", "CARRY", "CONFIRM", "FULLPATH",
        "UNIQUE", "INDEX", "KEY", "SKIP", "DEBUG", "LOCK", "REFRESH", "CURRENCY", "DATASESSION", "COVERAGE", "EVENTTRACKING",
        "ALTERNATE", "PRINTER", "DEVICE", "CLOCK", "ROLLOVER", "BLOCKSIZE", "LOGERRORS", "VARCHARMAPPING", "TABLEVALIDATE",
        "FUNCTION", "MESSAGE", "ODOMETER", "PALETTE", "SPACE", "HEADINGS", "INTENSITY", "READBORDER", "NOCPTRANS", "OLEOBJECT",
        "ASSERTS", "AUTOSAVE", "AUTOINCERROR", "BROWSEIME", "CPCOMPILE", "DOHISTORY", "FDOW", "FWEEK", "KEYCOMP", "LOGERRORS",
        "MACKEY", "MARGIN", "MOUSE", "NOTIFY", "SQLBUFFERING", "SYSFORMATS", "TOPIC", "TRBETWEEN", "WINDOW",
    ];

    private Stmt Set()
    {
        if (AtEnd) return new SetStmt("", null, null, []);
        var t = Next();
        var opt = SetOptionNames.FirstOrDefault(o => KwMatch(t, o)) ?? t.Text.ToUpperInvariant();
        var raw = _t.Skip(_p).ToList();
        switch (opt)
        {
            case "ORDER":
            {
                AcceptKw("TO");
                if (AtEnd) return new SetOrderStmt(null, null, null);
                Expr? tag = null, inA = null;
                bool? desc = null;
                if (Peek()?.Kind == TokenKind.Number) { _p++; tag = null; }
                else if (!Kw("IN"))
                {
                    AcceptKw("TAG");
                    tag = NameArg("IN", "ASCENDING", "DESCENDING", "OF");
                }
                while (!AtEnd)
                {
                    if (AcceptKw("OF")) NameArg("IN", "ASCENDING", "DESCENDING");
                    else if (AcceptKw("IN")) inA = AliasArg();
                    else if (AcceptKw("ASCENDING")) desc = false;
                    else if (AcceptKw("DESCENDING")) desc = true;
                    else throw Error($"Unrecognized phrase '{Peek()}'.");
                }
                return new SetOrderStmt(tag, inA, desc);
            }
            case "FILTER":
            {
                AcceptKw("TO");
                if (AtEnd) return new SetFilterStmt(null, null);
                if (Kw("IN")) { _p++; return new SetFilterStmt(null, AliasArg()); }
                var f = Expression();
                return new SetFilterStmt(f, AcceptKw("IN") ? AliasArg() : null);
            }
            case "RELATION":
            {
                if (AcceptKw("OFF"))
                {
                    ExpectKw("INTO");
                    return new SetRelationStmt([], false, true, AliasArg());
                }
                AcceptKw("TO");
                var rels = new List<(Expr, Expr)>();
                bool additive = false;
                while (!AtEnd)
                {
                    if (AcceptKw("ADDITIVE")) { additive = true; continue; }
                    var e = Expression();
                    ExpectKw("INTO");
                    rels.Add((e, AliasArg()));
                    if (!AcceptOp(",")) { if (AcceptKw("ADDITIVE")) additive = true; if (AcceptKw("IN")) AliasArg(); break; }
                }
                return new SetRelationStmt(rels, additive, false, null);
            }
            case "DATABASE":
                AcceptKw("TO");
                return new SetDatabaseStmt(AtEnd ? null : NameArg());
            case "PROCEDURE":
            case "CLASSLIB":
            case "LIBRARY":
            {
                AcceptKw("TO");
                var files = new List<Expr>();
                bool additive = false;
                while (!AtEnd)
                {
                    if (AcceptKw("ADDITIVE")) { additive = true; continue; }
                    if (AcceptOp(",")) continue;
                    files.Add(NameArg("ADDITIVE", "ALIAS", "IN"));
                    if (AcceptKw("ALIAS")) Ident();
                    if (AcceptKw("IN")) NameArg("ADDITIVE");
                }
                return opt == "PROCEDURE"
                    ? new SetProcedureStmt(files, additive, files.Count == 0)
                    : new SetStmt(opt, null, null, raw);
            }
        }
        // Generic: SET x ON|OFF, SET x TO expr
        string? word = null;
        Expr? expr = null;
        if (AcceptKw("TO"))
        {
            if (!AtEnd)
            {
                if (opt is "DATE" or "COLLATE" or "PATH" or "DEFAULT" or "CURRENCY" or "MARK" or "POINT" or "SEPARATOR" or "ALTERNATE" or "PRINTER" or "HELP" or "RESOURCE" or "COVERAGE" or "TEXTMERGE" or "NULLDISPLAY" or "DATASESSION" or "MESSAGE")
                {
                    if (Peek()!.Kind == TokenKind.Ident && Peek(1) == null && opt is "DATE") word = Next().Text.ToUpperInvariant();
                    else if (Peek()!.Kind is TokenKind.String or TokenKind.Number || IsOp("(")) expr = Expression();
                    else { word = RawText(_p, _t.Count); _p = _t.Count; }
                }
                else expr = Expression();
            }
        }
        else if (!AtEnd)
        {
            var w = Next();
            word = w.Text.ToUpperInvariant();
            if (w.Kind == TokenKind.Number) expr = new LiteralExpr(Value.Number(w.Number));
            else if (w.Kind == TokenKind.Macro) expr = new MacroExpr(w.Text);
        }
        while (!AtEnd) _p++; // trailing clauses (e.g. SET TALK OFF WINDOW) are ignored
        return new SetStmt(opt, word, expr, raw);
    }

    // ---- CREATE / ALTER ----------------------------------------------------------------

    private Stmt Create()
    {
        if (AcceptKw("DATABASE")) return new CreateDatabaseStmt(NameArg());
        if (Kw("SQL") && _p + 1 < _t.Count && _t[_p + 1].Text.Equals("VIEW", StringComparison.OrdinalIgnoreCase))
        {
            _p += 2;
            if (AtEnd) { _p = _t.Count; return new SetStmt("__CREATE", "VIEW", null, []); }
            var viewName = NameArg("REMOTE", "CONNECTION", "AS");
            bool remote = false, share = false;
            Expr? conn = null;
            while (!AtEnd && !Kw("AS"))
            {
                if (AcceptKw("REMOTE")) remote = true;
                else if (AcceptKw("CONNECTION")) { conn = NameArg("SHARE", "AS"); remote = true; }
                else if (AcceptKw("SHARE")) share = true;
                else _p++;
            }
            ExpectKw("AS");
            var sql = RawText(_p, _t.Count);
            _p = _t.Count;
            return new CreateViewStmt(viewName, remote, conn, share, sql);
        }
        if (AcceptKw("CONNECTION"))
        {
            string[] opts = ["DATASOURCE", "USERID", "PASSWORD", "DATABASE", "CONNSTRING"];
            var connName = NameArg(opts);
            Expr? ds = null, uid = null, pwd = null, db = null, cs = null;
            while (!AtEnd)
            {
                if (AcceptKw("DATASOURCE")) ds = Expression();
                else if (AcceptKw("USERID")) uid = Expression();
                else if (AcceptKw("PASSWORD")) pwd = Expression();
                else if (AcceptKw("DATABASE")) db = Expression();
                else if (AcceptKw("CONNSTRING")) cs = Expression();
                else _p++;
            }
            return new CreateConnectionStmt(connName, ds, uid, pwd, db, cs);
        }
        bool cursor = AcceptKw("CURSOR");
        if (!cursor && !AcceptKw("TABLE") && !AcceptKw("DBF"))
        {
            // CREATE FORM/REPORT/CLASS/PROJECT… (designers)
            var rest = RawText(_p, _t.Count);
            _p = _t.Count;
            return new SetStmt("__CREATE", rest, null, []);
        }
        var name = NameArg("FREE", "NAME", "CODEPAGE", "FROM") ;
        if (name is LiteralExpr { Value.Kind: ValueKind.Character } nl && nl.Value.AsString.Contains('('))
        {
            // Raw text swallowed the field list; re-split.
            var s = nl.Value.AsString;
            name = new LiteralExpr(Value.String(s[..s.IndexOf('(')].Trim()));
            _p = _t.FindIndex(tk => tk.IsOp("("));
        }
        bool free = false;
        while (AcceptKw("FREE") || AcceptKw("CODEPAGE") || AcceptKw("NAME"))
        {
            if (_t[_p - 1].Text.StartsWith("FREE", StringComparison.OrdinalIgnoreCase)) free = true;
            else { if (IsOp("=")) _p++; Expression(); }
        }
        if (AcceptKw("FROM"))
        {
            ExpectKw("ARRAY");
            return new CreateTableStmt(name, cursor, free, [], Ident());
        }
        ExpectOp("(");
        var fields = new List<FieldSpec>();
        while (!IsOp(")"))
        {
            if (Kw("PRIMARY") || Kw("UNIQUE") || Kw("FOREIGN") || Kw("CHECK"))
            {
                // Table constraints: PRIMARY KEY expr TAG name, UNIQUE expr TAG name, FOREIGN KEY … (key tags added by runtime)
                int depth = 0;
                var constraintStart = _p;
                while (!AtEnd && !(depth == 0 && (IsOp(",") || IsOp(")"))))
                {
                    if (IsOp("(")) depth++;
                    if (IsOp(")")) depth--;
                    _p++;
                }
                fields.Add(new FieldSpec("__CONSTRAINT:" + RawText(constraintStart, _p), ' ', 0, 0, false, false, false, false, null, null, null, false, 0, 0));
            }
            else fields.Add(FieldSpecification());
            if (!AcceptOp(",")) break;
        }
        ExpectOp(")");
        return new CreateTableStmt(name, cursor, free, fields, null);
    }

    private static readonly (string Name, char Type)[] TypeNames =
    [
        ("CHARACTER", 'C'), ("CHAR", 'C'), ("VARCHAR", 'V'), ("MEMO", 'M'), ("NUMERIC", 'N'), ("FLOAT", 'F'), ("DOUBLE", 'B'),
        ("INTEGER", 'I'), ("INT", 'I'), ("CURRENCY", 'Y'), ("MONEY", 'Y'), ("DATETIME", 'T'), ("DATE", 'D'), ("LOGICAL", 'L'),
        ("GENERAL", 'G'), ("BLOB", 'W'), ("VARBINARY", 'Q'),
    ];

    private FieldSpec FieldSpecification()
    {
        var name = Ident();
        var typeTok = Ident();
        char type;
        if (typeTok.Length == 1) type = char.ToUpperInvariant(typeTok[0]);
        else
        {
            var up = typeTok.ToUpperInvariant();
            type = TypeNames.FirstOrDefault(t => t.Name == up || (up.Length >= 4 && t.Name.StartsWith(up))).Type;
            if (type == '\0') throw Error($"Unknown field type '{typeTok}'.");
        }
        int width = 0, dec = 0;
        if (AcceptOp("("))
        {
            width = (int)Next().Number;
            if (AcceptOp(",")) dec = (int)Next().Number;
            ExpectOp(")");
        }
        bool isNull = false, notNull = false, pk = false, unique = false, autoInc = false;
        Expr? def = null, check = null;
        string? error = null;
        long next = 1;
        int step = 1;
        while (!AtEnd && !IsOp(",") && !IsOp(")"))
        {
            if (Kw("NOT") && (Peek(1)?.Kind == TokenKind.Null || KwMatch(Peek(1), "NULL"))) { _p += 2; notNull = true; }
            else if (Peek()!.Kind == TokenKind.Null || Kw("NULL")) { _p++; isNull = true; }
            else if (AcceptKw("DEFAULT")) def = Expression();
            else if (AcceptKw("CHECK")) { check = Expression(); if (AcceptKw("ERROR")) error = Expression() is LiteralExpr { Value.Kind: ValueKind.Character } l ? l.Value.AsString : null; }
            else if (Kw("PRIMARY")) { _p++; ExpectKw("KEY"); pk = true; }
            else if (AcceptKw("UNIQUE")) unique = true;
            else if (AcceptKw("AUTOINC"))
            {
                autoInc = true;
                if (AcceptKw("NEXTVALUE")) next = (long)Next().Number;
                if (AcceptKw("STEP")) step = (int)Next().Number;
            }
            else if (AcceptKw("REFERENCES")) { Ident(); if (AcceptKw("TAG")) Ident(); }
            else if (AcceptKw("NOCPTRANS") || AcceptKw("NOVALIDATE")) { }
            else throw Error($"Unrecognized field clause '{Peek()}'.");
        }
        return new FieldSpec(name, type, width, dec, isNull, notNull, pk, unique, def, check, error, autoInc, next, step);
    }

    private Stmt AlterTable()
    {
        ExpectKw("TABLE");
        var name = NameArg("ADD", "DROP", "ALTER", "RENAME");
        if (AcceptKw("ADD"))
        {
            AcceptKw("COLUMN");
            return new AlterTableStmt(name, "ADD", FieldSpecification(), null, null, null);
        }
        if (AcceptKw("DROP"))
        {
            AcceptKw("COLUMN");
            return new AlterTableStmt(name, "DROP", null, Ident(), null, null);
        }
        if (AcceptKw("ALTER"))
        {
            AcceptKw("COLUMN");
            return new AlterTableStmt(name, "ALTER", FieldSpecification(), null, null, null);
        }
        ExpectKw("RENAME");
        AcceptKw("COLUMN");
        var from = Ident();
        ExpectKw("TO");
        return new AlterTableStmt(name, "RENAME", null, null, from, Ident());
    }

    // ---- Aggregates, SCATTER/GATHER, COPY ------------------------------------------------

    private Stmt Aggregate(string verbText)
    {
        var kind = new[] { "COUNT", "SUM", "AVERAGE", "CALCULATE" }.First(k => KwMatch(new Token(TokenKind.Ident, verbText, 0), k));
        var exprs = new List<Expr>();
        var to = new List<Expr>();
        string? toArray = null;
        if (!AtEnd && !ScopeWords.Any(Kw) && !Kw("TO")) exprs = ExprList();
        var scope = ParseScope(true, "TO");
        if (AcceptKw("TO"))
        {
            if (AcceptKw("ARRAY")) toArray = Ident();
            else do to.Add(Postfix(Primary())); while (AcceptOp(","));
        }
        scope = MergeScope(scope, ParseScope());
        return new AggregateStmt(kind, exprs, to, scope, toArray);
    }

    private Stmt Scatter()
    {
        bool memvar = false, blank = false, memo = false, additive = false;
        string? name = null, toArray = null;
        List<string>? fields = null;
        while (!AtEnd)
        {
            if (AcceptKw("MEMVAR")) memvar = true;
            else if (AcceptKw("NAME")) { name = Ident(); if (AcceptKw("ADDITIVE")) additive = true; }
            else if (AcceptKw("FIELDS"))
            {
                if (AcceptKw("LIKE") || AcceptKw("EXCEPT")) { NameArg("MEMO", "BLANK", "MEMVAR", "NAME", "TO"); continue; }
                fields = [];
                do fields.Add(Ident()); while (AcceptOp(","));
            }
            else if (AcceptKw("BLANK")) blank = true;
            else if (AcceptKw("MEMO")) memo = true;
            else if (AcceptKw("TO")) toArray = Ident();
            else throw Error($"Unrecognized SCATTER clause '{Peek()}'.");
        }
        return new ScatterStmt(memvar || (name == null && toArray == null), name, fields, blank, memo, toArray, additive);
    }

    private Stmt Gather()
    {
        bool memvar = false, memo = false;
        string? name = null, fromArray = null;
        List<string>? fields = null;
        while (!AtEnd)
        {
            if (AcceptKw("MEMVAR")) memvar = true;
            else if (AcceptKw("NAME")) name = Ident();
            else if (AcceptKw("FIELDS")) { fields = []; do fields.Add(Ident()); while (AcceptOp(",")); }
            else if (AcceptKw("MEMO")) memo = true;
            else if (AcceptKw("FROM")) fromArray = Ident();
            else throw Error($"Unrecognized GATHER clause '{Peek()}'.");
        }
        return new GatherStmt(memvar || (name == null && fromArray == null), name, fields, memo, fromArray);
    }

    private Stmt Copy()
    {
        if (AcceptKw("STRUCTURE"))
        {
            ExpectKw("TO");
            var t = NameArg("FIELDS", "WITH", "DATABASE", "NAME");
            List<string>? f = null;
            if (AcceptKw("FIELDS")) { f = []; do f.Add(Ident()); while (AcceptOp(",")); }
            while (!AtEnd) _p++;
            return new CopyToStmt(t, null, f, Scope.Default, true, false);
        }
        ExpectKw("TO");
        bool array = AcceptKw("ARRAY");
        var target = NameArg("FIELDS", "TYPE", "FOR", "WHILE", "ALL", "NEXT", "REST", "RECORD", "WITH", "DATABASE", "SDF", "CSV", "XLS", "DELIMITED", "FOXPLUS", "FOX2X");
        string? type = null;
        List<string>? fields = null;
        Scope scope = Scope.Default;
        while (!AtEnd)
        {
            if (AcceptKw("FIELDS")) { fields = []; do fields.Add(Ident()); while (AcceptOp(",")); }
            else if (AcceptKw("TYPE")) type = Ident().ToUpperInvariant();
            else if (ScopeWords.Any(Kw)) scope = MergeScope(scope, ParseScope(true, "FIELDS", "TYPE", "WITH"));
            else if (Kw("SDF") || Kw("CSV") || Kw("XLS") || Kw("DELIMITED") || Kw("FOXPLUS") || Kw("FOX2X")) type = Next().Text.ToUpperInvariant();
            else if (AcceptKw("WITH")) { if (!AtEnd) _p++; }
            else _p++;
        }
        return new CopyToStmt(target, type, fields, scope, false, array);
    }

    // ---- SQL ----------------------------------------------------------------------------

    private bool IsSqlSelect()
    {
        if (AtEnd) return false;
        if (IsOp("*")) return true;
        for (int i = _p; i < _t.Count; i++) if (KwMatch(_t[i], "FROM") && _t[i].Text.Length == 4) return true;
        return false;
    }

    private bool IsSqlDelete()
    {
        for (int i = _p; i < _t.Count; i++) if (_t[i].Kind == TokenKind.Ident && _t[i].Text.Equals("FROM", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private Stmt SelectStatement()
    {
        _p--; // re-read SELECT
        var q = Select();
        return new SqlSelectStmt(q);
    }

    private SqlSelect Select()
    {
        var saved = _sql;
        _sql = true;
        try { return SelectCore(); }
        finally { _sql = saved; }
    }

    private SqlSelect SelectCore()
    {
        ExpectKw("SELECT");
        bool distinct = false;
        if (AcceptKw("DISTINCT")) distinct = true;
        else AcceptKw("ALL");
        Expr? top = null;
        bool percent = false;
        if (AcceptKw("TOP"))
        {
            top = Primary();
            percent = AcceptKw("PERCENT");
        }
        var cols = new List<SqlColumn>();
        do
        {
            if (AcceptOp("*")) { cols.Add(new SqlColumn(new LiteralExpr(Value.Null), null, true, null)); continue; }
            if (Peek()?.Kind == TokenKind.Ident && IsOp(".", 1) && IsOp("*", 2))
            {
                var a = Ident();
                _p += 2;
                cols.Add(new SqlColumn(new LiteralExpr(Value.Null), null, true, a));
                continue;
            }
            var e = Expression();
            string? alias = null;
            if (AcceptKw("AS")) alias = Ident();
            else if (Peek() is { Kind: TokenKind.Ident } t && !SqlStopWords.Any(w => KwMatch(t, w))) alias = Ident();
            cols.Add(new SqlColumn(e, alias, false, null));
        } while (AcceptOp(","));

        var from = new List<SqlSource>();
        var joins = new List<SqlJoin>();
        if (AcceptKw("FROM"))
        {
            AcceptKw("FORCE");
            from.Add(SqlSourceRef());
            while (true)
            {
                if (AcceptOp(",")) { from.Add(SqlSourceRef()); continue; }
                string? kind = null;
                if (AcceptKw("INNER")) kind = "INNER";
                else if (AcceptKw("LEFT")) { AcceptKw("OUTER"); kind = "LEFT"; }
                else if (AcceptKw("RIGHT")) { AcceptKw("OUTER"); kind = "RIGHT"; }
                else if (AcceptKw("FULL")) { AcceptKw("OUTER"); kind = "FULL"; }
                else if (AcceptKw("CROSS")) kind = "CROSS";
                else if (Kw("JOIN")) kind = "INNER";
                if (kind == null) break;
                ExpectKw("JOIN");
                var src = SqlSourceRef();
                Expr? on = null;
                if (AcceptKw("ON")) on = Expression();
                joins.Add(new SqlJoin(kind, src, on));
            }
        }
        Expr? where = AcceptKw("WHERE") ? Expression() : null;
        var groupBy = new List<Expr>();
        if (AcceptKw("GROUP")) { ExpectKw("BY"); groupBy = ExprList(); }
        Expr? having = AcceptKw("HAVING") ? Expression() : null;
        var unions = new List<(SqlSelect, bool)>();
        var order = new List<SqlOrder>();
        string? intoKind = null, intoName = null;
        bool readWrite = false, noFilter = false;
        while (!AtEnd && !IsOp(")"))
        {
            if (AcceptKw("UNION"))
            {
                bool all = AcceptKw("ALL");
                bool paren = AcceptOp("(");
                unions.Add((SelectCore(), all));
                if (paren) ExpectOp(")");
                continue;
            }
            if (AcceptKw("ORDER"))
            {
                ExpectKw("BY");
                do
                {
                    Expr oe;
                    int? pos = null;
                    if (Peek()?.Kind == TokenKind.Number) { pos = (int)Next().Number; oe = new LiteralExpr(Value.Number(pos.Value)); }
                    else oe = Expression();
                    bool desc = AcceptKw("DESC") || AcceptKw("DESCENDING");
                    if (!desc) { AcceptKw("ASC"); AcceptKw("ASCENDING"); }
                    order.Add(new SqlOrder(oe, desc, pos));
                } while (AcceptOp(","));
                continue;
            }
            if (AcceptKw("INTO"))
            {
                var k = Ident().ToUpperInvariant();
                intoKind = k.StartsWith("CURS") ? "CURSOR" : k.StartsWith("ARRA") ? "ARRAY" : k is "DBF" or "TABLE" ? "TABLE" : k;
                intoName = NameArg("READWRITE", "NOFILTER", "ORDER", "UNION", "NOCONSOLE", "PLAIN", "NOWAIT", "DATABASE", "WHERE", "GROUP", "HAVING") is LiteralExpr { Value.Kind: ValueKind.Character } l ? l.Value.AsString : null;
                continue;
            }
            if (AcceptKw("TO"))
            {
                if (AcceptKw("SCREEN")) { intoKind = "SCREEN"; continue; }
                AcceptKw("FILE"); AcceptKw("PRINTER");
                if (!AtEnd) NameArg("ADDITIVE", "NOCONSOLE", "PLAIN", "NOWAIT");
                intoKind ??= "SCREEN";
                continue;
            }
            if (AcceptKw("READWRITE")) { readWrite = true; continue; }
            if (AcceptKw("NOFILTER")) { noFilter = true; continue; }
            if (AcceptKw("NOCONSOLE") || AcceptKw("PLAIN") || AcceptKw("NOWAIT") || AcceptKw("ADDITIVE")) continue;
            if (AcceptKw("WHERE") && where == null) { where = Expression(); continue; }
            break;
        }
        return new SqlSelect(distinct, top, percent, cols, from, joins, where, groupBy, having, unions, order, intoKind, intoName, readWrite, noFilter);
    }

    private SqlSource SqlSourceRef()
    {
        if (IsOp("(") && Kw(1, "SELECT"))
        {
            _p++;
            var q = SelectCore();
            ExpectOp(")");
            AcceptKw("AS");
            return new SqlSource(null, Ident(), q);
        }
        Expr table;
        var t = Next();
        if (t.Kind == TokenKind.Ident)
        {
            var name = t.Text;
            if (IsOp("!") && Peek(1)?.Kind == TokenKind.Ident) { _p++; name += "!" + Ident(); }
            table = new LiteralExpr(Value.String(name));
        }
        else if (t.Kind == TokenKind.String) table = new LiteralExpr(Value.String(t.Text));
        else if (t.Kind == TokenKind.Macro) table = new MacroExpr(t.Text);
        else if (t.IsOp("(")) { table = Expression(); ExpectOp(")"); }
        else throw Error($"Expected a table name but found '{t}'.");
        string? alias = null;
        if (AcceptKw("AS")) alias = Ident();
        else if (Peek() is { Kind: TokenKind.Ident } a && !SqlStopWords.Any(w => KwMatch(a, w)) && !KwMatch(a, "WHERE") && !KwMatch(a, "GROUP") && !KwMatch(a, "HAVING"))
            alias = Ident();
        return new SqlSource(table, alias, null);
    }

    private Stmt SqlInsert()
    {
        ExpectKw("INTO");
        var table = SqlTableName();
        List<string>? cols = null;
        if (AcceptOp("("))
        {
            cols = [];
            do cols.Add(Ident()); while (AcceptOp(","));
            ExpectOp(")");
        }
        if (AcceptKw("VALUES"))
        {
            ExpectOp("(");
            var saved = _sql;
            _sql = true;
            var vals = ExprList();
            _sql = saved;
            ExpectOp(")");
            return new SqlInsertStmt(table, cols, vals, null, null, null, null);
        }
        if (AcceptKw("FROM"))
        {
            if (AcceptKw("MEMVAR")) return new SqlInsertStmt(table, cols, null, null, "MEMVAR", null, null);
            if (AcceptKw("NAME")) return new SqlInsertStmt(table, cols, null, null, null, Ident(), null);
            ExpectKw("ARRAY");
            return new SqlInsertStmt(table, cols, null, null, null, null, Ident());
        }
        if (Kw("SELECT")) return new SqlInsertStmt(table, cols, null, Select(), null, null, null);
        throw Error("Expected VALUES, FROM or SELECT.");
    }

    private Expr SqlTableName()
    {
        var t = Next();
        if (t.Kind == TokenKind.Ident)
        {
            var name = t.Text;
            if (IsOp("!") && Peek(1)?.Kind == TokenKind.Ident) { _p++; name += "!" + Ident(); }
            return new LiteralExpr(Value.String(name));
        }
        if (t.Kind == TokenKind.String) return new LiteralExpr(Value.String(t.Text));
        if (t.Kind == TokenKind.Macro) return new MacroExpr(t.Text);
        if (t.IsOp("(")) { var e = Expression(); ExpectOp(")"); return e; }
        throw Error($"Expected a table name but found '{t}'.");
    }

    private Stmt SqlUpdate()
    {
        var saved = _sql;
        _sql = true;
        try
        {
            var table = SqlTableName();
            ExpectKw("SET");
            var sets = new List<(string, Expr)>();
            do
            {
                var col = Ident();
                if (AcceptOp(".")) col = Ident();
                ExpectOp("=");
                sets.Add((col, Expression()));
            } while (AcceptOp(","));
            Expr? where = AcceptKw("WHERE") ? Expression() : null;
            return new SqlUpdateStmt(table, sets, where);
        }
        finally { _sql = saved; }
    }

    private Stmt SqlDelete()
    {
        var saved = _sql;
        _sql = true;
        try
        {
            if (!Kw("FROM")) SqlTableName(); // DELETE target FROM …
            ExpectKw("FROM");
            var table = SqlTableName();
            Expr? where = AcceptKw("WHERE") ? Expression() : null;
            return new SqlDeleteStmt(table, where);
        }
        finally { _sql = saved; }
    }
}
