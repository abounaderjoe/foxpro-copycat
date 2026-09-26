namespace JoePro.Tests.Runtime;

/// <summary>Constructs found in real VFP applications (FoxUnit, Thor, GoFish, ParallelFox) that must parse and run.</summary>
public class CompatibilityTests : RuntimeHarness
{
    [Fact]
    public void Leading_dot_calls_nested_with_and_macro_members()
    {
        var o = Run("""
            oRoot = CREATEOBJECT("Custom")
            oRoot.AddProperty("oChild", CREATEOBJECT("Holder"))
            lcProp = "cValue"
            WITH oRoot
              WITH .oChild
                .Store("x")
                IF .cValue = "x"
                  ? "if ok"
                ENDIF
                .&lcProp = .&lcProp + "y"
              ENDWITH
            ENDWITH
            ? oRoot.oChild.cValue, oRoot.oChild.&lcProp

            DEFINE CLASS Holder AS Custom
              cValue = ""
              PROCEDURE Store(tcValue)
                THIS.cValue = tcValue
            ENDDEFINE
            """);
        Assert.Equal("if ok\nxy xy", o);
    }

    [Fact]
    public void M_dot_prefixes_in_declarations_loops_and_catch()
    {
        var o = Run("""
            LOCAL m.lnTotal, laItems[2]
            m.lnTotal = 0
            laItems[1] = 5
            laItems[2] = 7
            FOR m.lnI = 1 TO 2
              m.lnTotal = m.lnTotal + m.laItems[m.lnI]
            ENDFOR
            FOR EACH m.lnX IN laItems FOXOBJECT
              m.lnTotal = m.lnTotal + m.lnX
            ENDFOR
            TRY
              ERROR 1098, "boom"
            CATCH TO m.loEx WHEN m.loEx.ErrorNo = 1
              ? "wrong catch"
            CATCH TO m.loEx
              ? "second catch", m.loEx.ErrorNo
            ENDTRY
            ? m.lnTotal
            """);
        Assert.Equal("second catch       1098\n        24", o);
    }

    [Fact]
    public void Declarations_with_as_types_and_object_array_properties()
    {
        var o = Run("""
            LOCAL loApp AS VisualFoxPro.Application, loMy AS 'My' OF 'My.vcx', lcX,
            oBox = CREATEOBJECT("Custom")
            DIMENSION oBox.aRows[3, 2]
            oBox.aRows[2, 1] = "b"
            ? ALEN(oBox.aRows, 1), ALEN(oBox.aRows, 2), oBox.aRows[2, 1]
            lcName = "gcDynamic"
            PUBLIC (lcName)
            gcDynamic = "made"
            ? gcDynamic
            RELEASE gcDynamic
            """);
        Assert.Equal("         3          2 b\nmade", o);
    }

    [Fact]
    public void Literals_cast_and_comment_continuation()
    {
        var o = Run("""
            ? .5 + 1, LEN(0h0D0A), TYPE("0h41")
            ? CAST(3.7 AS I), CAST(12 AS C(4)) + "|", CAST("2.5" AS N(6,2)), EMPTY({// :: AM})
            x = 1 && a comment that continues ;
            x = 2
            ? x
            * a star comment that continues ;
            x = 3
            ? x, ["] $ 'a"b'
            """);
        Assert.Equal("       1.5          2 Q\n         4 12  |       2.50 .T.\n         1\n         1 .T.", o);
    }

    [Fact]
    public void Sql_cast_columns_flexible_clause_order_and_from_expressions()
    {
        var o = Run("""
            CREATE CURSOR src (id I, name C(10))
            INSERT INTO src VALUES (1, "Alpha")
            INSERT INTO src VALUES (2, "Beta")
            INSERT INTO src VALUES (2, "Beta2")
            SELECT id, COUNT(*) AS n, CAST(MAX(name) AS M) AS last FROM src INTO CURSOR q GROUP BY id
            ? _TALLY, TYPE("q.last")
            lcTable = "src"
            SELECT * FROM lcTable + "" INTO CURSOR q2
            ? _TALLY
            """);
        Assert.Equal("         2 M\n         3", o);
    }

    [Fact]
    public void Textmerge_lines_replace_without_commas_and_blank()
    {
        var o = Run("""
            SET TEXTMERGE ON
            lcWho = "world"
            \Hello <<lcWho>>
            \\!
            SET TEXTMERGE OFF
            CREATE CURSOR t (a C(3), b N(3))
            APPEND BLANK
            REPLACE a WITH "x" b WITH 5
            ? a, b
            BLANK FIELDS b
            ? a, b
            """);
        Assert.Equal("\nHello world!\nx            5\nx            0", o);
    }

    [Fact]
    public void Alter_table_with_several_clauses_and_name_expressions()
    {
        var o = Run("""
            CREATE TABLE people (id I)
            lcCol = "nick"
            ALTER TABLE people ADD COLUMN name C(10) ADD COLUMN (lcCol) C(5)
            ALTER TABLE people RENAME COLUMN (lcCol) TO (lcCol + "2")
            ? FCOUNT(), FIELD(3)
            COPY STRUCTURE EXTENDED TO pstru
            USE pstru
            ? RECCOUNT(), TRIM(field_name), field_type
            """);
        Assert.Equal("         3 NICK2\n         3 ID I", o);
    }
}
