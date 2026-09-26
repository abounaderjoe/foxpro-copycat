using System.Globalization;
using JoePro.Core;

namespace JoePro.Language;

/// <summary>A &amp;macro used inside an expression; substituted and re-parsed at run time.</summary>
public sealed record MacroExpr(string VarName) : Expr;

/// <summary>
/// Recursive-descent parser for the FoxPro language: procedural statements, xBase data commands,
/// SELECT-SQL and DEFINE CLASS. Command keywords may be abbreviated to four characters.
/// </summary>
public sealed partial class Parser
{
    private readonly List<SourceLine> _lines;
    private int _li;
    private List<Token> _t = [];
    private int _p;
    private int _lineNo;
    private bool _sql;

    private Parser(List<SourceLine> lines) => _lines = lines;

    // ---- Entry points ---------------------------------------------------------------

    public static ProgramUnit ParseProgram(string source, string name = "", string? file = null, Func<string, string?>? includeResolver = null)
    {
        var p = new Parser(Preprocessor.Prepare(source, file, includeResolver));
        return p.Program(name, file);
    }

    /// <summary>Parses statements typed in the Command Window or produced by macro expansion.</summary>
    public static ProgramUnit ParseInteractive(string source) => ParseProgram(source, "(command)");

    public static Expr ParseExpression(string text)
    {
        var p = new Parser([]) { _t = Lexer.Lex(text, 0), _p = 0 };
        var e = p.Expression();
        if (!p.AtEnd) throw p.Error($"Unexpected '{p.Peek()}'.");
        return e;
    }

    private ProgramUnit Program(string name, string? file)
    {
        var unit = new ProgramUnit { Name = name, File = file };
        while (_li < _lines.Count)
        {
            BeginLine();
            if (IsProcStart())
            {
                var proc = ParseProcedure(null);
                unit.Procedures[proc.Name] = proc;
                continue;
            }
            if (Kw(0, "DEFINE") && Kw(1, "CLASS"))
            {
                var cls = ParseClass();
                unit.Classes[cls.Name] = cls;
                continue;
            }
            if (unit.Procedures.Count > 0 || unit.Classes.Count > 0)
            {
                // Code after the first PROCEDURE belongs to procedures; stray lines between blocks are skipped like VFP.
                _li++;
                continue;
            }
            var stmt = Statement();
            if (stmt is ParametersStmt ps && unit.Main.Count == 0)
            {
                unit.MainParameters = ps.Names;
                unit.MainLocalParameters = ps.Local;
            }
            if (stmt != null) unit.Main.Add(stmt);
        }
        return unit;
    }

    // ---- Line and token helpers ----------------------------------------------------------

    private void BeginLine()
    {
        var l = _lines[_li];
        _t = l.Tokens;
        _p = 0;
        _lineNo = l.Number;
    }

    private bool AtEnd => _p >= _t.Count;
    private Token? Peek(int o = 0) => _p + o < _t.Count ? _t[_p + o] : null;
    private Token Next() => _p < _t.Count ? _t[_p++] : throw Error("Unexpected end of line.");

    private CompileException Error(string msg) => new(msg, _lineNo);

    private static bool KwMatch(Token? t, string kw)
    {
        if (t == null || t.Kind != TokenKind.Ident) return false;
        var s = t.Text;
        if (s.Length > kw.Length) return false;
        if (s.Length < kw.Length && s.Length < 4) return false;
        return kw.StartsWith(s, StringComparison.OrdinalIgnoreCase);
    }

    private bool Kw(int offset, string kw) => KwMatch(Peek(offset), kw);
    private bool Kw(string kw) => Kw(0, kw);

    private bool AcceptKw(string kw)
    {
        if (!Kw(kw)) return false;
        _p++;
        return true;
    }

    private void ExpectKw(string kw)
    {
        if (!AcceptKw(kw)) throw Error($"Expected {kw}.");
    }

    private bool IsOp(string op, int o = 0) => Peek(o)?.IsOp(op) == true;

    private bool AcceptOp(string op)
    {
        if (!IsOp(op)) return false;
        _p++;
        return true;
    }

    private void ExpectOp(string op)
    {
        if (!AcceptOp(op)) throw Error(AtEnd ? $"Missing '{op}'." : $"Expected '{op}' but found '{Peek()}'.");
    }

    private string Ident()
    {
        var t = Next();
        if (t.Kind != TokenKind.Ident) throw Error($"Expected a name but found '{t}'.");
        return t.Text;
    }

    private void EndOfStatement()
    {
        if (!AtEnd) throw Error($"Command contains unrecognized phrase/keyword: '{Peek()}'.");
    }

    /// <summary>Returns the raw source text of the current line from token <paramref name="from"/> up to (not including) token <paramref name="to"/>.</summary>
    private string RawText(int from, int to)
    {
        var text = _lines.Count > 0 && _li < _lines.Count ? _lines[_li].Text : "";
        if (from >= _t.Count) return "";
        var start = _t[from].Column;
        var end = to < _t.Count ? _t[to].Column : text.Length;
        return text[start..end].Trim();
    }

    // ---- Blocks ----------------------------------------------------------------------

    private bool LineStartsWith(params string[] kws)
    {
        if (_li >= _lines.Count) return false;
        var toks = _lines[_li].Tokens;
        return toks.Count > 0 && kws.Any(k => KwMatch(toks[0], k));
    }

