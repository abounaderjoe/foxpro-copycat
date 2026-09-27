namespace JoePro.Tests.Runtime;

/// <summary>FOR conditions pushed down to SQL must select exactly the records record-by-record evaluation selects.</summary>
public class PrefilterTests : RuntimeHarness
{
    private const string Data = """
        CREATE TABLE pf FREE (id I, name C(10), vname V(10), amt N(8,2), money Y, flag L, born D, stamp T, nul N(5) NULL)
        FOR i = 1 TO 60
            INSERT INTO pf VALUES (i, IIF(i % 3 = 0, "abc" + TRANSFORM(i), IIF(i % 3 = 1, "ab", "ABC")), "x" + TRANSFORM(i % 4) + " ", ;
                i * 1.25, i * 2, i % 2 = 0, {^2024-01-01} + i, DATETIME(2024, 1, 1, 0, 0, 0) + i * 3600, IIF(i % 5 = 0, NULL, i))
        ENDFOR
        INDEX ON name TAG name
        INDEX ON id TAG id DESCENDING
        GO 7
        DELETE
        GO 8
        DELETE
        """;

    private static readonly string[] Conditions =
    [
        "id = 10", "id > 50", "id >= 50 AND id < 55", "id < 3 OR id > 58", "50 < id", "amt = 12.5", "amt > 70 AND flag",
        "money >= 100", "money = 20", "flag", "NOT flag", "flag = .F.", "flag <> .T.", "born > {^2024-02-15}", "born = {^2024-01-05}",
        "stamp >= DATETIME(2024, 1, 2, 0, 0, 0)", "name = \"abc\"", "name = \"ab\"", "name = \"ab   \"", "name = \"\"", "name = \"ABC\"",
        "vname = \"x1\"", "nul > 10", "nul = 15", "id = lnId", "id = m.lnId + 1", "name = lcName AND id > 20", "id > 5 AND UPPER(name) = \"ABC\"",
        "id = 3 OR name = \"ab\"", "(id > 10 OR amt < 5) AND NOT flag", "pf.id = 12", "id > -1",
    ];

    [Theory]
    [InlineData("SET EXACT OFF", "SET DELETED OFF", "SET ORDER TO")]
    [InlineData("SET EXACT ON", "SET DELETED ON", "SET ORDER TO")]
    [InlineData("SET EXACT OFF", "SET DELETED ON", "SET ORDER TO name")]
    [InlineData("SET EXACT OFF", "SET DELETED OFF", "SET ORDER TO id")]
    [InlineData("SET EXACT OFF", "SET DELETED ON", "SET FILTER TO id % 2 = 1")]
    public void Pushed_down_conditions_match_record_by_record_evaluation(string exact, string deleted, string order)
    {
        Run(Data);
        Run($"{exact}\n{deleted}\n{order}\nlnId = 20\nlcName = \"abc\"");
        foreach (var cond in Conditions)
        {
            var o = Run($$"""
                lcCond = [{{cond}}]
                COUNT FOR {{cond}} TO nFast
                nSlow = 0
                lcSlow = ""
                SCAN
                    IF EVALUATE(lcCond) = .T.
                        nSlow = nSlow + 1
                        lcSlow = lcSlow + TRANSFORM(id) + ","
                    ENDIF
                ENDSCAN
                lcFast = ""
                SCAN FOR {{cond}}
                    lcFast = lcFast + TRANSFORM(id) + ","
                ENDSCAN
                LOCATE FOR {{cond}}
                lnFirst = IIF(FOUND(), id, -1)
                CONTINUE
                lnSecond = IIF(FOUND(), id, -1)
                ? nFast = nSlow, lcFast == lcSlow, lnFirst = IIF(nSlow > 0, VAL(GETWORDNUM(lcSlow, 1, ",")), -1), ;
                  lnSecond = IIF(nSlow > 1, VAL(GETWORDNUM(lcSlow, 2, ",")), -1), nSlow
                """);
            var parts = o.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(parts.Take(4).All(p => p == ".T."), $"{cond} ({exact}, {deleted}, {order}): {o.Trim()}");
        }
    }

    [Fact]
    public void Replace_and_delete_for_touch_only_matching_records_including_buffered_ones()
    {
        Run(Data);
        var o = Run("""
            REPLACE amt WITH -1 FOR id > 55
            DELETE FOR name = "ab" AND id < 10
            COUNT FOR amt = -1 TO a
            COUNT FOR DELETED() TO d
            SET MULTILOCKS ON
            = CURSORSETPROP("Buffering", 5)
            GO 2
            REPLACE id WITH 999
            COUNT FOR id = 999 TO b
            ? a, d, b
            """);
        Assert.Equal("5 7 1", string.Join(" ", o.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }
}
