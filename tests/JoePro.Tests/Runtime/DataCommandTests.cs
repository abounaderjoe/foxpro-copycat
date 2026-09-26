using JoePro.Core;

namespace JoePro.Tests.Runtime;

public class DataCommandTests : RuntimeHarness
{
    private const string Setup = """
        CREATE TABLE customer (id I, name C(20), city C(15), balance Y, joined D, notes M)
        INSERT INTO customer VALUES (1, "Smith", "Boston", 10, {^2020-01-15}, "vip")
        INSERT INTO customer VALUES (2, "Adams", "Chicago", 0, {^2021-06-01}, "")
        INSERT INTO customer VALUES (3, "Jones", "Boston", 250.5, {^2019-03-10}, "")
        INSERT INTO customer VALUES (4, "Brown", "Denver", 5, {^2022-11-30}, "")
        CREATE TABLE orders (id I, custid I, amount N(10,2), shipped L)
        INSERT INTO orders VALUES (1, 1, 100.00, .T.)
        INSERT INTO orders VALUES (2, 1, 50.25, .F.)
        INSERT INTO orders VALUES (3, 3, 75.00, .T.)
        INSERT INTO orders VALUES (4, 9, 10.00, .F.)
        SELECT customer
        """;

    [Fact]
    public void Create_use_navigate_and_functions()
    {
        var o = Run(Setup + """

            GO TOP
            ? RECCOUNT(), RECNO(), ALIAS()
            GO BOTTOM
            ? name, EOF()
            SKIP
            ? EOF(), RECNO()
            GO 2
            ? ALLTRIM(name) + "/" + ALLTRIM(city)
            ? FCOUNT(), FIELD(2)
            """);
        Assert.Equal("         4          1 CUSTOMER\nBrown                .F.\n.T.          5\nAdams/Chicago\n         6 NAME", o);
    }

    [Fact]
    public void Index_seek_and_order()
    {
        var o = Run(Setup + """

            INDEX ON UPPER(name) TAG name
            GO TOP
            ? name
            ? SEEK("JON"), RECNO(), FOUND()
            SEEK "ZZZ"
            ? FOUND(), EOF()
            INDEX ON city + STR(balance, 10, 2) TAG citybal DESCENDING
            GO TOP
            ? ALLTRIM(city), ORDER()
            SET ORDER TO name
            GO TOP
            ? name, TAGCOUNT()
            """);
        Assert.Equal("Adams               \n.T.          3 .T.\n.F. .T.\nDenver CITYBAL\nAdams                         2", o);
    }

    [Fact]
    public void Scan_replace_and_locate()
    {
        var o = Run(Setup + """

            n = 0
            SCAN FOR city = "Boston"
               n = n + balance
            ENDSCAN
            ? n
            REPLACE ALL balance WITH balance * 2 FOR city = "Boston"
            SUM balance TO total
            ? total
            LOCATE FOR name = "Brown"
            ? FOUND(), RECNO()
            LOCATE FOR city = "Boston"
            CONTINUE
            ? RECNO()
            CONTINUE
            ? FOUND(), EOF()
            COUNT FOR balance > 5 TO cnt
            ? cnt
            """);
        Assert.Equal("260.5000\n526.0000\n.T.          4\n         3\n.F. .T.\n         2", o);
    }

    [Fact]
    public void Delete_recall_pack_and_set_deleted()
    {
        var o = Run(Setup + """

            DELETE FOR city = "Boston"
            ? DELETED(), RECCOUNT()
            SET DELETED ON
            COUNT TO cnt
            ? cnt
            SET DELETED OFF
            GO 1
            RECALL
            PACK
            ? RECCOUNT()
            """);
        Assert.Equal(".F.          4\n         2\n         3", o);
    }

    [Fact]
    public void Scatter_gather_and_append()
    {
        var o = Run(Setup + """

            GO 1
            SCATTER MEMVAR
            ? m.name
            m.name = "Smythe"
            GATHER MEMVAR
            ? name
            SCATTER NAME oRec
            APPEND BLANK
            GATHER NAME oRec
            ? RECCOUNT(), ALLTRIM(name), id
            """);
        Assert.Equal("Smith               \nSmythe              \n         5 Smythe          1", o);
    }

    [Fact]
    public void Sql_select_with_join_group_order_into_cursor()
    {
        var o = Run(Setup + """

            SELECT c.name, SUM(o.amount) AS total, COUNT(*) AS n ;
               FROM customer c INNER JOIN orders o ON o.custid = c.id ;
               GROUP BY c.name ORDER BY total DESC INTO CURSOR q
            ? _TALLY, ALIAS(), RECCOUNT()
            GO TOP
            ? ALLTRIM(name), total, n
            SELECT c.name, o.amount FROM customer c LEFT JOIN orders o ON o.custid = c.id WHERE c.city = "Denver" INTO ARRAY aRes
            ? ALEN(aRes, 1), ISNULL(aRes(1, 2))
            SELECT COUNT(*) FROM customer WHERE balance > 1000 INTO ARRAY aCnt
            ? aCnt(1)
            SELECT DISTINCT city FROM customer ORDER BY city INTO CURSOR cities
            ? RECCOUNT(), city
            SELECT name FROM customer WHERE id IN (SELECT custid FROM orders WHERE shipped) ORDER BY 1 INTO CURSOR shippedcust
            ? RECCOUNT()
            SELECT TOP 2 name, balance FROM customer ORDER BY balance DESC INTO CURSOR richest
            ? RECCOUNT(), ALLTRIM(name)
            """);
        Assert.Equal("         2 Q          2\nSmith     150.25          2\n         1 .T.\n         0\n         3 Boston         \n         2\n         2 Jones", o);
    }

