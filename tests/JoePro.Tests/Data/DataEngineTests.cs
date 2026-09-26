using JoePro.Core;
using JoePro.Data;

namespace JoePro.Tests.Data;

public class DataEngineTests : IDisposable
{
    private readonly string _dir = TestPaths.TempDir();
    private readonly DataSession _s;

    public DataEngineTests()
    {
        _s = new DataSession(new SetOptions { Default_ = _dir });
    }

    public void Dispose() => _s.Dispose();

    private WorkArea Customers()
    {
        var schema = new TableSchema("customer", [
            new FieldDef("ID", 'I'), new FieldDef("NAME", 'C', 20), new FieldDef("CITY", 'C', 15), new FieldDef("BALANCE", 'Y')]);
        _s.CreateTable(schema);
        var wa = _s.Use("customer");
        (int, string, string, decimal)[] rows =
        [
            (1, "Smith", "Boston", 10m), (2, "Adams", "Chicago", 0m), (3, "Jones", "Boston", 250.5m),
            (4, "Brown", "Denver", 5m), (5, "Smithers", "Austin", 1m),
        ];
        foreach (var (id, n, c, b) in rows)
            wa.AppendBlank([Value.Number(id), Value.String(n), Value.String(c), Value.Currency(b)]);
        wa.Table.CreateTag(new TagDef("NAME", "NAME"));
        wa.Table.CreateTag(new TagDef("ID", "ID", Kind: TagKind.Candidate));
        wa.GoTop();
        return wa;
    }

    private static List<string> Names(WorkArea wa)
    {
        var list = new List<string>();
        wa.GoTop();
        while (!wa.Eof)
        {
            list.Add(wa.Get("NAME").AsString.TrimEnd());
            wa.Skip();
        }
        return list;
    }

    [Fact]
    public void Natural_order_and_record_numbers()
    {
        var wa = Customers();
        Assert.Equal(5, wa.RecordCount);
        Assert.Equal(["Smith", "Adams", "Jones", "Brown", "Smithers"], Names(wa));
        Assert.True(wa.Eof);
        Assert.Equal(6, wa.RecNo);
        Assert.Equal("                    ", wa.Get("NAME").AsString);
    }

    [Fact]
    public void Index_order_and_descending()
    {
        var wa = Customers();
        wa.SetOrder("NAME");
        Assert.Equal(["Adams", "Brown", "Jones", "Smith", "Smithers"], Names(wa));
        wa.SetOrder("NAME", descending: true);
        Assert.Equal(["Smithers", "Smith", "Jones", "Brown", "Adams"], Names(wa));
    }

    [Fact]
    public void Skip_backwards_and_bof()
    {
        var wa = Customers();
        wa.SetOrder("NAME");
        wa.GoBottom();
        Assert.Equal("Smithers", wa.Get("NAME").AsString.TrimEnd());
        wa.Skip(-2);
        Assert.Equal("Jones", wa.Get("NAME").AsString.TrimEnd());
        wa.Skip(-10);
        Assert.True(wa.Bof);
        Assert.Equal("Adams", wa.Get("NAME").AsString.TrimEnd());
        Assert.Throws<VfpException>(() => wa.Skip(-1));
    }

    [Fact]
    public void Seek_partial_and_exact()
    {
        var wa = Customers();
        wa.SetOrder("NAME");
        Assert.True(wa.Seek(Value.String("Smi")));
        Assert.Equal(1, wa.RecNo);
        Assert.False(wa.Seek(Value.String("Zed")));
        Assert.True(wa.Eof);
        _s.Options.Exact = true;
        Assert.False(wa.Seek(Value.String("Smi")));
        Assert.True(wa.Seek(Value.String("Smithers")));
        Assert.Equal(5, wa.RecNo);
        _s.Options.Near = true;
        Assert.False(wa.Seek(Value.String("C")));
        Assert.Equal("Jones", wa.Get("NAME").AsString.TrimEnd());
    }

    [Fact]
    public void Seek_on_numeric_tag()
    {
        var wa = Customers();
        wa.SetOrder("ID");
        Assert.True(wa.Seek(Value.Number(4)));
        Assert.Equal("Brown", wa.Get("NAME").AsString.TrimEnd());
    }

    [Fact]
    public void Candidate_key_rejects_duplicates()
    {
        var wa = Customers();
        var ex = Assert.Throws<VfpException>(() => wa.AppendBlank([Value.Number(3), Value.String("Dup")]));
        Assert.Equal(ErrorCodes.UniquenessViolated, ex.Number);
        Assert.Equal(5, wa.RecordCount);
    }

    [Fact]
    public void Replace_updates_index_position()
    {
        var wa = Customers();
        wa.SetOrder("NAME");
        wa.Seek(Value.String("Adams"));
        wa.Replace(wa.FieldIndex("NAME"), Value.String("Zimmer"));
        Assert.Equal("Zimmer", wa.Get("NAME").AsString.TrimEnd());
        Assert.Equal(["Brown", "Jones", "Smith", "Smithers", "Zimmer"], Names(wa));
    }

    [Fact]
    public void Deleted_records_pack_and_set_deleted()
    {
        var wa = Customers();
        wa.Go(2);
        wa.Delete();
        Assert.True(wa.Deleted);
        Assert.Equal(5, Names(wa).Count);
        _s.Options.Deleted = true;
        Assert.Equal(["Smith", "Jones", "Brown", "Smithers"], Names(wa));
        _s.Options.Deleted = false;
        wa.Table.Pack();
        Assert.Equal(4, wa.RecordCount);
        wa.Go(2);
        Assert.Equal("Jones", wa.Get("NAME").AsString.TrimEnd());
        wa.SetOrder("NAME");
        Assert.Equal(["Brown", "Jones", "Smith", "Smithers"], Names(wa));
    }

