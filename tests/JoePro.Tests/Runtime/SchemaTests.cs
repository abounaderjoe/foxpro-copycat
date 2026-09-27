using JoePro.Core;
using JoePro.Data;

namespace JoePro.Tests.Runtime;

/// <summary>Schema changes: table designs, rules and triggers, keys and relations, referential integrity, schema diffs.</summary>
public class SchemaTests : RuntimeHarness
{
    private void Sales()
    {
        Run("""
            CREATE DATABASE sales
            CREATE TABLE customer (id I AUTOINC, name C(20) NOT NULL, city C(15))
            ALTER TABLE customer ADD PRIMARY KEY id TAG id
            CREATE TABLE orders (id I AUTOINC, custid I, amount N(10,2))
            ALTER TABLE orders ADD PRIMARY KEY id TAG id
            ALTER TABLE orders ADD FOREIGN KEY custid TAG custid REFERENCES customer ON DELETE CASCADE ON UPDATE CASCADE ON INSERT RESTRICT
            INSERT INTO customer (name, city) VALUES ('Acme', 'Boston')
            INSERT INTO customer (name, city) VALUES ('Globex', 'Chicago')
            INSERT INTO orders (custid, amount) VALUES (1, 100)
            INSERT INTO orders (custid, amount) VALUES (1, 250)
            INSERT INTO orders (custid, amount) VALUES (2, 75)
            """);
    }

    private Store Db => Rt.Session.CurrentDatabase!;

    [Fact]
    public void Keys_and_foreign_keys_make_persistent_relations_with_ri_codes()
    {
        Sales();
        var rel = Assert.Single(Db.Relations());
        Assert.Equal(new RelationDef("CUSTOMER", "ID", "ORDERS", "CUSTID", "CASCADE", "CASCADE", "RESTRICT"), rel);
        Assert.Equal("ID", Eval("DBGETPROP('customer', 'TABLE', 'PrimaryKey')"));
        Run("n = ADBOBJECTS(aRel, 'RELATION')");
        Assert.Equal("CCR", Eval("aRel[1,5]"));
        Assert.Equal("ORDERS", Eval("aRel[1,1]"));
        var ex = Assert.Throws<VfpException>(() => Run("ALTER TABLE customer ADD PRIMARY KEY name TAG name"));
        Assert.Contains("already has a primary key", ex.Message);

        Run("ALTER TABLE orders DROP FOREIGN KEY TAG custid SAVE");
        Assert.Empty(Db.Relations());
        Assert.NotNull(Db.OpenTable("orders").Schema.FindTag("custid"));   // SAVE keeps the tag
        Run("ALTER TABLE orders ADD FOREIGN KEY TAG custid REFERENCES customer TAG id");
        Assert.Equal("III", Assert.Single(Db.Relations()).RiCode);
        Run("ALTER TABLE orders DROP FOREIGN KEY TAG custid");
        Assert.Null(Db.OpenTable("orders").Schema.FindTag("custid"));
    }

    [Fact]
    public void Referential_integrity_cascades_and_restricts()
    {
        Sales();
        // Changing a customer's key cascades to its orders.
        Run("SELECT customer\nLOCATE FOR id = 1\nREPLACE id WITH 10");
        Assert.Equal("2", CountOrders("custid = 10"));
        // Inserting an order for a missing customer is restricted, and nothing is inserted.
        var ex = Assert.Throws<VfpException>(() => Run("INSERT INTO orders (custid, amount) VALUES (99, 1)"));
        Assert.Equal(ErrorCodes.TriggerFailed, ex.Number);
        Assert.Equal("3", CountOrders(".T."));
        // Changing an order's customer to a missing one is restricted too.
        Assert.Throws<VfpException>(() => Run("UPDATE orders SET custid = 42 WHERE custid = 2"));
        Assert.Equal("1", CountOrders("custid = 2"));
        // Deleting a customer cascades to its orders.
        Run("DELETE FROM customer WHERE id = 10");
        Assert.Equal("0", CountOrders("custid = 10"));
        Assert.Equal("1", CountOrders(".T."));

        // RESTRICT on delete: the delete fails and is undone.
        Run("ALTER TABLE orders DROP FOREIGN KEY TAG custid SAVE");
        Run("ALTER TABLE orders ADD FOREIGN KEY TAG custid REFERENCES customer ON DELETE RESTRICT ON UPDATE RESTRICT");
        ex = Assert.Throws<VfpException>(() => Run("SELECT customer\nLOCATE FOR id = 2\nDELETE"));
        Assert.Contains("restricted", ex.Message);
        Assert.Equal(".F.", Eval("DELETED('customer')"));
        Assert.Throws<VfpException>(() => Run("REPLACE customer.id WITH 20"));
        Assert.Equal("2", Eval("TRANSFORM(customer.id)"));
    }

