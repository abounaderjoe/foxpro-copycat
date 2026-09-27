using JoePro.Core;
using JoePro.Language;

namespace JoePro.Tests.Language;

public class ParserTests
{
    private static List<Stmt> Parse(string src) => Parser.ParseProgram(src).Main;

    [Fact]
    public void Keywords_can_be_abbreviated_to_four_characters()
    {
        var s = Parse("REPL name WITH 'x' FOR id = 1\nDIME a(3)\nSELE 2");
        Assert.IsType<ReplaceStmt>(s[0]);
        Assert.IsType<DeclareStmt>(s[1]);
        Assert.IsType<SelectAreaStmt>(s[2]);
    }

    [Fact]
    public void Comments_and_continuations()
    {
        var s = Parse("* comment\nx = 1 + ;\n    2 && trailing\nNOTE another");
        var a = Assert.IsType<AssignStmt>(Assert.Single(s));
        Assert.IsType<BinaryExpr>(a.Value);
    }

    [Fact]
    public void Bracket_strings_and_subscripts()
    {
        var s = Parse("? [hello]\nDIMENSION arr[2]\narr[1] = 5\nx = arr[1]");
        var print = Assert.IsType<PrintStmt>(s[0]);
        Assert.Equal("hello", ((LiteralExpr)print.Items[0]).Value.AsString);
        var assign = Assert.IsType<AssignStmt>(s[2]);
        Assert.IsType<IndexExpr>(assign.Target);
    }

    [Fact]
    public void Operator_precedence()
    {
        var e = Parser.ParseExpression("1 + 2 * 3 = 7 AND NOT .F. OR x $ 'abc'");
        var or = Assert.IsType<BinaryExpr>(e);
        Assert.Equal("OR", or.Op);
        var and = Assert.IsType<BinaryExpr>(or.Left);
        Assert.Equal("AND", and.Op);
        var eq = Assert.IsType<BinaryExpr>(and.Left);
        Assert.Equal("=", eq.Op);
    }

    [Fact]
    public void Dot_operators_and_logicals()
    {
        var e = Parser.ParseExpression("a .AND. .NOT. b .OR. .T.");
        Assert.Equal("OR", Assert.IsType<BinaryExpr>(e).Op);
    }

    [Fact]
    public void Date_literals()
    {
        var d = ((LiteralExpr)Parser.ParseExpression("{^2024-01-31}")).Value;
        Assert.Equal(new DateOnly(2024, 1, 31), Julian.ToDate(d.JulianDay));
        var t = ((LiteralExpr)Parser.ParseExpression("{^2024-01-31 14:30:15}")).Value;
        Assert.Equal(ValueKind.DateTime, t.Kind);
        Assert.True(((LiteralExpr)Parser.ParseExpression("{}")).Value.IsEmptyDate);
    }

    [Fact]
    public void Control_structures()
    {
        var src = """
            IF x > 1
               y = 1
            ELSE
               y = 2
            ENDIF
            DO CASE
            CASE x = 1
               z = 1
            OTHERWISE
               z = 0
            ENDCASE
            FOR i = 1 TO 10 STEP 2
               LOOP
            ENDFOR
            DO WHILE .T.
               EXIT
            ENDDO
            SCAN FOR active
            ENDSCAN
            TRY
               THROW "x"
            CATCH TO ex WHEN .T.
            FINALLY
            ENDTRY
            """;
        var s = Parse(src);
        Assert.Collection(s,
            x => Assert.IsType<IfStmt>(x),
            x => Assert.Equal(1, Assert.IsType<DoCaseStmt>(x).Cases.Count),
            x => Assert.NotNull(Assert.IsType<ForStmt>(x).Step),
            x => Assert.IsType<DoWhileStmt>(x),
            x => Assert.NotNull(Assert.IsType<ScanStmt>(x).Scope.For),
            x => Assert.Equal("ex", Assert.IsType<TryStmt>(x).CatchVar));
    }

    [Fact]
    public void Procedures_and_classes()
    {
        var src = """
            x = Add(1, 2)
            FUNCTION Add(a, b)
               RETURN a + b
            ENDFUNC
            PROCEDURE Hello
               LPARAMETERS cName
               ? "Hi " + cName
            DEFINE CLASS Person AS Custom
               Name = "nobody"
               PROTECTED Secret
               ADD OBJECT oTimer AS Timer WITH Interval = 100
               PROCEDURE Greet(cOther)
                  RETURN "Hi " + cOther
               ENDPROC
            ENDDEFINE
            """;
        var u = Parser.ParseProgram(src);
        Assert.Single(u.Main);
        Assert.Equal(["a", "b"], u.Procedures["ADD"].Parameters);
        Assert.Equal(2, u.Procedures["Hello"].Body.Count);
        var c = u.Classes["Person"];
        Assert.Equal("Custom", c.Parent);
        Assert.Contains("Secret", c.Protected);
        Assert.Single(c.Objects);
        Assert.True(c.Methods.ContainsKey("Greet"));
    }

