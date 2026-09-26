namespace JoePro.Tests.Runtime;

public class ViewTests : RuntimeHarness
{
    private const string Setup = """
        CREATE DATABASE shop
        CREATE TABLE cust (id I PRIMARY KEY, name C(10), city C(10))
        INSERT INTO cust VALUES (1, "Alpha", "Rome")
        INSERT INTO cust VALUES (2, "Beta", "Paris")
        INSERT INTO cust VALUES (3, "Gamma", "Rome")
        CREATE SQL VIEW vcity AS SELECT id, name FROM cust WHERE city = ?cCity
        =DBSETPROP("vcity.id", "FIELD", "KeyField", .T.)
        =DBSETPROP("vcity.name", "FIELD", "Updatable", .T.)
        =DBSETPROP("vcity", "VIEW", "SendUpdates", .T.)
        cCity = "Rome"
        """;

    [Fact]
    public void Parameterized_view_opens_and_requeries()
    {
        var o = Run(Setup + """

            USE vcity
            ? ALIAS(), RECCOUNT(), CURSORGETPROP("SourceType"), CURSORGETPROP("Buffering")
            ? DBGETPROP("vcity.name", "FIELD", "UpdateName"), DBGETPROP("vcity", "VIEW", "Tables")
            cCity = "Paris"
            ? REQUERY(), RECCOUNT(), TRIM(name)
            """);
        Assert.Equal("VCITY          2          1          3\ncust.name cust\n         1          1 Beta", o);
    }

    [Fact]
    public void Tableupdate_writes_changes_to_the_base_table()
    {
        var o = Run(Setup + """

            USE vcity
            REPLACE name WITH "Alfa"
            ? TABLEUPDATE(.T.)
            APPEND BLANK
            REPLACE id WITH 4, name WITH "Delta"
            ? TABLEUPDATE(.T.)
            LOCATE FOR id = 3
            DELETE
            ? TABLEUPDATE(.T.)
            SELECT cust
            SET DELETED ON
            COUNT TO n
            LOCATE FOR id = 1
            ? n, TRIM(name)
            LOCATE FOR id = 4
            ? TRIM(name), EMPTY(city)
            """);
        Assert.Equal(".T.\n.T.\n.T.\n         3 Alfa\nDelta .T.", o);
    }

    [Fact]
    public void Update_conflicts_are_detected_unless_forced()
    {
        var o = Run(Setup + """

            USE vcity
            UPDATE cust SET name = "Other" WHERE id = 1
            SELECT vcity
            REPLACE name WITH "Mine"
            ? TABLEUPDATE(.T.)
            ? TABLEUPDATE(.T., .T.)
            SELECT cust
            LOCATE FOR id = 1
            ? TRIM(name)
            """);
        Assert.Equal(".F.\n.T.\nMine", o);
    }

    [Fact]
    public void Views_without_sendupdates_only_change_the_cursor()
    {
        var o = Run(Setup + """

            =DBSETPROP("vcity", "VIEW", "SendUpdates", .F.)
            USE vcity
            REPLACE name WITH "Local"
            ? TABLEUPDATE(.T.), TRIM(vcity.name)
            SELECT cust
            LOCATE FOR id = 1
            ? TRIM(name)
            """);
        Assert.Equal(".T. Local\nAlpha", o);
    }

    [Fact]
    public void Cursor_properties_override_the_view_definition()
    {
        var o = Run(Setup + """

            =DBSETPROP("vcity", "VIEW", "SendUpdates", .F.)
            USE vcity
            ? CURSORGETPROP("KeyFieldList"), CURSORGETPROP("UpdatableFieldList"), CURSORGETPROP("SendUpdates")
            =CURSORSETPROP("SendUpdates", .T.)
            REPLACE name WITH "Cursor"
            ? TABLEUPDATE(.T.)
            SELECT cust
            LOCATE FOR id = 1
            ? TRIM(name), ADBOBJECTS(la, "VIEW"), la(1), INDBC("vcity", "VIEW")
            """);
        Assert.Equal("ID NAME .F.\n.T.\nCursor          1 VCITY .T.", o);
    }

    [Fact]
    public void Nodata_opens_an_empty_view_and_views_can_be_deleted()
    {
        var o = Run(Setup + """

            USE vcity NODATA
            ? RECCOUNT(), FCOUNT()
            USE
            DELETE VIEW vcity
            ? INDBC("vcity", "VIEW")
            """);
        Assert.Equal("         0          2\n.F.", o);
    }

    [Fact]
    public void Remote_views_update_the_server()
    {
        var o = Run("""
            CREATE DATABASE app
            CREATE CONNECTION lite CONNSTRING "Provider=sqlite;Data Source=remote.db"
            h = SQLCONNECT("lite")
            =SQLEXEC(h, "CREATE TABLE item (id INTEGER PRIMARY KEY, descr VARCHAR(30), qty INTEGER)")
            =SQLEXEC(h, "INSERT INTO item VALUES (1, 'Bolt', 10), (2, 'Nut', 20)")
            CREATE SQL VIEW vitem REMOTE CONNECTION lite AS SELECT id, descr, qty FROM item ORDER BY id
            =DBSETPROP("vitem.id", "FIELD", "KeyField", .T.)
            =DBSETPROP("vitem.qty", "FIELD", "Updatable", .T.)
            =DBSETPROP("vitem", "VIEW", "SendUpdates", .T.)
            USE vitem
            ? CURSORGETPROP("SourceType"), RECCOUNT(), TRIM(descr), DBGETPROP("vitem.qty", "FIELD", "UpdateName")
            REPLACE qty WITH 11
            ? TABLEUPDATE(.T.)
            =SQLEXEC(h, "SELECT qty FROM item WHERE id = 1", "chk")
            ? chk.qty
            """);
        Assert.Equal("         2          2 Bolt item.qty\n.T.\n        11", o);
    }
}