    /// <summary>Parses statements until a line starting with one of <paramref name="terminators"/> (left unconsumed).</summary>
    private List<Stmt> Block(params string[] terminators)
    {
        var body = new List<Stmt>();
        int startLine = _lineNo;
        while (true)
        {
            if (_li >= _lines.Count) throw new CompileException($"Missing {terminators[0]} for the block started on line {startLine}.", startLine);
            if (LineStartsWith(terminators)) return body;
            BeginLine();
            if (IsProcStart() || (Kw(0, "DEFINE") && Kw(1, "CLASS")))
                throw new CompileException($"Missing {terminators[0]} for the block started on line {startLine}.", startLine);
            var s = Statement();
            if (s != null) body.Add(s);
        }
    }

    private bool IsProcStart()
    {
        int o = 0;
        if (Kw(0, "PROTECTED") || Kw(0, "HIDDEN")) o = 1;
        return Kw(o, "PROCEDURE") || Kw(o, "FUNCTION");
    }

    private ProcedureDef ParseProcedure(ClassDef? owner)
    {
        BeginLine();
        string? vis = null;
        if (AcceptKw("PROTECTED")) vis = "PROTECTED";
        else if (AcceptKw("HIDDEN")) vis = "HIDDEN";
        bool isFunc = Kw("FUNCTION");
        _p++;
        var name = Ident();
        while (AcceptOp(".")) name += "." + Ident(); // PROCEDURE txtName.Valid in form classes
        var parms = new List<string>();
        bool hasParens = false;
        if (AcceptOp("("))
        {
            hasParens = true;
            while (!IsOp(")"))
            {
                if (AcceptOp("@")) { }
                parms.Add(Ident());
                if (AcceptKw("AS")) { Ident(); if (AcceptKw("OF")) Ident(); }
                if (!AcceptOp(",")) break;
            }
            ExpectOp(")");
        }
        if (AcceptKw("AS")) { Ident(); if (AcceptKw("OF")) Ident(); }
        AcceptKw("HELPSTRING");
        int line = _lineNo;
        _li++;
        var body = new List<Stmt>();
        while (_li < _lines.Count)
        {
            if (LineStartsWith("ENDPROC", "ENDFUNC")) { _li++; break; }
            if (LineStartsWith("ENDDEFINE") && owner != null) break;
            BeginLine();
            if (IsProcStart() || (Kw(0, "DEFINE") && Kw(1, "CLASS"))) break;
            var s = Statement();
            if (s != null) body.Add(s);
        }
        return new ProcedureDef(name, parms, hasParens, body, line, vis) { IsFunction = isFunc };
    }

    private ClassDef ParseClass()
    {
        int line = _lineNo;
        _p += 2;
        var name = Ident();
        ExpectKw("AS");
        var parent = Ident();
        string? lib = null;
        if (AcceptKw("OF")) lib = NameArg("OLEPUBLIC") is LiteralExpr { Value.Kind: ValueKind.Character } le ? le.Value.AsString : null;
        AcceptKw("OLEPUBLIC");
        _li++;
        var cls = new ClassDef(name, parent, lib, new(), new(), new(StringComparer.OrdinalIgnoreCase), line);
        while (true)
        {
            if (_li >= _lines.Count) throw new CompileException($"Missing ENDDEFINE for class {name}.", line);
            BeginLine();
            if (Kw("ENDDEFINE")) { _li++; break; }
            if (IsProcStart())
            {
                var m = ParseProcedure(cls);
                cls.Methods[m.Name] = m;
                if (m.Visibility == "PROTECTED") cls.Protected.Add(m.Name);
                if (m.Visibility == "HIDDEN") cls.Hidden.Add(m.Name);
                continue;
            }
            if (Kw("PROTECTED") || Kw("HIDDEN"))
            {
                var set = Kw("PROTECTED") ? cls.Protected : cls.Hidden;
                _p++;
                do set.Add(Ident()); while (AcceptOp(","));
                _li++;
                continue;
            }
            if (Kw("ADD") && Kw(1, "OBJECT"))
            {
                _p += 2;
                bool prot = AcceptKw("PROTECTED");
                var objName = Ident();
                while (AcceptOp(".")) objName += "." + Ident();
                ExpectKw("AS");
                var objClass = Ident();
                bool noInit = AcceptKw("NOINIT");
                var props = new List<(string, Expr)>();
                if (AcceptKw("WITH"))
                {
                    do
                    {
                        var pn = Ident();
                        while (AcceptOp(".")) pn += "." + Ident();
                        ExpectOp("=");
                        props.Add((pn, Expression()));
                    } while (AcceptOp(","));
                }
                cls.Objects.Add(new AddObjectDef(objName, objClass, props, noInit, prot));
                _li++;
                continue;
            }
            if (Kw("DIMENSION") || Kw("DECLARE"))
            {
                _p++;
                do
                {
                    var an = Ident();
                    var dims = SubscriptList();
                    cls.Members.Add(new MemberDef(an, null, dims, null));
                } while (AcceptOp(","));
                _li++;
                continue;
            }
            // property = value (also Name.Property = value for contained objects)
            var pname = Ident();
            while (AcceptOp(".")) pname += "." + Ident();
            List<Expr>? pdims = null;
            if (IsOp("(") || IsOp("[")) pdims = SubscriptList();
            ExpectOp("=");
            var val = Expression();
            cls.Members.Add(new MemberDef(pname, val, pdims, null));
            _li++;
        }
        return cls;
    }

