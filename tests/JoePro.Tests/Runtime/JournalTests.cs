using JoePro.Data;

namespace JoePro.Tests.Runtime;

public class JournalTests : RuntimeHarness
{
    [Fact]
    public void The_journal_records_every_change_with_its_origin()
    {
        Run("""
            CREATE DATABASE shop
            CREATE TABLE item (id I, name C(10), qty N(5))
            INSERT INTO item VALUES (1, 'before', 1)
            =DBSETPROP('shop', 'DATABASE', 'Journal', .T.)
            INSERT INTO item VALUES (2, 'bolt', 5)
            REPLACE qty WITH 7 FOR id = 2
            DELETE FOR id = 1
            RECALL FOR id = 1
            """);
        var db = Rt.Session.CurrentDatabase!;
        Assert.Equal(".T.", Eval("DBGETPROP('shop', 'DATABASE', 'Journal')"));
        var j = db.ReadJournal();
        Assert.Equal("IUDR", string.Concat(j.Select(e => e.Op)));
        Assert.All(j, e => Assert.Equal(("ITEM", "local"), (e.Table, e.Origin)));
        Assert.Equal(5, j[1].Old!["QTY"].AsNumber);
        Assert.Equal(7, j[1].New!["QTY"].AsNumber);
        Assert.Equal(["QTY"], j[1].ChangedFields);
        Assert.Equal("bolt", j[0].New!["NAME"].AsString.Trim());
        Assert.Null(j[2].New);
        db.Origin = "sync";
        Run("REPLACE name WITH 'nut' FOR id = 2");
        Assert.Equal("sync", db.ReadJournal(j[^1].Seq).Single().Origin);
        Assert.Equal(5, db.JournalHead);
        Assert.Equal(4, db.TrimJournal(4));
        Assert.Single(db.ReadJournal());
        // A change that fails is not journaled (it is part of the same transaction).
        Run("ALTER TABLE item ADD PRIMARY KEY id TAG id");
        Assert.ThrowsAny<Exception>(() => Run("INSERT INTO item VALUES (2, 'dup', 0)"));
        Assert.Single(db.ReadJournal());
    }
}
