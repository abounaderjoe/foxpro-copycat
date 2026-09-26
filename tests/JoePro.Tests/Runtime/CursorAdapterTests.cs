namespace JoePro.Tests.Runtime;

public class CursorAdapterTests : RuntimeHarness
{
    [Fact]
    public void Native_adapter_fills_updates_and_refreshes()
    {
        var o = Run("""
            CREATE TABLE cust (id I, name C(10))
            INSERT INTO cust VALUES (1, "Alpha")
            INSERT INTO cust VALUES (2, "Beta")
            INSERT INTO cust VALUES (3, "Gamma")
            oCA = CREATEOBJECT("CursorAdapter")
            oCA.Alias = "crsCust"
            oCA.DataSourceType = "NATIVE"
            oCA.SelectCmd = "SELECT id, name FROM cust WHERE id >= ?nMin ORDER BY id"
            nMin = 2
            ? oCA.CursorFill(), RECCOUNT("crsCust"), oCA.CursorStatus, CURSORGETPROP("Buffering", "crsCust")
            oCA.Tables = "cust"
            oCA.KeyFieldList = "id"
            oCA.UpdatableFieldList = "name"
            oCA.UpdateNameList = "id cust.id, name cust.name"
            oCA.SendUpdates = .T.
            SELECT crsCust
            REPLACE name WITH "Changed"
            ? TABLEUPDATE(.T.)
            SELECT cust
            LOCATE FOR id = 2
            ? TRIM(name)
            nMin = 3
            ? oCA.CursorRefresh(), RECCOUNT("crsCust"), crsCust.id
            """);
        Assert.Equal(".T.          2          1          5\n.T.\nChanged\n.T.          1          3", o);
    }

    [Fact]
    public void Odbc_adapter_uses_a_pass_through_handle_schema_and_events()
    {
        var o = Run("""
            PUBLIC gcLog
            gcLog = ""
            h = SQLSTRINGCONNECT("Provider=sqlite;Data Source=ca.db")
            =SQLEXEC(h, "CREATE TABLE item (id INTEGER PRIMARY KEY, descr TEXT)")
            =SQLEXEC(h, "INSERT INTO item VALUES (1, 'bolt'), (2, 'nut')")
            oCA = CREATEOBJECT("caItem")
            oCA.DataSource = h
            ? oCA.CursorFill(), TYPE("crsItem.descr"), LEN(crsItem.descr), gcLog
            SELECT crsItem
            REPLACE descr WITH "washer"
            ? TABLEUPDATE(.T.)
            =SQLEXEC(h, "SELECT descr FROM item WHERE id = 1", "chk")
            ? chk.descr

            DEFINE CLASS caItem AS CursorAdapter
              Alias = "crsItem"
              DataSourceType = "ODBC"
              SelectCmd = "SELECT id, descr FROM item ORDER BY id"
              CursorSchema = "id I, descr C(12)"
              UseCursorSchema = .T.
              Tables = "item"
              KeyFieldList = "id"
              UpdatableFieldList = "descr"
              UpdateNameList = "id item.id, descr item.descr"
              SendUpdates = .T.
              PROCEDURE BeforeCursorFill(lUseSchema, lNoData, cSelect)
                gcLog = gcLog + "before;"
              PROCEDURE AfterCursorFill(lUseSchema, lNoData, cSelect, lResult)
                gcLog = gcLog + "after:" + TRANSFORM(lResult)
            ENDDEFINE
            """);
        Assert.Equal(".T. C         12 before;after:.T.\n.T.\nwasher", o);
    }

    [Fact]
    public void Failed_fill_returns_false_and_sets_aerror()
    {
        var o = Run("""
            oCA = CREATEOBJECT("CursorAdapter")
            oCA.Alias = "bad"
            oCA.SelectCmd = "SELECT * FROM nosuchtable"
            ? oCA.CursorFill(), AERROR(la) > 0, USED("bad")
            """);
        Assert.Equal(".F. .T. .F.", o);
    }
}
