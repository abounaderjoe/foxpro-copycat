using System.Buffers.Binary;
using System.Text;

namespace JoePro.Legacy.Formats;

/// <summary>An index tag definition recovered from a .CDX or .IDX file.</summary>
public sealed record IndexTagInfo(string Name, string KeyExpression, string? ForExpression, bool Descending, bool Unique, bool Candidate, int KeyLength, int HeaderOffset);

/// <summary>
/// Reads index definitions from FoxPro compound (.CDX) and single (.IDX) index files.
/// Joe Pro rebuilds every index from its expression (judgment call J1): the binary B-tree is
/// only read for optional integrity cross-checks, because non-MACHINE collation weights are unpublished.
/// </summary>
public sealed class CdxFile : IDisposable
{
    private const int NodeSize = 512;
    private const byte OptUnique = 0x01;
    private const byte OptCandidate = 0x04; // TODO(oracle): candidate bit is not in Microsoft's published table.
    private const byte OptFor = 0x08;
    private const byte OptCompact = 0x20;
    private const byte OptCompound = 0x40;

    private readonly FileStream _fs;
    public string Path { get; }
    public IReadOnlyList<IndexTagInfo> Tags { get; }

    private CdxFile(string path, FileStream fs, Encoding encoding)
    {
        Path = path;
        _fs = fs;
        var root = ReadTagHeader(0, "", encoding, allowCompound: true);
        var options = ReadBytes(0, 16)[14];
        if ((options & OptCompound) != 0)
        {
            var tags = new List<IndexTagInfo>();
            foreach (var (key, offset) in ReadAllKeys(0, spacePad: true))
            {
                var name = Encoding.ASCII.GetString(key).TrimEnd(' ', '\0');
                tags.Add(ReadTagHeader(offset, name, encoding, allowCompound: false));
            }
            Tags = tags;
        }
        else
        {
            Tags = [root with { Name = System.IO.Path.GetFileNameWithoutExtension(path).ToUpperInvariant() }];
        }
    }

    public static CdxFile Open(string path, Encoding? encoding = null)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try { return new CdxFile(path, fs, encoding ?? Encoding.Latin1); }
        catch { fs.Dispose(); throw; }
    }

    private byte[] ReadBytes(long pos, int count)
    {
        if (pos < 0 || pos + count > _fs.Length) throw new InvalidDataException($"Index file '{Path}' is truncated or corrupt.");
        var b = new byte[count];
        _fs.Position = pos;
        _fs.ReadExactly(b);
        return b;
    }

    private IndexTagInfo ReadTagHeader(int offset, string name, Encoding enc, bool allowCompound)
    {
        var h = ReadBytes(offset, 1024);
        var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(12));
        var options = h[14];
        if ((options & OptCompact) == 0 && !allowCompound)
            throw new InvalidDataException($"Tag {name} in '{Path}' is not in compact format.");
        if ((options & OptCompact) == 0)
        {
            // Non-compact .IDX (FoxBASE/FoxPro 1.x): key expression at 16, FOR expression at 236, 220 bytes each.
            var keyExpr = CString(h.AsSpan(16, 220), enc);
            var forExpr = CString(h.AsSpan(236, 220), enc);
            return new IndexTagInfo(name, keyExpr, forExpr.Length == 0 ? null : forExpr, false, (options & OptUnique) != 0, false, keyLen, offset);
        }
        var descending = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(502)) != 0;
        var pool = h.AsSpan(512, 512);
        var keyExpression = CString(pool, enc);
        string? forExpression = null;
        if ((options & OptFor) != 0)
        {
            var rest = pool[Math.Min(pool.Length, enc.GetByteCount(keyExpression) + 1)..];
            var fe = CString(rest, enc);
            forExpression = fe.Length == 0 ? null : fe;
        }
        return new IndexTagInfo(name, keyExpression, forExpression, descending, (options & OptUnique) != 0,
            (options & OptCandidate) != 0, keyLen, offset);
    }

    private static string CString(ReadOnlySpan<byte> b, Encoding enc)
    {
        var n = b.IndexOf((byte)0);
        return enc.GetString(n < 0 ? b : b[..n]).Trim();
    }

    /// <summary>
    /// Returns all (key, record number) pairs of the tag whose header is at <paramref name="headerOffset"/>,
    /// in index order. For the tag bag, the "record number" is the offset of each tag's header.
    /// </summary>
    public IEnumerable<(byte[] Key, int RecNo)> ReadAllKeys(int headerOffset, bool spacePad)
    {
        var h = ReadBytes(headerOffset, 16);
        var node = BinaryPrimitives.ReadInt32LittleEndian(h);
        var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(12));
        var visited = new HashSet<int>();

        // Descend to the leftmost leaf.
        while (true)
        {
            if (!visited.Add(node)) throw new InvalidDataException($"Cycle detected in index '{Path}'.");
            var n = ReadBytes(node, NodeSize);
            var attr = BinaryPrimitives.ReadUInt16LittleEndian(n);
            if ((attr & 0x02) != 0) break;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(n.AsSpan(2));
            if (count == 0) yield break;
            node = BinaryPrimitives.ReadInt32BigEndian(n.AsSpan(12 + keyLen + 4));
        }

        // Walk leaves through right-sibling pointers.
        visited.Clear();
        while (node != -1 && visited.Add(node))
        {
            var n = ReadBytes(node, NodeSize);
            foreach (var entry in DecodeLeaf(n, keyLen, spacePad)) yield return entry;
            node = BinaryPrimitives.ReadInt32LittleEndian(n.AsSpan(8));
        }
    }

    private static List<(byte[], int)> DecodeLeaf(byte[] n, int keyLen, bool spacePad)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(n.AsSpan(2));
        var recMask = BinaryPrimitives.ReadUInt32LittleEndian(n.AsSpan(14));
        int dupMask = n[18], trailMask = n[19];
        int recBits = n[20], dupBits = n[21];
        int entryBytes = n[23];
        if (entryBytes is < 1 or > 8) throw new InvalidDataException("Invalid leaf node in index file.");
        var result = new List<(byte[], int)>(count);
        var prev = new byte[keyLen];
        int keyPos = NodeSize;
        for (int i = 0; i < count; i++)
        {
            ulong raw = 0;
            var at = 24 + i * entryBytes;
            for (int b = entryBytes - 1; b >= 0; b--) raw = (raw << 8) | n[at + b];
            var recNo = (int)(raw & recMask);
            var dup = (int)((raw >> recBits) & (ulong)dupMask);
            var trail = (int)((raw >> (recBits + dupBits)) & (ulong)trailMask);
            var newBytes = keyLen - dup - trail;
            if (newBytes < 0 || dup > keyLen) throw new InvalidDataException("Invalid key compression in index file.");
            keyPos -= newBytes;
            var key = new byte[keyLen];
            prev.AsSpan(0, dup).CopyTo(key);
            n.AsSpan(keyPos, newBytes).CopyTo(key.AsSpan(dup));
            key.AsSpan(dup + newBytes).Fill(spacePad ? (byte)' ' : (byte)0);
            result.Add((key, recNo));
            prev = key;
        }
        return result;
    }

    public void Dispose() => _fs.Dispose();
}