    [Fact]
    public void Filter_limits_navigation()
    {
        var wa = Customers();
        wa.SetFilter(new Pred(r => r.Get(2).AsString.TrimEnd() == "Boston"));
        Assert.Equal(["Smith", "Jones"], Names(wa));
    }

    [Fact]
    public void Table_persists_across_sessions()
    {
        Customers();
        _s.CloseAll();
        using var s2 = new DataSession(new SetOptions { Default_ = _dir });
        var wa = s2.Use("customer");
        Assert.Equal(5, wa.RecordCount);
        wa.SetOrder("NAME");
        wa.GoTop();
        Assert.Equal("Adams", wa.Get("NAME").AsString.TrimEnd());
        Assert.Equal(0m, wa.Get("BALANCE").AsCurrency);
    }

    [Fact]
    public void Optimistic_table_buffering_commits_and_detects_conflicts()
    {
        var wa = Customers();
        wa.SetBuffering(5);
        wa.Go(1);
        wa.Replace(wa.FieldIndex("CITY"), Value.String("Paris"));
        Assert.Equal(2, wa.GetFieldState(wa.FieldIndex("CITY")));
        Assert.Equal("Boston", wa.OldVal(wa.FieldIndex("CITY")).AsString.TrimEnd());
        Assert.Equal("Boston", wa.CurVal(wa.FieldIndex("CITY")).AsString.TrimEnd());
        wa.AppendBlank([Value.Number(6), Value.String("Newbie")]);
        Assert.True(wa.RecNo < 0);
        Assert.Equal(6, wa.RecordCount);
        Assert.True(wa.TableUpdate());
        Assert.Equal(6, wa.Table.RecordCount);
        Assert.Equal("Paris", wa.Table.Read(1)!.Values[2].AsString.TrimEnd());

        // Another session changes record 1 after we buffered an edit: conflict.
        wa.Go(1);
        wa.Replace(wa.FieldIndex("CITY"), Value.String("Rome"));
        using (var other = new DataSession(new SetOptions { Default_ = _dir }))
        {
            var o = other.Use("customer");
            o.Go(1);
            o.Replace(o.FieldIndex("CITY"), Value.String("Oslo"));
        }
        Assert.False(wa.TableUpdate());
        Assert.True(wa.TableUpdate(force: true));
        Assert.Equal("Rome", wa.Table.Read(1)!.Values[2].AsString.TrimEnd());
    }

    [Fact]
    public void Transactions_roll_back_and_nest()
    {
        var wa = Customers();
        _s.BeginTransaction();
        wa.AppendBlank([Value.Number(10), Value.String("Temp")]);
        _s.BeginTransaction();
        wa.Go(1);
        wa.Replace(wa.FieldIndex("NAME"), Value.String("Changed"));
        _s.Rollback();
        Assert.Equal("Smith", wa.Table.Read(1)!.Values[1].AsString.TrimEnd());
        Assert.Equal(6, wa.RecordCount);
        _s.Rollback();
        Assert.Equal(5, wa.RecordCount);
        Assert.Equal(0, _s.TransactionLevel);
    }

    [Fact]
    public void Cursors_live_in_memory()
    {
        var wa = _s.CreateCursor(new TableSchema("tmp", [new FieldDef("A", 'N', 5), new FieldDef("B", 'M')]));
        wa.AppendBlank([Value.Number(1), Value.String("memo text")]);
        Assert.Equal("TMP", wa.Alias);
        Assert.Equal("memo text", wa.Get("B").AsString);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Autoincrement_assigns_values()
    {
        var schema = new TableSchema("orders", [new FieldDef("ID", 'I') { AutoIncNext = 100, AutoIncStep = 10 }, new FieldDef("X", 'C', 1)]);
        _s.CreateTable(schema);
        var wa = _s.Use("orders");
        wa.AppendBlank();
        wa.AppendBlank();
        Assert.Equal(110, wa.Get("ID").AsNumber);
    }

    [Fact]
    public void Database_holds_many_tables()
    {
        _s.CreateDatabase("sales");
        _s.CreateTable(new TableSchema("a", [new FieldDef("X", 'C', 3)]));
        _s.CreateTable(new TableSchema("b", [new FieldDef("Y", 'N', 4)]));
        Assert.True(File.Exists(Path.Combine(_dir, "sales.jpdb")));
        Assert.False(File.Exists(Path.Combine(_dir, "a.jpt")));
        var wa = _s.Use("sales!b");
        Assert.Equal("SALES!B", wa.Source);
        Assert.Equal(2, _s.CurrentDatabase!.TableNames().Count);
    }

    [Fact]
    public void Relations_move_child_record_pointer()
    {
        var cust = Customers();
        var orders = _s.Select(0);
        _s.CreateTable(new TableSchema("orders", [new FieldDef("CUSTID", 'I'), new FieldDef("AMT", 'N', 8, 2)]));
        orders = _s.Use("orders");
        orders.AppendBlank([Value.Number(3), Value.Number(99.5)]);
        orders.Table.CreateTag(new TagDef("CUSTID", "CUSTID"));
        orders.SetOrder("CUSTID");
        cust.Relations.Add((new Pred(r => r.Get(0)), orders));
        cust.Go(3);
        Assert.False(orders.Eof);
        Assert.Equal(99.5, orders.Get("AMT").AsNumber);
        cust.Go(1);
        Assert.True(orders.Eof);
    }

    private sealed class Pred(Func<IRecord, Value> f) : ICompiledExpression
    {
        public Pred(Func<IRecord, bool> b) : this(r => Value.Logical(b(r))) { }
        public string Source => "test";
        public Value Evaluate(IRecord record) => f(record);
    }
}
