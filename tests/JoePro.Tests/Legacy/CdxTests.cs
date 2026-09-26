using System.Buffers.Binary;
using System.Text;
using JoePro.Legacy.Formats;

namespace JoePro.Tests.Legacy;

/// <summary>
/// Builds minimal compound index files that follow Microsoft's published CDX layout.
/// TODO(oracle): add CDX files produced by real VFP 9 to the corpus.
/// </summary>
public class CdxTests
{
    [Fact]
    public void Reads_tag_definitions_from_compound_index()
    {
        var path = Path.Combine(TestPaths.TempDir(), "cust.cdx");
        File.WriteAllBytes(path, BuildCdx(
            ("CUSTID", "cust_id", null, false, 0x01),
            ("NAME", "UPPER(lastname+firstname)", "!DELETED()", true, 0x08)));
        using var cdx = CdxFile.Open(path);
        Assert.Equal(2, cdx.Tags.Count);
        var id = cdx.Tags.Single(t => t.Name == "CUSTID");
        Assert.Equal("cust_id", id.KeyExpression);
        Assert.True(id.Unique);
        Assert.Null(id.ForExpression);
        var name = cdx.Tags.Single(t => t.Name == "NAME");
        Assert.Equal("UPPER(lastname+firstname)", name.KeyExpression);
        Assert.Equal("!DELETED()", name.ForExpression);
        Assert.True(name.Descending);
    }

    private static byte[] BuildCdx(params (string Name, string Expr, string? For, bool Desc, byte Opts)[] tags)
    {
        // Layout: [0] tag bag header (1024) [1024] tag bag leaf (512) [1536..] per tag: header (1024) + empty leaf (512)
        var size = 1536 + tags.Length * 1536;
        var f = new byte[size];
        WriteHeader(f, 0, rootNode: 1024, keyLen: 10, opts: 0x20 | 0x40 | 0x01, expr: "", forExpr: null, desc: false);
        var sorted = tags.Select((t, i) => (t.Name, Offset: 1536 + i * 1536)).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        WriteLeaf(f, 1024, 10, sorted.Select(t => (Encoding.ASCII.GetBytes(t.Name.PadRight(10)), t.Offset)).ToList());
        for (int i = 0; i < tags.Length; i++)
        {
            var off = 1536 + i * 1536;
            var t = tags[i];
            WriteHeader(f, off, rootNode: off + 1024, keyLen: 8, opts: (byte)(0x20 | t.Opts), expr: t.Expr, forExpr: t.For, desc: t.Desc);
            WriteLeaf(f, off + 1024, 8, []);
        }
        return f;
    }

    private static void WriteHeader(byte[] f, int off, int rootNode, int keyLen, byte opts, string expr, string? forExpr, bool desc)
    {
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(off), rootNode);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(off + 4), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(off + 12), (ushort)keyLen);
        f[off + 14] = opts;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(off + 502), (ushort)(desc ? 1 : 0));
        var pool = Encoding.ASCII.GetBytes(expr + "\0" + (forExpr ?? "") + "\0");
        pool.CopyTo(f, off + 512);
    }

    private static void WriteLeaf(byte[] f, int off, int keyLen, List<(byte[] Key, int Rec)> keys)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(off), 3); // root + leaf
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(off + 2), (ushort)keys.Count);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(off + 4), -1);
        BinaryPrimitives.WriteInt32LittleEndian(f.AsSpan(off + 8), -1);
        // 3-byte entries: 16 bits recno, 4 bits dup, 4 bits trail.
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(off + 14), 0xFFFF);
        f[off + 18] = 0x0F; f[off + 19] = 0x0F; f[off + 20] = 16; f[off + 21] = 4; f[off + 22] = 4; f[off + 23] = 3;
        int keyPos = off + 512;
        byte[] prev = new byte[keyLen];
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i].Key;
            int dup = 0;
            while (dup < keyLen && k[dup] == prev[dup] && i > 0) dup++;
            int trail = 0;
            while (trail < keyLen - dup && k[keyLen - 1 - trail] == (byte)' ') trail++;
            var newBytes = keyLen - dup - trail;
            keyPos -= newBytes;
            Array.Copy(k, dup, f, keyPos, newBytes);
            uint raw = (uint)keys[i].Rec | (uint)dup << 16 | (uint)trail << 20;
            f[off + 24 + i * 3] = (byte)raw;
            f[off + 25 + i * 3] = (byte)(raw >> 8);
            f[off + 26 + i * 3] = (byte)(raw >> 16);
            prev = k;
        }
    }
}