    // ---- Statements ------------------------------------------------------------------

    private Stmt? Statement()
    {
        var line = _lines[_li];
        if (_t.Count == 0) { _li++; return null; }
        if (_t[0].Kind == TokenKind.Macro)
        {
            _li++;
            return new MacroStmt(line.Text) { Line = _lineNo };
        }
        int startLi = _li;
        try
        {
            var s = StatementCore();
            if (s == null) return null;
            return s with { Line = _lines[startLi].Number };
        }
        catch (CompileException) when (line.HasMacro && _li == startLi)
        {
            _li++;
            return new MacroStmt(line.Text) { Line = line.Number };
        }
    }

    private bool IsAssignment()
    {
        // lvalue '=' : name, name.member..., name(…), name[…], m.name, .member (inside WITH)
        int i = 0;
        if (IsOp(".", 0)) i = 1;
        if (Peek(i)?.Kind != TokenKind.Ident) return false;
        i++;
        while (true)
        {
            var t = Peek(i);
            if (t == null) return false;
            if (t.IsOp("=")) return true;
            if (t.IsOp(".") && Peek(i + 1)?.Kind == TokenKind.Ident) { i += 2; continue; }
            if (t.IsOp("(") || t.IsOp("["))
            {
                int depth = 0;
                for (; i < _t.Count; i++)
                {
                    if (_t[i].IsOp("(") || _t[i].IsOp("[")) depth++;
                    else if (_t[i].IsOp(")") || _t[i].IsOp("]")) { depth--; if (depth == 0) { i++; break; } }
                }
                continue;
            }
            return false;
        }
    }

    private Stmt? StatementCore()
    {
        if (IsOp("?") || IsOp("??"))
        {
            bool nl = Next().Text == "?";
            var items = new List<Expr>();
            while (!AtEnd)
            {
                items.Add(Expression());
                // AT/PICTURE/FUNCTION/FONT/STYLE display clauses are accepted and ignored.
                while (Kw("AT") || Kw("PICTURE") || Kw("FUNCTION") || Kw("FONT") || Kw("STYLE")) { _p++; Expression(); if (AcceptOp(",")) Expression(); }
                if (!AcceptOp(",")) break;
            }
            EndOfStatement();
            _li++;
            return new PrintStmt(nl, items);
        }
        if (IsOp("="))
        {
            _p++;
            var e = Expression();
            EndOfStatement();
            _li++;
            return new ExprStmt(e);
        }
        if (IsAssignment() && !Kw("STORE"))
        {
            var target = Postfix(Primary());
            ExpectOp("=");
            var value = Expression();
            EndOfStatement();
            _li++;
            return new AssignStmt(target, value);
        }
        if (IsOp("@"))
        {
            _li++;
            return new NoOpStmt("@ SAY/GET");
        }
        if (IsOp(".") && Peek(1)?.Kind == TokenKind.Ident)
        {
            // .Method() inside WITH … ENDWITH
            var e = Postfix(Primary());
            EndOfStatement();
            _li++;
            return new ExprStmt(e);
        }

        var verb = Peek();
        if (verb == null || verb.Kind != TokenKind.Ident) throw Error($"Unrecognized command verb '{verb}'.");

        // Bare function or method call as a statement: MyFunc(1), obj.Method()
        if ((IsOp("(", 1) && !IsCommandWord(verb)) || (IsOp(".", 1) && !Kw("SET") && !Kw("USE")))
        {
            var e = Postfix(Primary());
            if (AtEnd)
            {
                _li++;
                return new ExprStmt(e);
            }
            _p = 0;
        }
        return Command();
    }

    private static readonly string[] CommandWords =
    [
        "IF", "DO", "FOR", "SCAN", "WAIT", "USE", "SELECT", "SKIP", "SEEK", "REPLACE", "RETURN", "ERROR", "CASE", "WHILE",
        "INSERT", "DELETE", "UPDATE", "STORE", "LOCAL", "PUBLIC", "PRIVATE", "DIMENSION", "DECLARE", "LOCATE", "APPEND",
        "INDEX", "CREATE", "SET", "COUNT", "SUM", "AVERAGE", "CALCULATE", "RELEASE", "THROW", "COPY", "IMPORT", "WITH",
    ];

    private static bool IsCommandWord(Token t) => CommandWords.Any(w => KwMatch(t, w));

