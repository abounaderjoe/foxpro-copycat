using JoePro.Core;

namespace JoePro.Tests.Runtime;

public class LanguageTests : RuntimeHarness
{
    [Theory]
    [InlineData("1 + 2", "         3")]
    [InlineData("10 / 4", "      2.50")]
    [InlineData("2 ^ 10", "   1024.00")]
    [InlineData("7 % 3", "         1")]
    [InlineData("MOD(-7, 3)", "         2")]
    [InlineData("\"abc\" + \"def\"", "abcdef")]
    [InlineData("\"ab  \" - \"cd\"", "abcd  ")]
    [InlineData("\"b\" $ \"abc\"", ".T.")]
    [InlineData(".NULL. + 1", ".NULL.")]
    [InlineData(".F. AND .NULL.", ".F.")]
    [InlineData(".T. OR .NULL.", ".T.")]
    [InlineData("{^2024-01-31} + 1", "02/01/24")]
    [InlineData("{^2024-03-01} - {^2024-02-01}", "        29")]
    [InlineData("IIF(1 > 2, 'yes', 'no')", "no")]
    [InlineData("$12.5 * 2", "25.0000")]
    public void Expressions(string expr, string expected) => Assert.Equal(expected, Eval(expr));

    [Fact]
    public void Set_exact_changes_string_equality()
    {
        Assert.Equal(".T.", Eval("'abcdef' = 'abc'"));
        Assert.Equal(".F.", Eval("'abcdef' == 'abc'"));
        Run("SET EXACT ON");
        Assert.Equal(".F.", Eval("'abcdef' = 'abc'"));
        Assert.Equal(".T.", Eval("'abc' = 'abc   '"));
    }

    [Fact]
    public void Print_and_variables()
    {
        var o = Run("x = 5\nSTORE 'hi' TO a, b\n? x, a + b\n?? '!'");
        Assert.Equal("         5 hihi!", o);
    }

    [Fact]
    public void Control_flow_loops_and_case()
    {
        var o = Run("""
            n = 0
            FOR i = 1 TO 10
               IF i % 2 = 0
                  LOOP
               ENDIF
               IF i > 7
                  EXIT
               ENDIF
               n = n + i
            ENDFOR
            ? n
            j = 0
            DO WHILE j < 3
               j = j + 1
            ENDDO
            DO CASE
            CASE j = 1
               ? "one"
            CASE j = 3
               ? "three"
            OTHERWISE
               ? "other"
            ENDCASE
            """);
        Assert.Equal("        16\nthree", o);
    }

    [Fact]
    public void Procedures_parameters_and_scoping()
    {
        var o = Run("""
            PRIVATE pv
            pv = "private"
            LOCAL lv
            lv = 1
            ? Add(2, 3)
            DO Show WITH "x"
            y = 10
            DO Bump WITH y
            ? y
            z = 10
            =Bump2(z)
            ? z
            =Bump2(@z)
            ? z
            FUNCTION Add(a, b)
               RETURN a + b
            ENDFUNC
            PROCEDURE Show
               LPARAMETERS c
               ? c + pv
            ENDPROC
            PROCEDURE Bump
               PARAMETERS v
               v = v + 1
            ENDPROC
            FUNCTION Bump2(v)
               v = v + 1
            ENDFUNC
            """);
        Assert.Equal("         5\nxprivate\n        11\n        10\n        11", o);
    }

    [Fact]
    public void Local_variables_are_not_visible_to_callees()
    {
        var ex = Assert.Throws<VfpException>(() => Run("""
            LOCAL secret
            secret = 1
            =Peek()
            FUNCTION Peek
               RETURN secret
            ENDFUNC
            """));
        Assert.Equal(ErrorCodes.VariableNotFound, ex.Number);
    }

    [Fact]
    public void Arrays()
    {
        var o = Run("""
            DIMENSION a(3)
            a(1) = 'c'
            a(2) = 'a'
            a[3] = 'b'
            =ASORT(a)
            ? a(1) + a(2) + a(3), ALEN(a)
            DIMENSION m(2, 3)
            m(2, 3) = 99
            ? m(6), ALEN(m, 1), ALEN(m, 2), ASCAN(m, 99)
            DIMENSION a(5)
            ? a(1), a(5)
            n = ALINES(lines, "one" + CHR(13) + CHR(10) + "two")
            ? n, lines(2)
            """);
        Assert.Equal("abc          3\n        99          2          3          6\na .F.\n         2 two", o);
    }