    [Fact]
    public void Sql_union_and_expressions()
    {
        var o = Run(Setup + """

            SELECT name FROM customer WHERE city = "Boston" UNION SELECT name FROM customer WHERE city = "Denver" INTO CURSOR u
            ? RECCOUNT()
            SELECT UPPER(LEFT(name, 3)) AS code, balance * 2 AS dbl FROM customer WHERE id = 3 INTO CURSOR x
            ? code, dbl
            """);
        Assert.Equal("         3\nJON 501.0000", o);
    }

    [Fact]
    public void Sql_update_and_delete()
    {
        var o = Run(Setup + """

            UPDATE customer SET balance = balance + 1 WHERE city = "Boston"
            ? _TALLY
            DELETE FROM customer WHERE balance = 0
            ? _TALLY
            SELECT customer
            COUNT FOR DELETED() TO nDel
            ? nDel
            """);
        Assert.Equal("         2\n         1\n         1", o);
    }

    [Fact]
    public void Transactions_and_buffering()
    {
        var o = Run(Setup + """

            BEGIN TRANSACTION
            REPLACE ALL balance WITH 0
            ROLLBACK
            SUM balance TO t
            ? t
            =CURSORSETPROP("Buffering", 5, "customer")
            GO 1
            REPLACE name WITH "Buffered"
            ? GETFLDSTATE("name"), OLDVAL("name")
            =TABLEREVERT(.T.)
            ? name
            REPLACE name WITH "Kept"
            ? TABLEUPDATE(.T.)
            ? name
            """);
        Assert.Equal("265.5000\n         2 Smith               \nSmith               \n.T.\nKept                ", o);
    }

    [Fact]
    public void Relations_follow_parent()
    {
        var o = Run(Setup + """

            SELECT orders
            INDEX ON custid TAG custid
            SELECT customer
            SET RELATION TO id INTO orders
            GO 3
            ? orders.amount
            GO 2
            ? EOF("orders")
            """);
        Assert.Equal("     75.00\n.T.", o);
    }

    [Fact]
    public void Database_with_stored_procedures_and_rules()
    {
        var o = Run("""
            CREATE DATABASE shop
            CREATE TABLE item (code C(5) PRIMARY KEY, price N(8,2) CHECK price >= 0 ERROR "Price must not be negative", qty I DEFAULT 1)
            INSERT INTO item (code, price) VALUES ("A1", 9.99)
            ? qty, !EMPTY(DBC())
            TRY
               REPLACE price WITH -1
            CATCH TO e
               ? e.Message
            ENDTRY
            ? price
            TRY
               INSERT INTO item (code, price) VALUES ("A1", 1)
            CATCH TO e
               ? e.ErrorNo
            ENDTRY
            """);
        Assert.Equal("         1 .T.\nPrice must not be negative\n      9.99\n      1884", o);
    }

    [Fact]
    public void Copy_to_legacy_dbf_and_back()
    {
        var o = Run(Setup + """

            COPY TO legacy.dbf FOR city = "Boston"
            USE legacy.dbf IN 0
            ? RECCOUNT("legacy"), legacy.name
            SELECT 0
            CREATE TABLE fresh (id I, name C(20), city C(15), balance Y, joined D, notes M)
            APPEND FROM legacy.dbf
            GO TOP
            ? RECCOUNT(), notes
            """);
        Assert.Equal("         2 Smith               \n         2 vip", o);
    }

    [Fact]
    public void List_structure_and_records()
    {
        var o = Run(Setup + "\nLIST name, city FOR city = 'Boston'\nDISPLAY STRUCTURE");
        Assert.Contains("Record# NAME", o);
        Assert.Contains("      1 Smith", o);
        Assert.Contains("      3 Jones", o);
        Assert.DoesNotContain("Adams", o);
        Assert.Contains("BALANCE         Currency", o);
    }

    [Fact]
    public void Alter_table_add_and_drop_columns()
    {
        var o = Run(Setup + """

            ALTER TABLE customer ADD COLUMN email C(40)
            REPLACE ALL email WITH LOWER(ALLTRIM(name)) + "@x.com"
            ALTER TABLE customer DROP COLUMN notes
            GO 1
            ? FCOUNT(), ALLTRIM(email), RECCOUNT()
            """);
        Assert.Equal("         6 smith@x.com          4", o);
    }
}