    private Stmt? Command()
    {
        int line = _lineNo;
        if (Kw("IF")) return If();
        if (Kw("DO") && Kw(1, "CASE")) return DoCase();
        if (Kw("DO") && Kw(1, "WHILE")) { _p += 2; return While(); }
        if (Kw("WHILE") && _t.Count > 1 && !IsOp("=", 1)) { _p++; return While(); }
        if (Kw("FOR") && Kw(1, "EACH")) return ForEach();
        if (Kw("FOR")) return For();
        if (Kw("SCAN")) return Scan();
        if (Kw("TRY")) return Try();
        if (Kw("WITH")) return With();
        if (Kw("TEXT")) return Text();

        var verb = Next();
        Stmt s;
        if (KwMatch(verb, "EXIT")) s = new ExitStmt();
        else if (KwMatch(verb, "LOOP")) s = new LoopStmt();
        else if (KwMatch(verb, "RETURN"))
        {
            bool toMaster = AcceptKw("TO") && AcceptKw("MASTER");
            s = new ReturnStmt(AtEnd ? null : Expression(), toMaster);
        }
        else if (KwMatch(verb, "LOCAL") && (Kw("ARRAY") || !(AtEnd || Kw("FOR") || Kw("ALL") || Kw("NEXT") || Kw("REST") || Kw("WHILE") || Kw("RECORD"))))
            s = Declare("LOCAL");
        else if (KwMatch(verb, "LOCATE")) s = new LocateStmt(ParseScope());
        else if (KwMatch(verb, "PUBLIC")) s = Declare("PUBLIC");
        else if (KwMatch(verb, "PRIVATE"))
        {
            if (AcceptKw("ALL"))
            {
                string? like = null;
                bool except = false;
                if (AcceptKw("LIKE")) like = RawText(_p, _t.Count);
                else if (AcceptKw("EXCEPT")) { except = true; like = RawText(_p, _t.Count); }
                _p = _t.Count;
                s = new PrivateAllStmt(like, except);
            }
            else s = Declare("PRIVATE");
        }
        else if (KwMatch(verb, "DIMENSION")) s = Declare("DIMENSION");
        else if (KwMatch(verb, "DECLARE"))
        {
            if (IsDllDeclare()) { _p = _t.Count; s = new NoOpStmt("DECLARE DLL"); }
            else s = Declare("DIMENSION");
        }
        else if (KwMatch(verb, "STORE"))
        {
            var v = Expression();
            ExpectKw("TO");
            var targets = new List<Expr>();
            do targets.Add(Postfix(Primary())); while (AcceptOp(","));
            s = new StoreStmt(v, targets);
        }
        else if (KwMatch(verb, "PARAMETERS") || KwMatch(verb, "LPARAMETERS"))
        {
            var names = new List<string>();
            while (!AtEnd)
            {
                names.Add(Ident());
                if (AcceptKw("AS")) { Ident(); if (AcceptKw("OF")) Ident(); }
                if (!AcceptOp(",")) break;
            }
            s = new ParametersStmt(names, KwMatch(verb, "LPARAMETERS"));
        }
        else if (KwMatch(verb, "DO")) s = Do();
        else if (KwMatch(verb, "THROW")) s = new ThrowStmt(AtEnd ? null : Expression());
        else if (KwMatch(verb, "ERROR")) s = new ErrorStmt(AtEnd ? [] : ExprList());
        else if (KwMatch(verb, "RELEASE")) s = Release();
        else if (KwMatch(verb, "ON")) s = On();
        else if (KwMatch(verb, "NODEFAULT")) s = new ExprStmt(new CallExpr("NODEFAULT", []));
        else if (KwMatch(verb, "READ"))
        {
            ExpectKw("EVENTS");
            s = new ReadEventsStmt(false);
        }
        else if (KwMatch(verb, "CLEAR")) s = Clear();
        else if (KwMatch(verb, "QUIT")) s = new QuitStmt(false);
        else if (KwMatch(verb, "CANCEL")) s = new QuitStmt(true);
        else if (KwMatch(verb, "SUSPEND") || KwMatch(verb, "RESUME")) { s = new NoOpStmt(verb.Text.ToUpperInvariant()); }
        else if (KwMatch(verb, "WAIT")) s = Wait();
        else s = DataCommand(verb) ?? throw Error($"Unrecognized command verb '{verb.Text}'.");

        if (s is not IfStmt and not DoCaseStmt and not DoWhileStmt and not ForStmt and not ForEachStmt and not ScanStmt and not TryStmt and not WithStmt and not TextStmt)
        {
            EndOfStatement();
            _li++;
        }
        return s;
    }

    private bool IsDllDeclare()
    {
        // DECLARE [type] FunctionName IN library
        for (int i = _p; i < _t.Count; i++) if (KwMatch(_t[i], "IN") && i > _p) return !IsOp("(", 1) && !IsOp("[", 1);
        return false;
    }

    private DeclareStmt Declare(string scope)
    {
        AcceptKw("ARRAY");
        var vars = new List<VarDecl>();
        do
        {
            var name = Ident();
            List<Expr>? dims = null;
            if (IsOp("(") || IsOp("[")) dims = SubscriptList();
            string? type = null;
            if (AcceptKw("AS"))
            {
                type = Ident();
                if (AcceptKw("OF")) NameArg();
            }
            vars.Add(new VarDecl(name, dims, type));
        } while (AcceptOp(","));
        return new DeclareStmt(scope, vars);
    }

    private List<Expr> SubscriptList()
    {
        var close = IsOp("[") ? "]" : ")";
        _p++;
        var list = new List<Expr>();
        do list.Add(Expression()); while (AcceptOp(","));
        ExpectOp(close);
        return list;
    }

    private List<Expr> ExprList()
    {
        var list = new List<Expr>();
        do list.Add(Expression()); while (AcceptOp(","));
        return list;
    }

