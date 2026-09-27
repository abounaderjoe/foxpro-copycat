using JoePro.Documents.Queries;

namespace JoePro.Tests.Documents;

public class QueryDocumentTests
{
    private static QueryDocument Sample()
    {
        var q = new QueryDocument { Distinct = true, Top = 10, Destination = QueryDestination.Cursor, IntoName = "qtop", ReadWrite = true };
        q.Tables.Add(new QueryTable { Name = "sales!customer", Alias = "c", X = 20, Y = 30 });
        q.Tables.Add(new QueryTable { Name = "orders", Join = JoinKind.Left, On = "orders.custid = c.id AND orders.total > 0", X = 260, Y = 30 });
        q.Fields.Add(new QueryField("c.name"));
        q.Fields.Add(new QueryField("SUM(orders.total)", "total"));
        q.Filters.Add(new QueryCondition("c.city", "=", "'Boston'", Or: true));
        q.Filters.Add(new QueryCondition("c.city", "IN", "('Austin', 'Dallas')"));
        q.Filters.Add(new QueryCondition("orders.date", "BETWEEN", "{^2024-01-01} AND {^2024-12-31}", Not: true));
        q.Filters.Add(new QueryCondition("EMPTY(c.closed)"));
        q.Filters.Add(new QueryCondition("c.phone", "IS NULL", Not: true));
        q.GroupBy.Add("c.name");
        q.Having.Add(new QueryCondition("SUM(orders.total)", ">", "100"));
        q.OrderBy.Add(new QueryOrder("2", Descending: true));
        q.OrderBy.Add(new QueryOrder("c.name"));
        return q;
    }

    [Fact]
    public void Visual_query_to_sql_and_back_is_identical()
    {
        var q = Sample();
        var sql = q.ToSql();
        Assert.Equal("""
            SELECT DISTINCT TOP 10 c.name, SUM(orders.total) AS total ;
               FROM sales!customer c ;
               LEFT OUTER JOIN orders ON orders.custid = c.id AND orders.total > 0 ;
               WHERE c.city = 'Boston' OR c.city IN ('Austin', 'Dallas') AND NOT orders.date BETWEEN {^2024-01-01} AND {^2024-12-31} AND EMPTY(c.closed) AND c.phone IS NOT NULL ;
               GROUP BY c.name ;
               HAVING SUM(orders.total) > 100 ;
               ORDER BY 2 DESC, c.name ;
               INTO CURSOR qtop READWRITE
            """.Replace("\r\n", "\n"), sql);
        var back = QueryDocument.FromSql(sql);
        Assert.False(back.IsSqlOnly, back.SqlOnlyReason);
        Assert.Equal(sql, back.ToSql());
        // Everything but the diagram positions (which SQL does not carry) reads back.
        foreach (var t in back.Tables) { t.X = q.FindTable(t.Ref)!.X; t.Y = q.FindTable(t.Ref)!.Y; }
        Assert.Equal(QuerySerializer.Write(q), QuerySerializer.Write(back));
        Assert.Equal(QuerySerializer.Write(q), QuerySerializer.Write(QuerySerializer.Parse(QuerySerializer.Write(q))));
    }

    [Fact]
    public void Reads_vfp_generated_sql_with_nested_joins_and_continuations()
    {
        var sql = """
            SELECT Customer.company, Orders.order_date, Orditems.quantity;
             FROM  testdata!customer INNER JOIN testdata!orders;
                INNER JOIN testdata!orditems ;
               ON  Orders.order_id = Orditems.order_id ;
               ON  Customer.cust_id = Orders.cust_id;
             WHERE Customer.country = "Germany";
             ORDER BY Customer.company
            """;
        var q = QueryDocument.FromSql(sql);
        Assert.False(q.IsSqlOnly, q.SqlOnlyReason);
        Assert.Equal(["testdata!customer", "testdata!orders", "testdata!orditems"], q.Tables.Select(t => t.Name));
        Assert.Equal("Customer.cust_id = Orders.cust_id", q.Tables[1].On);
        Assert.Equal("Orders.order_id = Orditems.order_id", q.Tables[2].On);
        Assert.Equal(("Customer.country", "=", "\"Germany\""), (q.Filters[0].Left, q.Filters[0].Op, q.Filters[0].Right));
        Assert.Equal("customer", q.Tables[0].Ref);
        // Written back in sequential style, which reads back the same.
        Assert.Equal(q.ToSql(), QueryDocument.FromSql(q.ToSql()).ToSql());
    }

    [Fact]
    public void Queries_the_designer_cannot_show_stay_as_sql()
    {
        foreach (var (sql, reason) in new[]
        {
            ("SELECT a FROM t1 UNION SELECT a FROM t2", "UNION"),
            ("SELECT x.a FROM (SELECT a FROM t) x", "derived table"),
            ("SELECT a FROM t TO PRINTER", "TO PRINTER"),
            ("DELETE FROM t", "not a SELECT"),
        })
        {
            var q = QueryDocument.FromSql(sql);
            Assert.True(q.IsSqlOnly, sql);
            Assert.Contains(reason, q.SqlOnlyReason);
            Assert.Equal(sql, q.ToSql());
            Assert.Equal(sql, QuerySerializer.Parse(QuerySerializer.Write(q)).ToSql());
        }
        // Old-style joins in WHERE and plain expressions are representable.
        var old = QueryDocument.FromSql("SELECT * FROM customer, orders WHERE orders.custid = customer.id AND !DELETED() ORDER BY customer.name DESC");
        Assert.False(old.IsSqlOnly);
        Assert.Null(old.Tables[1].Join);
        Assert.Empty(old.Fields);
        Assert.Equal("!DELETED()", old.Filters[1].Left);
        Assert.Equal("SELECT * ;\n   FROM customer, orders ;\n   WHERE orders.custid = customer.id AND !DELETED() ;\n   ORDER BY customer.name DESC", old.ToSql());
        Assert.Contains("BROWSE", old.RunCode());
        Assert.Contains("INTO CURSOR query", old.RunCode());
    }

    [Fact]
    public void Qpr_files_convert_to_queries()
    {
        var r = LegacyQueryConverter.ConvertText("""
            * Generated by the Query Designer
            SELECT Customer.cust_id, Customer.company;
             FROM customer;
             WHERE Customer.maxordamt > 1000;
             INTO CURSOR bigones
            """);
        Assert.Empty(r.Findings);
        Assert.Equal(QueryDestination.Cursor, r.Document.Destination);
        Assert.Equal("bigones", r.Document.IntoName);
        Assert.Equal(("Customer.maxordamt", ">", "1000"), (r.Document.Filters[0].Left, r.Document.Filters[0].Op, r.Document.Filters[0].Right));
        var sqlOnly = LegacyQueryConverter.ConvertText("SELECT a FROM t1 UNION SELECT a FROM t2\nREPORT FORM x");
        Assert.True(sqlOnly.Document.IsSqlOnly);
        Assert.Equal(2, sqlOnly.Findings.Count);
    }
}