    private string CountOrders(string where)
    {
        Run($"SELECT COUNT(*) AS n FROM orders WHERE {where} AND !DELETED() INTO CURSOR qCount");
        var n = Eval("TRANSFORM(qCount.n)");
        Run("USE IN qCount");
        return n;
    }

    [Fact]
    public void Rules_defaults_triggers_and_field_properties()
    {
        Sales();
        Run("""
            ALTER TABLE orders SET CHECK amount >= 0 ERROR "Amounts cannot be negative"
            ALTER TABLE orders ALTER COLUMN amount SET DEFAULT 5
            ALTER TABLE customer ALTER COLUMN name SET CHECK !EMPTY(name) ERROR "Name is required"
            CREATE TRIGGER ON customer FOR DELETE AS city <> 'Boston'
            =DBSETPROP('customer.name', 'FIELD', 'Caption', 'Customer name')
            =DBSETPROP('customer.name', 'FIELD', 'Format', '!')
            =DBSETPROP('customer', 'TABLE', 'Comment', 'People who buy')
            """);
        Assert.Equal("amount >= 0", Eval("DBGETPROP('orders', 'TABLE', 'RuleExpression')"));
        Assert.Equal("Customer name", Eval("DBGETPROP('customer.name', 'FIELD', 'Caption')"));
        Assert.Equal("!", Eval("DBGETPROP('customer.name', 'FIELD', 'Format')"));
        Assert.Equal("People who buy", Eval("DBGETPROP('customer', 'TABLE', 'Comment')"));
        Assert.Contains("negative", Assert.Throws<VfpException>(() => Run("UPDATE orders SET amount = -1 WHERE id = 1")).Message);
        Assert.Contains("required", Assert.Throws<VfpException>(() => Run("UPDATE customer SET name = '' WHERE id = 2")).Message);
        Run("INSERT INTO orders (custid) VALUES (2)");
        Assert.Equal("5.00", Eval("TRANSFORM(orders.amount)"));
        Assert.Equal(ErrorCodes.TriggerFailed, Assert.Throws<VfpException>(() => Run("DELETE FROM customer WHERE id = 1")).Number);
        Run("DELETE TRIGGER ON customer FOR DELETE\nALTER TABLE orders DROP CHECK\nALTER TABLE orders ALTER COLUMN amount DROP DEFAULT");
        Assert.Equal("", Eval("DBGETPROP('orders', 'TABLE', 'RuleExpression')"));
        Assert.Equal("", Eval("DBGETPROP('customer', 'TABLE', 'DeleteTrigger')"));
        Assert.Throws<VfpException>(() => Run("=DBSETPROP('orders', 'TABLE', 'RuleExpression', '.T.')"));

        // Properties survive a rebuild and reopening the database.
        Run("ALTER TABLE customer ADD COLUMN phone C(12)");
        Run("CLOSE DATABASES ALL\nOPEN DATABASE sales");
        Assert.Equal("Customer name", Eval("DBGETPROP('customer.name', 'FIELD', 'Caption')"));
        Assert.Equal("!EMPTY(name)", Eval("DBGETPROP('customer.name', 'FIELD', 'RuleExpression')"));
    }

    [Fact]
    public void Alter_table_rebuild_keeps_relations_autoincrement_values_and_other_work_areas()
    {
        Sales();
        Run("USE customer AGAIN IN 0 ALIAS cust\nSELECT cust\nSET ORDER TO id");
        Run("ALTER TABLE customer ADD COLUMN phone C(12)");
        Assert.Single(Db.Relations());
        Assert.True(Rt.Session.FindAlias("cust")!.InUse);   // other work areas stay open
        Assert.Equal("ID", Rt.Session.FindAlias("cust")!.Order?.Name);
        Assert.True(Rt.Session.FindAlias("orders")!.InUse);
        // Rows keep their keys, and new rows continue the sequence.
        Run("INSERT INTO customer (name) VALUES ('Initech')");
        Assert.Equal("3", Eval("TRANSFORM(customer.id)"));
        Run("ALTER TABLE customer RENAME COLUMN city TO town");
        Run("SELECT town FROM customer WHERE id = 2 INTO CURSOR q");
        Assert.Equal("Chicago", Eval("TRIM(q.town)"));
        Run("ALTER TABLE customer ALTER COLUMN name C(30)");
        Assert.Equal("30", Eval("TRANSFORM(FSIZE('name', 'customer'))"));
        Assert.Equal("Acme", Eval("TRIM(LOOKUP(customer.name, 1, customer.id))"));
    }