    private Stmt If()
    {
        _p++;
        var cond = Expression();
        AcceptKw("THEN");
        EndOfStatement();
        _li++;
        var then = Block("ELSE", "ENDIF");
        List<Stmt>? els = null;
        if (LineStartsWith("ELSE"))
        {
            BeginLine();
            _p = 1;
            if (Kw("IF"))
            {
                // ELSE IF on one line is not VFP syntax; treat as nested IF requiring its own ENDIF.
                throw Error("ELSE IF is not supported; use DO CASE or a nested IF.");
            }
            _li++;
            els = Block("ENDIF");
        }
        _li++;
        return new IfStmt(cond, then, els);
    }

    private Stmt DoCase()
    {
        _li++;
        var cases = new List<CaseClause>();
        List<Stmt>? otherwise = null;
        // Lines before the first CASE are ignored (only comments are allowed there).
        while (_li < _lines.Count && !LineStartsWith("CASE", "OTHERWISE", "ENDCASE")) _li++;
        while (true)
        {
            if (_li >= _lines.Count) throw Error("Missing ENDCASE.");
            BeginLine();
            if (Kw("ENDCASE")) { _li++; break; }
            if (Kw("OTHERWISE"))
            {
                _li++;
                otherwise = Block("ENDCASE");
                continue;
            }
            ExpectKw("CASE");
            var cond = Expression();
            EndOfStatement();
            _li++;
            cases.Add(new CaseClause(cond, Block("CASE", "OTHERWISE", "ENDCASE")));
        }
        return new DoCaseStmt(cases, otherwise);
    }

    private Stmt While()
    {
        var cond = Expression();
        EndOfStatement();
        _li++;
        var body = Block("ENDDO");
        _li++;
        return new DoWhileStmt(cond, body);
    }

    private Stmt For()
    {
        _p++;
        var v = Ident();
        ExpectOp("=");
        var from = Expression();
        ExpectKw("TO");
        var to = Expression();
        Expr? step = null;
        if (AcceptKw("STEP")) step = Expression();
        EndOfStatement();
        _li++;
        var body = Block("ENDFOR", "NEXT");
        _li++;
        return new ForStmt(v, from, to, step, body);
    }

    private Stmt ForEach()
    {
        _p += 2;
        var v = Ident();
        if (AcceptKw("AS")) { Ident(); if (AcceptKw("OF")) NameArg(); }
        ExpectKw("IN");
        var coll = Expression();
        AcceptKw("FOXOBJECT");
        EndOfStatement();
        _li++;
        var body = Block("ENDFOR", "NEXT");
        _li++;
        return new ForEachStmt(v, coll, body);
    }

    private Stmt Scan()
    {
        _p++;
        var scope = ParseScope();
        EndOfStatement();
        _li++;
        var body = Block("ENDSCAN");
        _li++;
        return new ScanStmt(scope, body);
    }

    private Stmt Try()
    {
        _li++;
        var body = Block("CATCH", "FINALLY", "ENDTRY");
        string? catchVar = null;
        Expr? when = null;
        List<Stmt>? catchBody = null, finallyBody = null;
        if (LineStartsWith("CATCH"))
        {
            BeginLine();
            _p = 1;
            if (AcceptKw("TO")) catchVar = Ident();
            if (AcceptKw("WHEN")) when = Expression();
            EndOfStatement();
            _li++;
            catchBody = Block("FINALLY", "ENDTRY");
        }
        if (LineStartsWith("FINALLY"))
        {
            _li++;
            finallyBody = Block("ENDTRY");
        }
        _li++;
        return new TryStmt(body, catchVar, when, catchBody, finallyBody);
    }

    private Stmt With()
    {
        _p++;
        var target = Expression();
        AcceptKw("AS");
        if (!AtEnd && Peek()!.Kind == TokenKind.Ident) { _p++; if (AcceptKw("OF")) NameArg(); }
        EndOfStatement();
        _li++;
        var body = Block("ENDWITH");
        _li++;
        return new WithStmt(target, body);
    }

    private Stmt Text()
    {
        var line = _lines[_li];
        _p++;
        string? toVar = null;
        bool additive = false, noShow = false, merge = false, pretext = false;
        while (!AtEnd)
        {
            if (AcceptKw("TO")) toVar = Ident();
            else if (AcceptKw("ADDITIVE")) additive = true;
            else if (AcceptKw("TEXTMERGE")) merge = true;
            else if (AcceptKw("NOSHOW")) noShow = true;
            else if (AcceptKw("FLAGS")) Expression();
            else if (AcceptKw("PRETEXT")) { Expression(); pretext = true; }
            else throw Error($"Unrecognized TEXT clause '{Peek()}'.");
        }
        _li++;
        return new TextStmt(toVar, additive, noShow, merge, pretext, line.TextBlock ?? []);
    }

    private Stmt Do()
    {
        if (AcceptKw("FORM"))
        {
            var form = NameArg("NAME", "WITH", "LINKED", "NOSHOW", "TO", "NOREAD");
            var args = new List<Expr>();
            string? nameVar = null, toVar = null;
            bool linked = false, noShow = false;
            while (!AtEnd)
            {
                if (AcceptKw("WITH")) args = ArgList();
                else if (AcceptKw("NAME")) { nameVar = Ident(); while (AcceptOp(".")) nameVar += "." + Ident(); }
                else if (AcceptKw("LINKED")) linked = true;
                else if (AcceptKw("NOSHOW")) noShow = true;
                else if (AcceptKw("NOREAD")) { }
                else if (AcceptKw("TO")) toVar = Ident();
                else throw Error($"Unrecognized DO FORM clause '{Peek()}'.");
            }
            return new DoFormStmt(form, args, nameVar, linked, noShow, toVar);
        }
        var target = NameArg("IN", "WITH");
        string? inFile = null;
        var argsList = new List<Expr>();
        if (AcceptKw("IN")) inFile = NameArg("WITH") is LiteralExpr { Value.Kind: ValueKind.Character } l ? l.Value.AsString : null;
        if (AcceptKw("WITH")) argsList = ArgList();
        return new DoStmt(target, inFile, argsList);
    }