    [Fact]
    public void Try_catch_finally_and_exception_object()
    {
        var o = Run("""
            TRY
               x = 1 / 0
            CATCH TO oErr
               ? oErr.ErrorNo, oErr.Message
            FINALLY
               ? "done"
            ENDTRY
            TRY
               THROW "custom"
            CATCH TO e
               ? e.UserValue
            ENDTRY
            """);
        Assert.Equal("      1307 Division by zero.\ndone\ncustom", o);
    }

    [Fact]
    public void On_error_handler_continues_execution()
    {
        var o = Run("""
            ON ERROR errs = errs + 1
            errs = 0
            x = undefined_var
            y = 1 + "a"
            ON ERROR
            ? errs
            """);
        Assert.Equal("         2", o);
    }

    [Fact]
    public void Macro_substitution_and_evaluate()
    {
        var o = Run("""
            lcVar = "counter"
            &lcVar = 41
            counter = counter + 1
            ? counter
            lcExpr = "counter * 2"
            ? EVALUATE(lcExpr), &lcExpr
            lcCmd = "? 'from macro'"
            &lcCmd
            """);
        Assert.Equal("        42\n        84         84\nfrom macro", o);
    }

    [Fact]
    public void Text_merge()
    {
        var o = Run("""
            name = "World"
            TEXT TO lcOut TEXTMERGE NOSHOW
            Hello <<name>>! <<1+1>>
            ENDTEXT
            ? lcOut
            """);
        Assert.Equal("Hello World! 2", o);
    }

    [Theory]
    [InlineData("ALLTRIM('  ab  ')", "ab")]
    [InlineData("PADL('7', 3, '0')", "007")]
    [InlineData("PADR('ab', 4) + '|'", "ab  |")]
    [InlineData("SUBSTR('abcdef', 2, 3)", "bcd")]
    [InlineData("LEFT('abc', 2) + RIGHT('abc', 2)", "abbc")]
    [InlineData("AT('b', 'abcb', 2)", "         4")]
    [InlineData("RAT('b', 'abcb')", "         4")]
    [InlineData("STRTRAN('a-b-c', '-', '+')", "a+b+c")]
    [InlineData("STUFF('abcdef', 2, 3, 'X')", "aXef")]
    [InlineData("PROPER('hello wORLD')", "Hello World")]
    [InlineData("STR(3.14159, 6, 2)", "  3.14")]
    [InlineData("STR(123456, 3)", "***")]
    [InlineData("VAL('12.5abc')", "     12.50")]
    [InlineData("TRANSFORM(1234.5, '9,999.99')", "1,234.50")]
    [InlineData("TRANSFORM('abc', '@!')", "ABC")]
    [InlineData("CHRTRAN('hello', 'lo', 'x')", "hexx")]
    [InlineData("GETWORDNUM('the quick fox', 2)", "quick")]
    [InlineData("STREXTRACT('<a>x</a>', '<a>', '</a>')", "x")]
    [InlineData("OCCURS('a', 'banana')", "         3")]
    [InlineData("INLIST(3, 1, 2, 3)", ".T.")]
    [InlineData("BETWEEN(5, 1, 10)", ".T.")]
    [InlineData("LIKE('a*c', 'abbc')", ".T.")]
    [InlineData("EMPTY('   ') AND EMPTY(0) AND EMPTY({})", ".T.")]
    [InlineData("NVL(.NULL., 'x')", "x")]
    [InlineData("VARTYPE(123) + VARTYPE('a') + VARTYPE(.NULL.) + VARTYPE(nosuchvar)", "NCXU")]
    [InlineData("TYPE('1+1') + TYPE('bogus(')", "NU")]
    [InlineData("ROUND(2.345, 2)", "      2.35")]
    [InlineData("INT(-3.7)", "        -3")]
    [InlineData("MAX(3, 9, 4)", "         9")]
    [InlineData("DTOS({^2024-07-04})", "20240704")]
    [InlineData("DOW({^2024-07-04})", "         5")]
    [InlineData("CDOW({^2024-07-04})", "Thursday")]
    [InlineData("GOMONTH({^2024-01-31}, 1)", "02/29/24")]
    [InlineData("YEAR(CTOD('12/25/2023'))", "      2023")]
    [InlineData("TTOC({^2024-07-04 13:05:09}, 1)", "20240704130509")]
    [InlineData("HOUR({^2024-07-04 13:05:09})", "        13")]
    [InlineData("JUSTSTEM('c:\\data\\cust.dbf') + JUSTEXT('a.prg')", "custprg")]
    [InlineData("FORCEEXT('report.frx', 'jpreport')", "report.jpreport")]
    [InlineData("BITAND(12, 10)", "         8")]
    [InlineData("ICASE(.F., 1, .T., 2, 3)", "         2")]
    [InlineData("SUBS('abcdef', 3)", "cdef")]
    public void Builtin_functions(string expr, string expected) => Assert.Equal(expected, Eval(expr));