    [Fact]
    public void Table_design_changes_scripts_and_apply()
    {
        Sales();
        var design = TableDesign.From(Db.OpenTable("customer").Schema);
        design.FindField("city")!.Field = design.FindField("city")!.Field with { Width = 25 };
        design.Fields.Insert(1, new FieldDesign(new FieldDef("CODE", 'C', 6) { Caption = "Code" }));
        design.FindField("name")!.Field = design.FindField("name")!.Field with { Name = "CUSTNAME", Caption = "Name" };
        design.Tags.Add(new TagDef("CUSTNAME", "UPPER(custname)"));
        design.RuleExpr = "!EMPTY(custname)";
        var changes = design.Changes(Db.OpenTable("customer").Schema).Select(c => c.Kind).ToList();
        Assert.Contains(SchemaChangeKind.AddField, changes);
        Assert.Contains(SchemaChangeKind.RenameField, changes);
        Assert.Contains(SchemaChangeKind.AlterField, changes);
        Assert.Contains(SchemaChangeKind.AddTag, changes);
        Assert.Contains(SchemaChangeKind.TableProperties, changes);
        var script = design.Script(Db.OpenTable("customer").Schema, inDatabase: true);
        Assert.Contains("ALTER TABLE CUSTOMER ADD COLUMN CODE C(6)", script);
        Assert.Contains("ALTER TABLE CUSTOMER RENAME COLUMN NAME TO CUSTNAME", script);
        Assert.Contains("ALTER TABLE CUSTOMER ALTER COLUMN CITY C(25)", script);
        Assert.Contains("INDEX ON UPPER(custname) TAG CUSTNAME", script);
        Assert.Contains("ALTER TABLE CUSTOMER SET CHECK !EMPTY(custname)", script);
        Assert.Contains("=DBSETPROP(\"CUSTOMER.CUSTNAME\", \"FIELD\", \"Caption\", \"Name\")", script);

        var table = Rt.ApplyTableDesign(design, Db);
        Assert.Equal(["ID", "CODE", "CUSTNAME", "CITY"], table.Fields.Select(f => f.Name));
        Assert.Equal("Globex", Eval("TRIM(LOOKUP(customer.custname, 2, customer.id))"));   // renamed field keeps its data
        Assert.Single(Db.Relations());
        Assert.Equal("!EMPTY(custname)", Db.OpenTable("customer").Schema.RuleExpr);
        Assert.Equal(("Code", "Name"), (table.Fields[1].Caption, table.Fields[2].Caption));

        // Changes without a rebuild: a comment and an index only.
        var d2 = TableDesign.From(Db.OpenTable("customer").Schema);
        d2.Comment = "Buyers";
        d2.Tags.RemoveAll(t => t.Name == "CUSTNAME");
        Assert.DoesNotContain(d2.Changes(Db.OpenTable("customer").Schema), c => c.NeedsRebuild);
        Rt.ApplyTableDesign(d2, Db);
        Assert.Equal("Buyers", Db.OpenTable("customer").Schema.Comment);
        Assert.Null(Db.OpenTable("customer").Schema.FindTag("CUSTNAME"));

        // A new table.
        var fresh = TableDesign.New("regions");
        fresh.Fields.Add(new FieldDesign(new FieldDef("CODE", 'C', 3)));
        fresh.Fields.Add(new FieldDesign(new FieldDef("NAME", 'V', 40)));
        fresh.Tags.Add(new TagDef("CODE", "code", Kind: TagKind.Primary));
        Rt.ApplyTableDesign(fresh, Db);
        Assert.True(Db.HasTable("regions"));
        Assert.Equal(TagKind.Primary, Db.OpenTable("regions").Schema.FindTag("code")!.Kind);

        var bad = TableDesign.New("bad name");
        Assert.Contains(bad.Validate(), e => e.Contains("not a valid table name"));
        Assert.Contains(bad.Validate(), e => e.Contains("at least one field"));
    }

    [Fact]
    public void Free_table_designs_rebuild_the_file_and_refuse_database_features()
    {
        Run("CREATE TABLE items FREE (code C(5), qty N(5))\nINSERT INTO items VALUES ('A', 1)\nINSERT INTO items VALUES ('B', 2)\nUSE");
        var path = Path.Combine(Dir, "items.jpt");
        var store = Rt.Session.StoreOf(path);
        var design = TableDesign.From(store.OpenTable(store.TableNames()[0]).Schema);
        design.Fields.Add(new FieldDesign(new FieldDef("PRICE", 'Y')));
        design.Tags.Add(new TagDef("CODE", "code", Kind: TagKind.Candidate));
        Rt.ApplyTableDesign(design, null, path);
        Run("USE items\nSET ORDER TO code\nGO BOTTOM");
        Assert.Equal("B", Eval("TRIM(code)"));
        Assert.Equal("3", Eval("TRANSFORM(FCOUNT())"));
        Run("USE");
        var d2 = TableDesign.From(Rt.Session.StoreOf(path).OpenTable("items").Schema);
        d2.RuleExpr = "qty > 0";
        Assert.Contains("free table", Assert.Throws<VfpException>(() => Rt.ApplyTableDesign(d2, null, path)).Message);
        Assert.Contains("CREATE TABLE ITEMS FREE", d2.Script(null, inDatabase: false));
    }