    private List<Expr> ArgList()
    {
        var list = new List<Expr>();
        do
        {
            if (IsOp(",") || AtEnd) list.Add(new EmptyArgExpr());
            else list.Add(Expression());
        } while (AcceptOp(","));
        return list;
    }

    private Stmt Release()
    {
        if (AcceptKw("ALL"))
        {
            string? like = null;
            bool except = false;
            if (AcceptKw("LIKE")) like = RawText(_p, _t.Count);
            else if (AcceptKw("EXCEPT")) { except = true; like = RawText(_p, _t.Count); }
            _p = _t.Count;
            return new ReleaseStmt([], true, like, except);
        }
        if (AcceptKw("WINDOWS") || AcceptKw("POPUPS") || AcceptKw("MENUS") || AcceptKw("PADS") || AcceptKw("BAR") || AcceptKw("CLASSLIB") || AcceptKw("LIBRARY") || AcceptKw("PROCEDURE"))
        {
            _p = _t.Count;
            return new NoOpStmt("RELEASE");
        }
        var names = new List<string>();
        do names.Add(Ident()); while (AcceptOp(","));
        return new ReleaseStmt(names, false, null, false);
    }

    private Stmt On()
    {
        if (AcceptKw("ERROR"))
        {
            var cmd = AtEnd ? null : RawText(_p, _t.Count);
            _p = _t.Count;
            return new OnErrorStmt(cmd);
        }
        _p = _t.Count; // ON KEY LABEL, ON SHUTDOWN, ON ESCAPE… (UI runtime; Phase 3)
        return new NoOpStmt("ON");
    }

    private Stmt Clear()
    {
        if (AcceptKw("EVENTS")) return new ReadEventsStmt(true);
        string? what = AtEnd ? null : Next().Text.ToUpperInvariant();
        _p = _t.Count;
        return new ClearStmt(what);
    }

    private Stmt Wait()
    {
        Expr? msg = null;
        string? toVar = null;
        bool window = false, noWait = false, clear = false;
        Expr? timeout = null;
        while (!AtEnd)
        {
            if (AcceptKw("WINDOW"))
            {
                window = true;
                if (AcceptKw("AT")) { Expression(); ExpectOp(","); Expression(); }
            }
            else if (AcceptKw("TO")) toVar = Ident();
            else if (AcceptKw("NOWAIT")) noWait = true;
            else if (AcceptKw("NOCLEAR")) { }
            else if (AcceptKw("CLEAR")) clear = true;
            else if (AcceptKw("TIMEOUT")) timeout = Expression();
            else msg = Expression();
        }
        return new WaitStmt(msg, toVar, window, noWait, timeout, clear);
    }

    // ---- Scope clauses and name arguments --------------------------------------------------

    private static readonly string[] ScopeWords = ["ALL", "NEXT", "RECORD", "REST", "FOR", "WHILE", "IN", "NOOPTIMIZE"];

    private Scope ParseScope(bool allowIn = true, params string[] stopAt)
    {
        var kind = "DEFAULT";
        Expr? count = null, @for = null, @while = null, @in = null;
        while (!AtEnd)
        {
            if (stopAt.Any(Kw)) break;
            if (AcceptKw("ALL")) kind = "ALL";
            else if (AcceptKw("REST")) kind = "REST";
            else if (Kw("NEXT")) { _p++; kind = "NEXT"; count = Expression(); }
            else if (Kw("RECORD")) { _p++; kind = "RECORD"; count = Expression(); }
            else if (AcceptKw("FOR")) @for = Expression();
            else if (AcceptKw("WHILE")) @while = Expression();
            else if (allowIn && AcceptKw("IN")) @in = AliasArg();
            else if (AcceptKw("NOOPTIMIZE")) { }
            else break;
        }
        return new Scope(kind, count, @for, @while, @in);
    }

    /// <summary>A work area reference: a number, an alias name, or an expression in parentheses.</summary>
    private Expr AliasArg()
    {
        var t = Peek() ?? throw Error("Missing alias.");
        if (t.Kind == TokenKind.Number) { _p++; return new LiteralExpr(Value.Number(t.Number)); }
        if (t.Kind == TokenKind.Ident && !IsOp("(", 1)) { _p++; return new LiteralExpr(Value.String(t.Text)); }
        if (t.Kind == TokenKind.Macro) { _p++; return new MacroExpr(t.Text); }
        return Primary();
    }

