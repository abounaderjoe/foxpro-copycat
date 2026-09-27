using JoePro.Documents.Queries;
using JoePro.Data;

namespace JoePro.Tests.Runtime;

/// <summary>Queries (.jpquery): DO runs them, DO query.qpr finds a migrated query, the View Designer's save.</summary>
public class QueryTests : RuntimeHarness
{
    [Fact]
    public void Do_runs_a_query_document_and_qpr_falls_back_to_it()
    {
        Run("CREATE TABLE items FREE (name C(10), qty N(5))\nINSERT INTO items VALUES ('bolt', 5)\nINSERT INTO items VALUES ('nut', 50)\nUSE");
        var q = new QueryDocument { Destination = QueryDestination.Cursor, IntoName = "big" };
        q.Tables.Add(new QueryTable { Name = "items" });
        q.Fields.Add(new QueryField("name"));
        q.Filters.Add(new QueryCondition("qty", ">", "10"));
        q.Save(Path.Combine(Dir, "bigitems.jpquery"));
        Run("DO bigitems.jpquery");
        Assert.Equal("nut", Eval("TRIM(big.name)"));
        Run("USE IN big\nDO bigitems.qpr");
        Assert.Equal("1", Eval("TRANSFORM(RECCOUNT('big'))"));
    }

    [Fact]
    public void Define_view_saves_a_view_with_column_sources()
    {
        Run("CREATE DATABASE shop\nCREATE TABLE customer (id I, name C(20))\nINSERT INTO customer VALUES (1, 'Acme')");
        var db = Rt.Session.CurrentDatabase!;
        var v = Rt.DefineView(db, "allcust", "SELECT id, name FROM customer");
        Assert.Equal("customer.name", v.GetField("name", "UpdateName").AsString);
        Rt.DefineView(db, "allcust", "SELECT name FROM customer WHERE id = 1");
        Assert.Contains("WHERE", db.GetView("allcust")!.Sql);
        Run("USE allcust");
        Assert.Equal("Acme", Eval("TRIM(allcust.name)"));
    }

    [Fact]
    public void Into_after_a_union_applies_to_the_whole_statement()
    {
        Run("CREATE CURSOR c1 (name C(5))\nINSERT INTO c1 VALUES ('a')\nCREATE CURSOR c2 (name C(5))\nINSERT INTO c2 VALUES ('b')");
        Run("SELECT name FROM c1 UNION SELECT name FROM c2 ORDER BY 1 DESC INTO CURSOR both");
        Assert.Equal("2", Eval("TRANSFORM(RECCOUNT('both'))"));
        Assert.Equal("b", Eval("TRIM(both.name)"));
    }
}