    [Fact]
    public void Sql_select_with_joins_grouping_and_into()
    {
        var s = Parse("SELECT c.name, SUM(o.amt) AS total FROM customer c INNER JOIN orders o ON o.custid = c.id WHERE o.amt > 0 GROUP BY c.name HAVING total > 10 ORDER BY 2 DESC INTO CURSOR q");
        var q = Assert.IsType<SqlSelectStmt>(Assert.Single(s)).Query;
        Assert.Equal(2, q.Columns.Count);
        Assert.Equal("total", q.Columns[1].Alias);
        Assert.Single(q.From);
        Assert.Equal("c", q.From[0].Alias);
        Assert.Equal("INNER", Assert.Single(q.Joins).Kind);
        Assert.NotNull(q.Where);
        Assert.Single(q.GroupBy);
        Assert.NotNull(q.Having);
        Assert.True(q.OrderBy[0].Desc);
        Assert.Equal(("CURSOR", "q"), (q.IntoKind, q.IntoName));
    }

    [Fact]
    public void Xbase_select_vs_sql_select()
    {
        Assert.IsType<SelectAreaStmt>(Parse("SELECT customer")[0]);
        Assert.IsType<SelectAreaStmt>(Parse("SELECT 0")[0]);
        Assert.IsType<SqlSelectStmt>(Parse("SELECT * FROM customer")[0]);
    }

    [Fact]
    public void Create_table_with_field_clauses()
    {
        var s = Parse("CREATE TABLE cust (id I AUTOINC PRIMARY KEY, name C(30) NOT NULL, bal Y DEFAULT 0, notes Memo NULL)");
        var ct = Assert.IsType<CreateTableStmt>(s[0]);
        Assert.Equal(4, ct.Fields.Count);
        Assert.True(ct.Fields[0].AutoInc && ct.Fields[0].PrimaryKey);
        Assert.Equal(('C', 30), (ct.Fields[1].Type, ct.Fields[1].Width));
        Assert.Equal('M', ct.Fields[3].Type);
        Assert.True(ct.Fields[3].Null);
    }

    [Fact]
    public void Use_with_clauses_and_paths()
    {
        var u = Assert.IsType<UseStmt>(Parse("USE data\\customer IN 0 ALIAS cust ORDER TAG name SHARED")[0]);
        Assert.Equal("data\\customer", ((LiteralExpr)u.Table!).Value.AsString);
        Assert.Equal("cust", ((LiteralExpr)u.Alias!).Value.AsString);
        Assert.False(u.Exclusive);
    }

    [Fact]
    public void Macro_lines_fall_back_to_runtime_substitution()
    {
        var s = Parse("&lcCommand\nSET EXACT &lcState\nUSE &lcTable");
        Assert.IsType<MacroStmt>(s[0]);
        Assert.IsType<SetStmt>(s[1]);
        Assert.IsType<MacroExpr>(Assert.IsType<UseStmt>(s[2]).Table);
    }

    [Fact]
    public void Preprocessor_defines_and_conditionals()
    {
        var s = Parse("#DEFINE MAX_ROWS 100\n#IF MAX_ROWS > 50\nx = MAX_ROWS\n#ELSE\nx = 0\n#ENDIF");
        var a = Assert.IsType<AssignStmt>(Assert.Single(s));
        Assert.Equal(100, ((LiteralExpr)a.Value).Value.AsNumber);
    }

    [Fact]
    public void Text_block_captures_raw_lines()
    {
        var s = Parse("TEXT TO lcSql TEXTMERGE NOSHOW\nSELECT * FROM x WHERE a = '<<y>>'\nENDTEXT");
        var t = Assert.IsType<TextStmt>(Assert.Single(s));
        Assert.Equal("lcSql", t.ToVar);
        Assert.True(t.Merge);
        Assert.Single(t.Lines);
    }

    [Fact]
    public void Syntax_errors_report_line_numbers()
    {
        var ex = Assert.Throws<CompileException>(() => Parse("x = 1\nIF x\ny = (1 +\nENDIF"));
        Assert.Equal(3, ex.Line);
    }

    [Fact]
    public void Include_of_the_foxpro_system_headers_uses_the_built_in_copies_unless_a_file_exists()
    {
        static Expr Value(ProgramUnit u) => Assert.IsType<AssignStmt>(Assert.Single(u.Main)).Value;
        var builtIn = Parser.ParseProgram("#INCLUDE ..\\..\\FoxPro.H\nx = MB_YESNO + MB_ICONQUESTION");
        var sum = Assert.IsType<BinaryExpr>(Value(builtIn));
        Assert.Equal((4.0, 32.0), (((LiteralExpr)sum.Left).Value.AsNumber, ((LiteralExpr)sum.Right).Value.AsNumber));
        Assert.IsType<AssignStmt>(Assert.Single(Parser.ParseProgram("#INCLUDE foxpro_reporting.h\nx = FRX_OBJTYP_BAND").Main));
        var own = Parser.ParseProgram("#INCLUDE foxpro.h\nx = MB_YESNO", includeResolver: _ => "#DEFINE MB_YESNO 99\n");
        Assert.Equal(99.0, ((LiteralExpr)Value(own)).Value.AsNumber);
        Assert.Throws<CompileException>(() => Parser.ParseProgram("#INCLUDE missing.h\nx = 1"));
        Assert.Equal(["foxpro.h", "foxpro_reporting.h"], SystemHeaders.All);
    }
}