    /// <summary>
    /// A name argument such as a file or table name: (expr), a string, a &amp;macro, or raw text
    /// up to the next clause keyword (so paths like c:\data\cust.dbf work unquoted).
    /// </summary>
    private Expr NameArg(params string[] stopWords)
    {
        var t = Peek() ?? throw Error("Missing name.");
        if (t.IsOp("(")) return Primary();
        if (t.Kind == TokenKind.String) { _p++; return new LiteralExpr(Value.String(t.Text)); }
        if (t.Kind == TokenKind.Macro && (Peek(1) == null || stopWords.Any(w => Kw(1, w)))) { _p++; return new MacroExpr(t.Text); }
        int start = _p;
        while (!AtEnd && !stopWords.Any(Kw) && !(IsOp(",") && _p > start)) _p++;
        if (_p == start) throw Error("Missing name.");
        return new LiteralExpr(Value.String(RawText(start, _p)));
    }

    // ---- Expressions ------------------------------------------------------------------

    public Expr Expression() => Or();

    private Expr Or()
    {
        var l = And();
        while (Peek() is { } t && (t.IsOp("OR") || KwExact(t, "OR")))
        {
            _p++;
            l = new BinaryExpr("OR", l, And());
        }
        return l;
    }

    private static bool KwExact(Token t, string kw) => t.Kind == TokenKind.Ident && t.Text.Equals(kw, StringComparison.OrdinalIgnoreCase);

    private Expr And()
    {
        var l = Not();
        while (Peek() is { } t && (t.IsOp("AND") || KwExact(t, "AND")))
        {
            _p++;
            l = new BinaryExpr("AND", l, Not());
        }
        return l;
    }

    private Expr Not()
    {
        if (Peek() is { } t && (t.IsOp("NOT") || t.IsOp("!") || KwExact(t, "NOT")))
        {
            _p++;
            return new UnaryExpr("NOT", Not());
        }
        return Comparison();
    }

    private Expr Comparison()
    {
        if (_sql && KwExact(Peek() ?? new Token(TokenKind.Op, "", 0), "EXISTS"))
        {
            _p++;
            ExpectOp("(");
            var q = Select();
            ExpectOp(")");
            return new ExistsExpr(q);
        }
        var l = Additive();
        while (true)
        {
            var t = Peek();
            if (t == null) return l;
            if (t.Kind == TokenKind.Op && t.Text is "=" or "==" or "<>" or "!=" or "#" or "<" or ">" or "<=" or ">=" or "$")
            {
                _p++;
                var op = t.Text is "!=" or "#" ? "<>" : t.Text;
                l = new BinaryExpr(op, l, Additive());
                continue;
            }
            if (!_sql) return l;
            bool not = false;
            int save = _p;
            if (KwExact(t, "NOT")) { not = true; _p++; t = Peek(); if (t == null) { _p = save; return l; } }
            if (KwExact(t, "IN"))
            {
                _p++;
                ExpectOp("(");
                if (Kw("SELECT"))
                {
                    var q = Select();
                    ExpectOp(")");
                    l = new InListExpr(l, [new SubqueryExpr(q)], not);
                }
                else
                {
                    var items = ExprList();
                    ExpectOp(")");
                    l = new InListExpr(l, items, not);
                }
                continue;
            }
            if (KwExact(t, "LIKE")) { _p++; l = new LikeExpr(l, Additive(), not); continue; }
            if (KwExact(t, "BETWEEN"))
            {
                _p++;
                var lo = Additive();
                if (!(Peek() is { } a && (a.IsOp("AND") || KwExact(a, "AND")))) throw Error("Expected AND in BETWEEN.");
                _p++;
                l = new BetweenExpr(l, lo, Additive(), not);
                continue;
            }
            if (!not && KwExact(t, "IS"))
            {
                _p++;
                bool isNot = false;
                if (Peek() is { } n && KwExact(n, "NOT")) { isNot = true; _p++; }
                if (!(Peek() is { } nu && (nu.Kind == TokenKind.Null || KwExact(nu, "NULL")))) throw Error("Expected NULL.");
                _p++;
                l = new IsNullExpr(l, isNot);
                continue;
            }
            _p = save;
            return l;
        }
    }

    private Expr Additive()
    {
        var l = Multiplicative();
        while (Peek() is { Kind: TokenKind.Op } t && t.Text is "+" or "-")
        {
            _p++;
            l = new BinaryExpr(t.Text, l, Multiplicative());
        }
        return l;
    }

    private Expr Multiplicative()
    {
        var l = Unary();
        while (Peek() is { Kind: TokenKind.Op } t && t.Text is "*" or "/" or "%")
        {
            _p++;
            l = new BinaryExpr(t.Text, l, Unary());
        }
        return l;
    }

    // TODO(oracle): confirm whether -2^2 evaluates to 4 or -4 in VFP 9.
    private Expr Unary()
    {
        if (Peek() is { Kind: TokenKind.Op } t && t.Text is "-" or "+")
        {
            _p++;
            var operand = Unary();
            return t.Text == "-" ? new UnaryExpr("-", operand) : operand;
        }
        return Power();
    }

    private Expr Power()
    {
        var l = Postfix(Primary());
        if (Peek() is { Kind: TokenKind.Op } t && t.Text is "^" or "**")
        {
            _p++;
            return new BinaryExpr("^", l, Unary());
        }
        return l;
    }