    [Fact]
    public void Objects_classes_inheritance_and_dodefault()
    {
        var o = Run("""
            oDog = CREATEOBJECT("Dog", "Rex")
            ? oDog.Speak()
            ? oDog.Name, oDog.Legs
            oDog.Legs = 3
            ? oDog.Legs, oDog.BaseClass, oDog.ParentClass
            oCol = CREATEOBJECT("Collection")
            oCol.Add("apple", "a")
            oCol.Add("pear", "p")
            ? oCol.Count, oCol.Item("p"), oCol.Item(1)
            oEmpty = CREATEOBJECT("Empty")
            =ADDPROPERTY(oEmpty, "x", 5)
            ? oEmpty.x, PEMSTATUS(oDog, "Speak", 5)
            DEFINE CLASS Animal AS Custom
               Legs = 4
               PROCEDURE Speak
                  RETURN "..."
               ENDPROC
            ENDDEFINE
            DEFINE CLASS Dog AS Animal
               PROCEDURE Init(cName)
                  This.Name = cName
               ENDPROC
               PROCEDURE Speak
                  RETURN "Woof " + DODEFAULT()
               ENDPROC
            ENDDEFINE
            """);
        Assert.Equal("Woof ...\nRex          4\n         3 Custom Animal\n         2 pear apple\n         5 .T.", o);
    }

    [Fact]
    public void Member_objects_and_access_assign()
    {
        var o = Run("""
            oForm = CREATEOBJECT("MyForm")
            ? oForm.txtName.Value, oForm.txtName.Parent.Name
            oForm.Total = -5
            ? oForm.Total
            WITH oForm
               .Caption = "Changed"
               ? .Caption
            ENDWITH
            DEFINE CLASS MyForm AS Form
               Caption = "Hello"
               Total = 0
               ADD OBJECT txtName AS TextBox WITH Value = "abc"
               PROCEDURE Total_Assign(vNew)
                  This.Total = MAX(vNew, 0)
               ENDPROC
            ENDDEFINE
            """);
        Assert.Equal("abc MyForm\n         0\nChanged", o);
    }

    [Fact]
    public void Init_returning_false_yields_null()
    {
        Assert.Equal(".T.", Eval("ISNULL(CREATEOBJECT('Custom')) = .F."));
        var o = Run("""
            o = CREATEOBJECT("Refuses")
            ? ISNULL(o)
            DEFINE CLASS Refuses AS Custom
               PROCEDURE Init
                  RETURN .F.
               ENDPROC
            ENDDEFINE
            """);
        Assert.Equal(".T.", o);
    }

    [Fact]
    public void Four_letter_abbreviations_run()
    {
        var o = Run("STOR 3 TO nVal\nIF nVal = 3\n? 'ok'\nENDI");
        Assert.Equal("ok", o);
    }

    [Fact]
    public void Programs_run_from_files()
    {
        File.WriteAllText(Path.Combine(Dir, "hello.prg"), "LPARAMETERS cWho\nRETURN 'Hello, ' + cWho\nPROCEDURE Other\nRETURN 1");
        var o = Run("? hello('Joe')\nDO hello WITH 'x'");
        Assert.Equal("Hello, Joe", o);
    }

    [Fact]
    public void Execscript_runs_code_strings()
    {
        Assert.Equal("         6", Eval("EXECSCRIPT('LPARAMETERS a, b' + CHR(13) + 'RETURN a * b', 2, 3)"));
    }

    [Fact]
    public void Errors_report_vfp_numbers()
    {
        var ex = Assert.Throws<VfpException>(() => Run("x = 'a' + 1"));
        Assert.Equal(ErrorCodes.OperatorOperandMismatch, ex.Number);
        ex = Assert.Throws<VfpException>(() => Run("? nosuch"));
        Assert.Equal(ErrorCodes.VariableNotFound, ex.Number);
        ex = Assert.Throws<VfpException>(() => Run("ERROR 'custom failure'"));
        Assert.Equal(ErrorCodes.UserDefined, ex.Number);
    }
}