    [Fact]
    public void Rename_table_and_stored_procedure_files()
    {
        Sales();
        Run("RENAME TABLE customer TO client");
        Assert.True(Db.HasTable("client"));
        Assert.Equal("CLIENT", Assert.Single(Db.Relations()).ParentTable);
        Assert.True(Rt.Session.FindAlias("client")!.InUse);

        File.WriteAllText(Path.Combine(Dir, "procs.prg"), "FUNCTION Twice(n)\nRETURN n * 2\n");
        Run("APPEND PROCEDURES FROM procs");
        Assert.Equal("8", Eval("TRANSFORM(Twice(4))"));
        File.WriteAllText(Path.Combine(Dir, "more.prg"), "FUNCTION Thrice(n)\nRETURN n * 3\n");
        Run("APPEND PROCEDURES FROM more");
        Assert.Equal("9", Eval("TRANSFORM(Thrice(3))"));
        Assert.Equal("8", Eval("TRANSFORM(Twice(4))"));
        Run("APPEND PROCEDURES FROM more OVERWRITE\nCOPY PROCEDURES TO out");
        Assert.DoesNotContain("Twice", File.ReadAllText(Path.Combine(Dir, "out.prg")));
    }

    [Fact]
    public void Schema_diff_scripts_turn_one_database_into_another()
    {
        // Version 1.
        Sales();
        Run("CLOSE DATABASES ALL");
        File.Copy(Path.Combine(Dir, "sales.jpdb"), Path.Combine(Dir, "deployed.jpdb"));
        // Version 2: a new field, a changed field, a removed field, a new table, a view, procedures, a changed relation.
        Run("""
            OPEN DATABASE sales
            ALTER TABLE customer ADD COLUMN email V(60) NULL
            ALTER TABLE customer ALTER COLUMN city C(30)
            ALTER TABLE customer ALTER COLUMN name SET CHECK !EMPTY(name) ERROR "Name is required"
            =DBSETPROP('customer.email', 'FIELD', 'Caption', 'E-mail')
            ALTER TABLE orders DROP COLUMN amount
            ALTER TABLE orders ADD COLUMN total Y
            CREATE TABLE notes (id I, custid I, body M)
            INDEX ON custid TAG custid
            CREATE TRIGGER ON notes FOR INSERT AS .T.
            CREATE SQL VIEW bigorders AS SELECT * FROM orders WHERE total > 100
            ALTER TABLE orders DROP FOREIGN KEY TAG custid SAVE
            ALTER TABLE orders ADD FOREIGN KEY TAG custid REFERENCES customer ON DELETE RESTRICT
            """);
        File.WriteAllText(Path.Combine(Dir, "p.prg"), "FUNCTION Hello\nRETURN 'hi'\n");
        Run("APPEND PROCEDURES FROM p");
        var target = DatabaseSchema.Read(Db);
        Run("CLOSE DATABASES ALL\nOPEN DATABASE deployed");
        var deployed = DatabaseSchema.Read(Db);
        var script = DatabaseSchema.DiffScript(deployed, target);
        Assert.Contains("ALTER TABLE CUSTOMER ADD COLUMN EMAIL V(60) NULL", script);
        Assert.Contains("ALTER TABLE ORDERS DROP COLUMN AMOUNT", script);
        Assert.Contains("CREATE TABLE NOTES (", script);
        Assert.Contains("CREATE SQL VIEW BIGORDERS AS", script);
        Assert.Contains("REFERENCES CUSTOMER TAG ID ON DELETE RESTRICT", script);
        Assert.Contains("APPEND PROCEDURES FROM joepro-procedures.prg OVERWRITE", script);

        // Running the script on the deployed database gives the new version, keeping its rows.
        File.WriteAllText(Path.Combine(Dir, "upgrade.prg"), script);
        Run("DO upgrade");
        var upgraded = DatabaseSchema.Read(Db);
        Assert.Equal(target.CreateScript().Replace("SALES", "X"), upgraded.CreateScript().Replace("DEPLOYED", "X"));
        Assert.Equal("* The databases have the same definitions; nothing to change.", DatabaseSchema.DiffScript(upgraded, target).Split('\n')[2]);
        Assert.Equal("Globex", Eval("TRIM(LOOKUP(customer.name, 2, customer.id))"));
        Assert.Equal("hi", Eval("Hello()"));
    }
}