    private Expr Primary()
    {
        var t = Next();
        switch (t.Kind)
        {
            case TokenKind.Number:
                return new LiteralExpr(t.IsCurrency ? Value.Currency((decimal)t.Number) : Value.Number(t.Number, t.Decimals));
            case TokenKind.String:
                return new LiteralExpr(Value.String(t.Text));
            case TokenKind.Logical:
                return new LiteralExpr(Value.Logical(t.Text == ".T."));
            case TokenKind.Null:
                return new LiteralExpr(Value.Null);
            case TokenKind.Date:
                return new LiteralExpr(ParseDateLiteral(t.Text));
            case TokenKind.Macro:
                return new MacroExpr(t.Text);
            case TokenKind.Op when t.Text == "(":
            {
                if (_sql && Kw("SELECT"))
                {
                    var q = Select();
                    ExpectOp(")");
                    return new SubqueryExpr(q);
                }
                var e = Expression();
                ExpectOp(")");
                return e;
            }
            case TokenKind.Op when t.Text == "@":
                return new ByRefExpr(Ident());
            case TokenKind.Op when t.Text == ".":
                // .Member inside WITH … ENDWITH
                return new MemberExpr(new SpecialObjectExpr("WITH"), Ident());
            case TokenKind.Op when t.Text == "!" || t.Text == "NOT":
                return new UnaryExpr("NOT", Unary());
            case TokenKind.Ident:
            {
                var name = t.Text;
                var upper = name.ToUpperInvariant();
                if (upper == "M" && IsOp(".") && Peek(1)?.Kind == TokenKind.Ident)
                {
                    _p++;
                    return new MemVarExpr(Ident());
                }
                if (upper is "THIS" or "THISFORM" or "THISFORMSET" or "_SCREEN" or "_VFP" or "_JOEPRO")
                    return new SpecialObjectExpr(upper);
                if (_sql && upper == "NULL") return new LiteralExpr(Value.Null);
                if (IsOp("("))
                {
                    _p++;
                    var args = new List<Expr>();
                    if (!IsOp(")"))
                    {
                        if (_sql && upper is "COUNT" && IsOp("*")) { _p++; args.Add(new LiteralExpr(Value.String("*"))); }
                        else
                        {
                            if (_sql && Kw("DISTINCT")) { _p++; name = name + "_DISTINCT"; }
                            args = ArgList();
                        }
                    }
                    ExpectOp(")");
                    return new CallExpr(name, args);
                }
                if (IsOp("["))
                {
                    var subs = SubscriptList();
                    return new IndexExpr(new NameExpr(name), subs);
                }
                if (IsOp("->") && Peek(1)?.Kind == TokenKind.Ident)
                {
                    _p++;
                    return new AliasFieldExpr(name, Ident());
                }
                return new NameExpr(name);
            }
        }
        throw Error($"Unexpected '{t}'.");
    }

    private Expr Postfix(Expr e)
    {
        while (true)
        {
            if (IsOp(".") && Peek(1)?.Kind == TokenKind.Ident)
            {
                _p++;
                var name = Ident();
                if (IsOp("("))
                {
                    _p++;
                    var args = IsOp(")") ? new List<Expr>() : ArgList();
                    ExpectOp(")");
                    e = new MethodCallExpr(e, name, args);
                }
                else e = new MemberExpr(e, name);
                continue;
            }
            if (IsOp("[") && e is MemberExpr)
            {
                e = new IndexExpr(e, SubscriptList());
                continue;
            }
            return e;
        }
    }

    public static Value ParseDateLiteral(string text)
    {
        var s = text.Trim();
        if (s.Length == 0 || s == "/" || s == "//" || s == "-" || s == ".") return Value.EmptyDate;
        if (s is ":" or "/:" or "//:" or "^:") return Value.EmptyDateTime;
        bool strict = s.StartsWith('^');
        if (strict) s = s[1..].Trim();
        string datePart = s, timePart = "";
        var sep = s.IndexOfAny([' ', ',', 'T']);
        if (sep > 0) { datePart = s[..sep]; timePart = s[(sep + 1)..].Trim(); }
        long julian;
        if (strict)
        {
            var parts = datePart.Split(['-', '/', '.'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) throw new CompileException($"Invalid date literal {{{text}}}.", 0);
            julian = Julian.FromDate(new DateOnly(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2])));
        }
        else
        {
            julian = Formatter.ParseDate(datePart, SetOptions.Default);
            if (julian == 0) throw new CompileException($"Invalid date literal {{{text}}}.", 0);
        }
        if (timePart.Length == 0 && sep < 0) return Value.FromJulian(julian);
        return Value.DateTimeFromJulianMs(julian * Julian.MsPerDay + ParseTime(timePart));
    }

    private static long ParseTime(string s)
    {
        if (s.Length == 0) return 0;
        bool pm = false, am = false;
        var u = s.ToUpperInvariant();
        if (u.EndsWith("PM") || u.EndsWith("P")) { pm = true; u = u.TrimEnd('M').TrimEnd('P').Trim(); }
        else if (u.EndsWith("AM") || u.EndsWith("A")) { am = true; u = u.TrimEnd('M').TrimEnd('A').Trim(); }
        var p = u.Split(':');
        int h = int.Parse(p[0], CultureInfo.InvariantCulture);
        int m = p.Length > 1 ? int.Parse(p[1], CultureInfo.InvariantCulture) : 0;
        int sec = p.Length > 2 ? int.Parse(p[2], CultureInfo.InvariantCulture) : 0;
        if (pm && h < 12) h += 12;
        if (am && h == 12) h = 0;
        return ((h * 60L + m) * 60 + sec) * 1000;
    }
}
