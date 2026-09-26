using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using JoePro.Core;

namespace JoePro.Legacy.Formats;

/// <summary>
/// Writes a new Visual FoxPro table (.DBF + .FPT) from scratch. Used by COPY TO … TYPE FOXPRO
/// exports and to build legacy test fixtures. It never modifies an existing legacy table.
/// </summary>
public static class DbfWriter
{
    public const int DefaultMemoBlockSize = 64;

    public static void Write(string path, IReadOnlyList<FieldDef> fields, IEnumerable<(bool Deleted, Value[] Values)> records,
        int codePage = 1252, string? dbcBacklink = null)
    {
        fields = fields.Select(f => f.Normalize()).ToList();
        var encoding = CodePages.Get(codePage);
        bool hasMemo = fields.Any(f => f.Type is 'M' or 'G' or 'W');
        bool hasVar = fields.Any(f => f.Type is 'V' or 'Q');
        bool hasAutoInc = fields.Any(f => f.AutoIncNext.HasValue);

        // Allocate _NullFlags bits (same order as the reader).
        int bits = 0;
        var varBit = new int[fields.Count];
        var nullBit = new int[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            varBit[i] = fields[i].Type is 'V' or 'Q' ? bits++ : -1;
            nullBit[i] = fields[i].Nullable ? bits++ : -1;
        }
        int nullFlagsLen = bits == 0 ? 0 : (bits + 7) / 8;

        var offsets = new int[fields.Count];
        int recLen = 1;
        for (int i = 0; i < fields.Count; i++)
        {
            offsets[i] = recLen;
            recLen += fields[i].Width;
        }
        int nullFlagsOffset = recLen;
        recLen += nullFlagsLen;

        int fieldCount = fields.Count + (nullFlagsLen > 0 ? 1 : 0);
        int headerLen = 32 + fieldCount * 32 + 1 + 263;

        var ext = System.IO.Path.GetExtension(path).ToUpperInvariant();
        var memoPath = System.IO.Path.ChangeExtension(path, ext switch
        {
            ".SCX" => ".sct", ".VCX" => ".vct", ".FRX" => ".frt", ".LBX" => ".lbt", ".MNX" => ".mnt", ".PJX" => ".pjt", ".DBC" => ".dct", _ => ".fpt",
        });
        using var memo = hasMemo ? new FptBuilder(memoPath, DefaultMemoBlockSize) : null;
        var recordList = new List<byte[]>();
        foreach (var (deleted, values) in records)
        {
            var rec = new byte[recLen];
            rec.AsSpan().Fill((byte)' ');
            rec[0] = deleted ? (byte)'*' : (byte)' ';
            if (nullFlagsLen > 0) rec.AsSpan(nullFlagsOffset, nullFlagsLen).Clear();
            for (int i = 0; i < fields.Count; i++)
            {
                var f = fields[i];
                var v = i < values.Length ? values[i] : f.BlankValue();
                var slot = rec.AsSpan(offsets[i], f.Width);
                if (v.IsNull)
                {
                    if (!f.Nullable) throw new InvalidOperationException($"Field {f.Name} does not accept null values.");
                    SetBit(rec, nullFlagsOffset, nullBit[i]);
                    if (f.Type is not ('C' or 'N' or 'F' or 'D' or 'L')) slot.Clear();
                    continue;
                }
                EncodeField(f, f.Coerce(v), slot, encoding, memo, rec, nullFlagsOffset, varBit[i]);
            }
            recordList.Add(rec);
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var header = new byte[headerLen];
        header[0] = hasVar ? (byte)0x32 : hasAutoInc ? (byte)0x31 : (byte)0x30;
        var today = DateTime.Today;
        header[1] = (byte)(today.Year % 100);
        header[2] = (byte)today.Month;
        header[3] = (byte)today.Day;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), recordList.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)headerLen);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)recLen);
        header[28] = (byte)((hasMemo ? 0x02 : 0x00) | (ext == ".DBC" ? 0x04 : 0x00));
        header[29] = CodePages.ToMark(codePage);
        int p = 32;
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            WriteDescriptor(header.AsSpan(p, 32), f.Name, f.Type, offsets[i], f.Width, f.Decimals,
                (byte)((f.Nullable ? DbfField.FlagNullable : 0) | (f.Binary ? DbfField.FlagBinary : 0) | (f.AutoIncNext.HasValue ? DbfField.FlagAutoInc : 0)),
                (int)(f.AutoIncNext ?? 0), f.AutoIncNext.HasValue ? f.AutoIncStep : 0);
            p += 32;
        }
        if (nullFlagsLen > 0)
        {
            WriteDescriptor(header.AsSpan(p, 32), "_NullFlags", '0', nullFlagsOffset, nullFlagsLen, 0,
                DbfField.FlagSystem | DbfField.FlagBinary, 0, 0);
            p += 32;
        }
        header[p] = 0x0D;
        if (dbcBacklink != null) encoding.GetBytes(dbcBacklink).AsSpan(0, Math.Min(263, dbcBacklink.Length)).CopyTo(header.AsSpan(p + 1));
        fs.Write(header);
        foreach (var r in recordList) fs.Write(r);
        fs.WriteByte(0x1A);
    }

    private static void SetBit(byte[] rec, int offset, int bit)
    {
        if (bit < 0) return;
        rec[offset + bit / 8] |= (byte)(1 << (bit % 8));
    }

    private static void WriteDescriptor(Span<byte> d, string name, char type, int offset, int length, int decimals, byte flags, int next, int step)
    {
        d.Clear();
        Encoding.ASCII.GetBytes(name.ToUpperInvariant()).AsSpan(0, Math.Min(10, name.Length)).CopyTo(d);
        d[11] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(d[12..], offset);
        d[16] = (byte)length;
        d[17] = (byte)decimals;
        d[18] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(d[19..], next);
        d[23] = (byte)step;
    }

    private static void EncodeField(FieldDef f, Value v, Span<byte> slot, Encoding enc, FptBuilder? memo, byte[] rec, int nfOffset, int varBit)
    {
        switch (f.Type)
        {
            case 'C':
                slot.Fill((byte)' ');
                var cb = enc.GetBytes(v.AsString);
                cb.AsSpan(0, Math.Min(cb.Length, slot.Length)).CopyTo(slot);
                break;
            case 'V':
            case 'Q':
                slot.Clear();
                var vb = f.Type == 'V' ? enc.GetBytes(v.AsString) : v.AsBinary;
                var n = Math.Min(vb.Length, slot.Length);
                vb.AsSpan(0, n).CopyTo(slot);
                if (n < slot.Length)
                {
                    slot[^1] = (byte)n;
                    SetBit(rec, nfOffset, varBit);
                }
                break;
            case 'N':
            case 'F':
                var s = Formatter.FormatNumber(v.AsNumber, f.Decimals, SetOptions.Default).PadLeft(f.Width);
                Encoding.ASCII.GetBytes(s).CopyTo(slot);
                break;
            case 'I':
                BinaryPrimitives.WriteInt32LittleEndian(slot, (int)v.AsNumber);
                break;
            case 'B':
                BinaryPrimitives.WriteDoubleLittleEndian(slot, v.AsNumber);
                break;
            case 'Y':
                BinaryPrimitives.WriteInt64LittleEndian(slot, (long)(v.AsCurrency * 10000m));
                break;
            case 'D':
                var ds = v.IsEmptyDate ? "        " : Julian.ToDate(v.JulianDay).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                Encoding.ASCII.GetBytes(ds).CopyTo(slot);
                break;
            case 'T':
                var ms = v.JulianMs;
                BinaryPrimitives.WriteInt32LittleEndian(slot, (int)(ms / Julian.MsPerDay));
                BinaryPrimitives.WriteInt32LittleEndian(slot[4..], (int)(ms % Julian.MsPerDay));
                break;
            case 'L':
                slot[0] = v.AsBool ? (byte)'T' : (byte)'F';
                break;
            case 'M':
            case 'G':
            case 'W':
                var data = f.Type == 'M' ? enc.GetBytes(v.AsString) : v.AsBinary;
                var block = data.Length == 0 ? 0 : memo!.Add(data, f.Type == 'M' ? 1 : 2);
                BinaryPrimitives.WriteInt32LittleEndian(slot, block);
                break;
        }
    }

    private sealed class FptBuilder : IDisposable
    {
        private readonly FileStream _fs;
        private readonly int _blockSize;
        private int _nextBlock;

        public FptBuilder(string path, int blockSize)
        {
            _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            _blockSize = blockSize;
            _nextBlock = (512 + blockSize - 1) / blockSize;
            _fs.Write(new byte[512]);
        }

        public int Add(byte[] data, int type)
        {
            var block = _nextBlock;
            _fs.Position = (long)block * _blockSize;
            var head = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, type);
            BinaryPrimitives.WriteInt32BigEndian(head.AsSpan(4), data.Length);
            _fs.Write(head);
            _fs.Write(data);
            var total = 8 + data.Length;
            var blocks = (total + _blockSize - 1) / _blockSize;
            var pad = blocks * _blockSize - total;
            if (pad > 0) _fs.Write(new byte[pad]);
            _nextBlock += blocks;
            return block;
        }

        public void Dispose()
        {
            var head = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(head, _nextBlock);
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(6), (ushort)_blockSize);
            _fs.Position = 0;
            _fs.Write(head);
            _fs.Dispose();
        }
    }
}
