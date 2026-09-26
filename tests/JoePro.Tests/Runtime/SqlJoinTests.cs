namespace JoePro.Tests.Runtime;

public class SqlJoinTests : RuntimeHarness
{
    private const string Setup = """
        CREATE CURSOR cust (id I, code C(4), name C(10))
        INSERT INTO cust VALUES (1, "AB", "Alpha")
        INSERT INTO cust VALUES (2, "ABC", "Beta")
        INSERT INTO cust VALUES (3, "XYZ", "Gamma")
        CREATE CURSOR ord (id I, custid I, code C(4), amt N(8,2))
        INSERT INTO ord VALUES (10, 1, "AB", 5)
        INSERT INTO ord VALUES (11, 1, "AB", 7)
        INSERT INTO ord VALUES (12, 2, "ABC", 9)
        INSERT INTO ord VALUES (13, 9, "QQ", 1)
        CREATE CURSOR item (ordid I, qty I)
        INSERT INTO item VALUES (10, 2)
        INSERT INTO item VALUES (12, 3)
        """;

    [Fact]
    public void Comma_join_with_where_equality()
    {
        var o = Run(Setup + "\nSELECT c.name, o.amt FROM cust c, ord o WHERE o.custid = c.id AND o.amt > 6 ORDER BY 2 INTO CURSOR q\n? _TALLY\nGO TOP\n? name, amt");
        Assert.Equal("         2\nAlpha            7.00", o);
    }

    [Fact]
    public void Three_table_inner_joins()
    {
        var o = Run(Setup + "\nSELECT c.name, i.qty FROM cust c JOIN ord o ON o.custid = c.id JOIN item i ON i.ordid = o.id ORDER BY qty INTO ARRAY a\n? ALEN(a, 1), a(1,1), a(2,2)");
        Assert.Equal("         2 Alpha               3", o);
    }

    [Fact]
    public void Outer_joins_keep_unmatched_rows()
    {
        var o = Run(Setup + """

            SELECT c.name, o.id FROM cust c LEFT JOIN ord o ON o.custid = c.id INTO CURSOR l
            ? _TALLY
            SELECT c.name, o.id FROM cust c RIGHT JOIN ord o ON o.custid = c.id INTO CURSOR r
            ? _TALLY
            SELECT c.name, o.id FROM cust c FULL JOIN ord o ON o.custid = c.id INTO CURSOR f
            ? _TALLY
            """);
        Assert.Equal("         4\n         4\n         5", o);
    }

    [Fact]
    public void Character_keys_follow_set_ansi()
    {
        // ANSI OFF: "ABC" = "AB" is true (compare to the length of the right side), so the join must not be hashed.
        var o = Run(Setup + "\nSET ANSI OFF\nSELECT o.id FROM ord o JOIN cust c ON TRIM(o.code) = TRIM(c.code) INTO CURSOR x\n? _TALLY");
        var off = int.Parse(o.Trim());
        Run("SET ANSI ON");
        o = Run("SELECT o.id FROM ord o JOIN cust c ON TRIM(o.code) = TRIM(c.code) INTO CURSOR y\n? _TALLY");
        Assert.Equal(3, int.Parse(o.Trim()));
        Assert.Equal(4, off); // order 12 "ABC" also matches customer "AB"
    }

    [Fact]
    public void Large_equi_join_is_fast()
    {
        Run("""
            CREATE CURSOR big1 (id I, v I)
            CREATE CURSOR big2 (id I, w I)
            FOR i = 1 TO 3000
               INSERT INTO big1 VALUES (i, i * 2)
               INSERT INTO big2 VALUES (i, i * 3)
            ENDFOR
            """);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var o = Run("SELECT COUNT(*) FROM big1 a JOIN big2 b ON b.id = a.id WHERE a.v > 10 INTO ARRAY n\n? n(1)");
        sw.Stop();
        Assert.Equal("      2995", o);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"join took {sw.ElapsedMilliseconds} ms");
    }
}
