namespace JoePro.Tests.Runtime;

public class PassThroughTests : RuntimeHarness
{
    private const string Setup = """
        h = SQLSTRINGCONNECT("Provider=sqlite;Data Source=remote.db")
        =SQLEXEC(h, "CREATE TABLE cust (id INTEGER PRIMARY KEY, name VARCHAR(20), bal NUMERIC(10,2), since DATE)")
        =SQLEXEC(h, "INSERT INTO cust VALUES (1, 'Alpha', 10.5, '2024-01-02')")
        """;

    [Fact]
    public void Sqlexec_binds_parameters_and_creates_a_cursor()
    {
        var o = Run(Setup + """

            lcName = "Beta"
            ? SQLEXEC(h, "INSERT INTO cust (id, name, bal) VALUES (2, ?lcName, ?(3*4))")
            ? SQLEXEC(h, "SELECT * FROM cust ORDER BY id", "crs")
            ? ALIAS(), RECCOUNT(), TYPE("since"), TYPE("bal"), since
            SKIP
            ? TRIM(name), bal, ISNULL(since)
            ? SQLDISCONNECT(h)
            """);
        Assert.Equal("         1\n         1\nCRS          2 D N 01/02/24\nBeta      12.00 .T.\n         1", o);
    }

    [Fact]
    public void Errors_return_minus_one_and_fill_aerror()
    {
        var o = Run(Setup + """

            ? SQLEXEC(h, "SELECT bogus FROM nowhere")
            ? AERROR(la), la(1), "no such table" $ la(3)
            """);
        Assert.Equal("        -1\n         1       1526 .T.", o);
    }

    [Fact]
    public void Batches_create_numbered_cursors_and_count_info()
    {
        var o = Run(Setup + """

            ? SQLEXEC(h, "SELECT id FROM cust; SELECT name, bal FROM cust", "r", laInfo)
            ? ALEN(laInfo, 1), laInfo(1, 1), laInfo(2, 1), laInfo(2, 2), USED("r"), USED("r1")
            """);
        Assert.Equal("         2\n         2 R R1          1 .T. .T.", o);
    }

    [Fact]
    public void Manual_transactions_roll_back()
    {
        var o = Run(Setup + """

            =SQLSETPROP(h, "Transactions", 2)
            =SQLEXEC(h, "DELETE FROM cust")
            ? SQLROLLBACK(h)
            =SQLEXEC(h, "SELECT COUNT(*) AS n FROM cust", "c")
            ? c.n, SQLGETPROP(h, "Transactions")
            """);
        Assert.Equal("         1\n         1          2", o);
    }

    [Fact]
    public void Tables_and_columns_describe_the_server()
    {
        var o = Run(Setup + """

            ? SQLTABLES(h, "TABLE", "t")
            ? RECCOUNT("t"), TRIM(t.table_name)
            ? SQLCOLUMNS(h, "cust", "FOXPRO", "cols")
            ?
            SCAN
              ?? TRIM(field_name) + ":" + field_type + " "
            ENDSCAN
            """);
        Assert.Equal("         1\n         1 cust\n         1\nid:I name:C bal:N since:D ", o);
    }

    [Fact]
    public void Named_connections_are_stored_in_the_database()
    {
        var o = Run("""
            CREATE DATABASE app
            CREATE CONNECTION lite CONNSTRING "Provider=sqlite;Data Source=remote.db"
            ? DBGETPROP("lite", "CONNECTION", "ConnectString")
            h = SQLCONNECT("lite")
            ? h > 0, ASQLHANDLES(la)
            ? ADBOBJECTS(lo, "CONNECTION"), lo(1)
            """);
        Assert.Equal("Provider=sqlite;Data Source=remote.db\n.T.          1\n         1 LITE", o);
    }
}
