using JoePro.Core;
using JoePro.Legacy.Formats;

namespace JoePro.Tests.Legacy;

public class DbfTests
{
    [Fact]
    public void Reads_real_vfp_table_with_memo_and_deleted_record()
    {
        using var t = DbfTable.Open(TestPaths.Corpus("dbfread", "memotest.dbf"));
        Assert.True(t.IsVisualFoxPro);
        Assert.Null(t.DbcBacklink);
        Assert.Equal(3, t.RecordCount);
        Assert.Equal(["NAME", "BIRTHDATE", "MEMO"], t.Fields.Select(f => f.Name));
        Assert.Equal(['C', 'D', 'M'], t.Fields.Select(f => f.Type));

        var recs = t.Records().ToList();
        Assert.Equal("Alice", recs[0].Values[0].AsString.TrimEnd());
        Assert.Equal(new DateOnly(1987, 3, 1), Julian.ToDate(recs[0].Values[1].JulianDay));
        Assert.Equal("Alice memo", recs[0].Values[2].AsString);
        Assert.Equal("Bob memo", recs[1].Values[2].AsString);
        Assert.False(recs[1].Deleted);
        Assert.True(recs[2].Deleted);
        Assert.Equal("Deleted Guy memo", recs[2].Values[2].AsString);
        Assert.Equal(new DateOnly(1979, 12, 22), Julian.ToDate(recs[2].Values[1].JulianDay));
    }

    [Fact]
    public void Reads_dbase3_table_without_memo()
    {
        using var t = DbfTable.Open(TestPaths.Corpus("dbfread", "people.dbf"));
        Assert.False(t.IsVisualFoxPro);
        Assert.Equal(0x03, t.FileType);
        Assert.Contains(t.Fields, f => f.Name == "NAME");
        Assert.True(t.RecordCount >= 1);
        _ = t.Records().ToList();
    }

    [Fact]
    public void Round_trips_every_vfp_field_type()
    {
        var dir = TestPaths.TempDir();
        var path = Path.Combine(dir, "all.dbf");
        var fields = new List<FieldDef>
        {
            new("CNAME", 'C', 10),
            new("VNOTE", 'V', 20) { Nullable = true },
            new("NQTY", 'N', 8, 2),
            new("IID", 'I') { AutoIncNext = 5, AutoIncStep = 1 },
            new("BRATE", 'B', 8, 4),
            new("YPRICE", 'Y'),
            new("DBORN", 'D') { Nullable = true },
            new("TSTAMP", 'T'),
            new("LACTIVE", 'L'),
            new("MNOTES", 'M'),
            new("WBLOB", 'W'),
            new("QBIN", 'Q', 6),
        };
        var ts = Value.DateTimeOf(new DateTime(2023, 7, 4, 13, 45, 30));
        var rows = new List<(bool, Value[])>
        {
            (false, [Value.String("Widget"), Value.String("short"), Value.Number(12.5), Value.Number(1), Value.Number(3.14159),
                Value.Currency(19.9999m), Value.DateOf(new DateOnly(1990, 5, 17)), ts, Value.True, Value.String("A long memo\r\nwith two lines"),
                Value.Binary([1, 2, 3]), Value.Binary([9, 8])]),
            (true, [Value.String("Gadget"), Value.Null, Value.Number(-3), Value.Number(2), Value.Number(0),
                Value.Currency(-1.5m), Value.Null, Value.EmptyDateTime, Value.False, Value.String(""), Value.Binary([]), Value.Binary([1, 2, 3, 4, 5, 6])]),
        };
        DbfWriter.Write(path, fields, rows);

        using var t = DbfTable.Open(path);
        Assert.Equal(0x32, t.FileType);
        Assert.Equal(12, t.Fields.Count);
        var r = t.Records().ToList();
        Assert.Equal("Widget    ", r[0].Values[0].AsString);
        Assert.Equal("short", r[0].Values[1].AsString);
        Assert.True(r[1].Values[1].IsNull);
        Assert.Equal(12.5, r[0].Values[2].AsNumber);
        Assert.Equal(2, r[0].Values[2].Decimals);
        Assert.Equal(3.14159, r[0].Values[4].AsNumber);
        Assert.Equal(19.9999m, r[0].Values[5].AsCurrency);
        Assert.Equal(-1.5m, r[1].Values[5].AsCurrency);
        Assert.Equal(new DateOnly(1990, 5, 17), Julian.ToDate(r[0].Values[6].JulianDay));
        Assert.True(r[1].Values[6].IsNull);
        Assert.Equal(ts.JulianMs, r[0].Values[7].JulianMs);
        Assert.True(r[1].Values[7].IsEmptyDate);
        Assert.True(r[0].Values[8].AsBool);
        Assert.Equal("A long memo\r\nwith two lines", r[0].Values[9].AsString);
        Assert.Equal("", r[1].Values[9].AsString);
        Assert.Equal(new byte[] { 1, 2, 3 }, r[0].Values[10].AsBinary);
        Assert.Equal(new byte[] { 9, 8 }, r[0].Values[11].AsBinary);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, r[1].Values[11].AsBinary);
        Assert.True(r[1].Deleted);
        var iid = t.Fields.Single(f => f.Name == "IID");
        Assert.True(iid.IsAutoInc);
        Assert.Equal(5, iid.AutoIncNext);
    }

    [Fact]
    public void Code_page_mark_selects_encoding()
    {
        var dir = TestPaths.TempDir();
        var path = Path.Combine(dir, "cp.dbf");
        DbfWriter.Write(path, [new FieldDef("NAME", 'C', 10)], [(false, [Value.String("Müller")])], codePage: 850);
        using var t = DbfTable.Open(path);
        Assert.Equal(0x02, t.CodePageMark);
        Assert.True(t.CodePageFromFile);
        Assert.Equal("Müller", t.ReadRecord(1).Values[0].AsString.TrimEnd());
    }
}
