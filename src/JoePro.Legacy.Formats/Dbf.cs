using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using JoePro.Core;

namespace JoePro.Legacy.Formats;

/// <summary>A field descriptor as stored in a DBF header.</summary>
public sealed record DbfField(string Name, char Type, int Offset, int Length, int Decimals, byte Flags, long AutoIncNext, int AutoIncStep)
{
    public const byte FlagSystem = 0x01;
    public const byte FlagNullable = 0x02;
    public const byte FlagBinary = 0x04;
    public const byte FlagAutoInc = 0x0C;

    public bool IsSystem => (Flags & FlagSystem) != 0;
    public bool IsNullable => (Flags & FlagNullable) != 0;
    public bool IsBinary => (Flags & FlagBinary) != 0;
    public bool IsAutoInc => (Flags & FlagAutoInc) == FlagAutoInc;
    public bool IsVarLength => Type is 'V' or 'Q';

    /// <summary>Bit index in _NullFlags that marks a short Varchar/Varbinary value, or -1.</summary>
    public int VarLengthBit { get; internal set; } = -1;
    /// <summary>Bit index in _NullFlags that marks NULL, or -1.</summary>
    public int NullBit { get; internal set; } = -1;

    public FieldDef ToFieldDef()
    {
        var t = Type switch { 'P' => 'W', '+' => 'I', '@' => 'T', 'O' => 'B', _ => Type };
        var width = t is 'M' or 'G' or 'W' ? 4 : Length;
        return new FieldDef(Name, t, width, Decimals)
        {
            Nullable = IsNullable,
            Binary = IsBinary,
            AutoIncNext = IsAutoInc || Type == '+' ? AutoIncNext : null,
            AutoIncStep = IsAutoInc ? Math.Max(1, AutoIncStep) : 1,
        };
    }
}

public sealed record DbfRecord(int RecNo, bool Deleted, Value[] Values);

/// <summary>
/// Reads FoxBASE+, FoxPro 2.x and Visual FoxPro tables (.DBF) including memo fields (.FPT/.DBT).
/// Reading only; live legacy tables are written through the VFP OLE DB provider (see architecture doc).
/// </summary>
public sealed class DbfTable : IDisposable
{
    private readonly FileStream _stream;
    private readonly FptFile? _memo;
    private readonly byte[] _recordBuffer;
    private readonly DbfField? _nullFlags;

    public string Path { get; }
    public byte FileType { get; }
    public DateOnly? LastUpdate { get; }
    public int RecordCount { get; }
    public int HeaderLength { get; }
    public int RecordLength { get; }
    public byte TableFlags { get; }
    public byte CodePageMark { get; }
    public Encoding Encoding { get; }
    /// <summary>True if the code page came from the file; false if it was unmarked and a default was used.</summary>
    public bool CodePageFromFile { get; }
    public string? DbcBacklink { get; }
    /// <summary>User fields, excluding hidden system fields such as _NullFlags.</summary>
    public IReadOnlyList<DbfField> Fields { get; }
    public IReadOnlyList<DbfField> AllFields { get; }
    public bool HasStructuralCdx => (TableFlags & 0x01) != 0;
    public bool HasMemo => (TableFlags & 0x02) != 0 || FileType is 0x83 or 0xF5 or 0x8B or 0xCB;
    public bool IsDbc => (TableFlags & 0x04) != 0;
    public bool IsVisualFoxPro => FileType is 0x30 or 0x31 or 0x32;
    public string? MemoPath => _memo?.Path;

    public string FormatName => FileType switch
    {
        0x02 => "FoxBASE",
        0x03 => "FoxBASE+/dBASE III PLUS (no memo)",
        0x30 => "Visual FoxPro",
        0x31 => "Visual FoxPro (autoincrement)",
        0x32 => "Visual FoxPro (Varchar/Varbinary)",
        0x83 => "FoxBASE+/dBASE III PLUS (memo)",
        0x8B => "dBASE IV (memo)",
        0xCB => "dBASE IV SQL table (memo)",
        0xF5 => "FoxPro 2.x (memo)",
        0xFB => "FoxBASE",
        _ => $"Unknown (0x{FileType:X2})",
    };

    static DbfTable() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private DbfTable(string path, FileStream stream, Encoding? encodingOverride)
    {
        Path = path;
        _stream = stream;
        var header = new byte[32];
        ReadExactly(0, header);
        FileType = header[0];
        if (header[2] is >= 1 and <= 12 && header[3] is >= 1 and <= 31)
        {
            var yy = header[1];
            var year = yy < 100 ? (yy < 80 ? 2000 + yy : 1900 + yy) : 1900 + yy;
            try { LastUpdate = new DateOnly(year, header[2], header[3]); } catch (ArgumentOutOfRangeException) { }
        }
        RecordCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        HeaderLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8));
        RecordLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10));
        TableFlags = header[28];
        CodePageMark = header[29];
        var cp = CodePages.FromMark(CodePageMark);
        CodePageFromFile = cp != null;
        Encoding = encodingOverride ?? cp ?? Encoding.GetEncoding(1252);

        if (RecordCount < 0 || HeaderLength < 33 || RecordLength < 1 || HeaderLength > stream.Length)
            throw new InvalidDataException($"'{path}' is not a valid table (corrupt header).");

        // Field descriptors: 32 bytes each until 0x0D.
        var fields = new List<DbfField>();
        var desc = new byte[32];
        long pos = 32;
        int computedOffset = 1;
        while (pos + 1 <= HeaderLength)
        {
            ReadExactly(pos, desc.AsSpan(0, 1));
            if (desc[0] == 0x0D) break;
            ReadExactly(pos, desc);
            var name = Encoding.ASCII.GetString(desc, 0, 11);
            var nul = name.IndexOf('\0');
            if (nul >= 0) name = name[..nul];
            var type = (char)desc[11];
            var offset = BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(12));
            int length = desc[16];
            int decimals = desc[17];
            if (type == 'C' && !IsVisualFoxPro && decimals > 0)
            {
                // Clipper/FoxBASE extension: character fields longer than 255 use decimals as the high byte.
                length += decimals * 256;
                decimals = 0;
            }
            if (!IsVisualFoxPro || offset <= 0) offset = computedOffset;
            computedOffset += length;
            fields.Add(new DbfField(name.Trim().ToUpperInvariant(), type, offset, length, decimals, desc[18],
                BinaryPrimitives.ReadInt32LittleEndian(desc.AsSpan(19)), desc[23]));
            pos += 32;
        }

        // VFP backlink: 263 bytes after the terminator.
        if (IsVisualFoxPro && pos + 1 + 263 <= HeaderLength)
        {
            var bl = new byte[263];
            ReadExactly(pos + 1, bl);
            var s = Encoding.GetString(bl).TrimEnd('\0', ' ');
            DbcBacklink = s.Length == 0 ? null : s;
        }

        // Assign _NullFlags bits. TODO(oracle J13): confirm the bit order for fields that are both Varchar and nullable.
        int bit = 0;
        foreach (var f in fields)
        {
            if (f.Type == '0') continue;
            if (f.IsVarLength) f.VarLengthBit = bit++;
            if (f.IsNullable) f.NullBit = bit++;
        }
        _nullFlags = fields.FirstOrDefault(f => f.Type == '0');
        AllFields = fields;
        Fields = fields.Where(f => f.Type != '0' && !(f.IsSystem && f.Name.StartsWith('_'))).ToList();

        _recordBuffer = new byte[RecordLength];
        if (fields.Any(f => f.Type is 'M' or 'G' or 'P' or 'W' || (f.Type == 'B' && !IsVisualFoxPro)))
        {
            var memoPath = FindMemo(path);
            if (memoPath != null) _memo = FptFile.Open(memoPath);
        }
    }

    public static DbfTable Open(string path, Encoding? encodingOverride = null)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try { return new DbfTable(path, fs, encodingOverride); }
        catch { fs.Dispose(); throw; }
    }

    private static string? FindMemo(string dbfPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbfPath) ?? ".";
        var stem = System.IO.Path.GetFileNameWithoutExtension(dbfPath);
        var ext = System.IO.Path.GetExtension(dbfPath).ToUpperInvariant();
        // Companion extensions for design-time tables (SCX→SCT, etc.).
        var memoExt = ext switch
        {
            ".SCX" => ".SCT", ".VCX" => ".VCT", ".FRX" => ".FRT", ".LBX" => ".LBT",
            ".MNX" => ".MNT", ".PJX" => ".PJT", ".DBC" => ".DCT", _ => ".FPT",
        };
        foreach (var candidate in new[] { memoExt, ".DBT" })
        {
            foreach (var file in Directory.EnumerateFiles(dir, stem + ".*"))
            {
                if (string.Equals(System.IO.Path.GetExtension(file), candidate, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(System.IO.Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }
        return null;
    }

    private void ReadExactly(long position, Span<byte> buffer)
    {
        _stream.Position = position;
        _stream.ReadExactly(buffer);
    }

    /// <summary>Reads a record by its 1-based record number.</summary>
    public DbfRecord ReadRecord(int recNo)
    {
        if (recNo < 1 || recNo > RecordCount) throw new ArgumentOutOfRangeException(nameof(recNo));
        long pos = HeaderLength + (long)(recNo - 1) * RecordLength;
        if (pos + RecordLength > _stream.Length) throw new InvalidDataException($"Record {recNo} is truncated in '{Path}'.");
        ReadExactly(pos, _recordBuffer);
        return new DbfRecord(recNo, _recordBuffer[0] == (byte)'*', DecodeRecord(_recordBuffer));
    }

    public IEnumerable<DbfRecord> Records()
    {
        for (int i = 1; i <= RecordCount; i++) yield return ReadRecord(i);
    }

    private bool NullFlag(byte[] rec, int bit)
    {
        if (_nullFlags == null || bit < 0) return false;
        var byteIndex = bit / 8;
        if (byteIndex >= _nullFlags.Length) return false;
        return (rec[_nullFlags.Offset + byteIndex] & (1 << (bit % 8))) != 0;
    }

    private Value[] DecodeRecord(byte[] rec)
    {
        var values = new Value[Fields.Count];
        for (int i = 0; i < Fields.Count; i++)
        {
            var f = Fields[i];
            if (f.IsNullable && NullFlag(rec, f.NullBit))
            {
                values[i] = Value.Null;
                continue;
            }
            values[i] = DecodeField(f, rec.AsSpan(f.Offset, f.Length), rec);
        }
        return values;
    }

    private Value DecodeField(DbfField f, ReadOnlySpan<byte> raw, byte[] rec)
    {
        switch (f.Type)
        {
            case 'C':
                return Value.String(f.IsBinary ? Latin1(raw) : Encoding.GetString(raw));
            case 'V':
            case 'Q':
            {
                var len = raw.Length;
                if (NullFlag(rec, f.VarLengthBit)) len = raw[^1];
                var data = raw[..Math.Min(len, raw.Length)];
                return f.Type == 'Q' ? Value.Binary(data.ToArray()) : Value.String(f.IsBinary ? Latin1(data) : Encoding.GetString(data));
            }
            case 'N':
            case 'F':
            {
                var s = Encoding.ASCII.GetString(raw).Trim();
                if (s.Length == 0 || s.All(c => c == '*')) return Value.Number(0, f.Decimals);
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? Value.Number(d, f.Decimals)
                    : throw new InvalidDataException($"Invalid numeric value '{s}' in field {f.Name}.");
            }
            case 'I':
            case '+':
                return Value.Number(BinaryPrimitives.ReadInt32LittleEndian(raw), 0);
            case 'B' when IsVisualFoxPro:
            case 'O':
                return Value.Number(BinaryPrimitives.ReadDoubleLittleEndian(raw), f.Decimals);
            case 'Y':
                return Value.Currency(BinaryPrimitives.ReadInt64LittleEndian(raw) / 10000m);
            case 'D':
            {
                var s = Encoding.ASCII.GetString(raw).Trim();
                if (s.Length != 8 || !int.TryParse(s, out _)) return Value.EmptyDate;
                if (!DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return Value.EmptyDate;
                return Value.DateOf(d);
            }
            case 'T':
            case '@':
            {
                var day = BinaryPrimitives.ReadInt32LittleEndian(raw);
                var ms = BinaryPrimitives.ReadInt32LittleEndian(raw[4..]);
                if (day == 0) return Value.EmptyDateTime;
                return Value.DateTimeFromJulianMs(day * Julian.MsPerDay + ms);
            }
            case 'L':
                return (char)raw[0] switch
                {
                    'T' or 't' or 'Y' or 'y' => Value.True,
                    'F' or 'f' or 'N' or 'n' => Value.False,
                    _ => f.IsNullable ? Value.Null : Value.False, // '?' or blank = not initialized
                };
            case 'M':
            case 'G':
            case 'W':
            case 'P':
            case 'B':
            {
                var block = f.Length == 4
                    ? BinaryPrimitives.ReadInt32LittleEndian(raw)
                    : int.TryParse(Encoding.ASCII.GetString(raw).Trim(), out var b) ? b : 0;
                if (block <= 0 || _memo == null)
                    return f.Type == 'M' ? Value.EmptyString : Value.Binary([]);
                var bytes = _memo.ReadBlock(block);
                return f.Type == 'M' && !f.IsBinary ? Value.String(Encoding.GetString(bytes)) : Value.Binary(bytes);
            }
            default:
                return Value.Binary(raw.ToArray());
        }
    }

    private static string Latin1(ReadOnlySpan<byte> raw) => Encoding.Latin1.GetString(raw);

    public void Dispose()
    {
        _stream.Dispose();
        _memo?.Dispose();
    }
}

/// <summary>Reads FoxPro memo files (.FPT) and dBASE memo files (.DBT).</summary>
public sealed class FptFile : IDisposable
{
    private readonly FileStream _stream;
    public string Path { get; }
    public int BlockSize { get; }
    public bool IsDbt { get; }

    private FptFile(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
        IsDbt = path.EndsWith(".dbt", StringComparison.OrdinalIgnoreCase);
        var header = new byte[8];
        stream.ReadExactly(header);
        if (IsDbt)
        {
            BlockSize = 512;
        }
        else
        {
            BlockSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6));
            if (BlockSize == 0) BlockSize = 64;
        }
    }

    public static FptFile Open(string path) =>
        new(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

    public byte[] ReadBlock(int block)
    {
        long pos = (long)block * BlockSize;
        if (pos >= _stream.Length) throw new InvalidDataException($"Memo block {block} is past the end of '{Path}'.");
        _stream.Position = pos;
        if (IsDbt)
        {
            // dBASE III: text terminated by 0x1A 0x1A.
            var ms = new MemoryStream();
            int b, prev = -1;
            while ((b = _stream.ReadByte()) >= 0)
            {
                if (b == 0x1A && prev == 0x1A) { ms.SetLength(ms.Length - 1); break; }
                ms.WriteByte((byte)b);
                prev = b;
            }
            return ms.ToArray();
        }
        var head = new byte[8];
        _stream.ReadExactly(head);
        var length = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4));
        if (length < 0 || pos + 8 + length > _stream.Length)
            throw new InvalidDataException($"Memo block {block} in '{Path}' has an invalid length.");
        var data = new byte[length];
        _stream.ReadExactly(data);
        return data;
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Maps the DBF code page mark (header byte 29) to an encoding.</summary>
public static class CodePages
{
    private static readonly Dictionary<byte, int> Marks = new()
    {
        [0x01] = 437, [0x02] = 850, [0x03] = 1252, [0x04] = 10000, [0x64] = 852, [0x65] = 866,
        [0x66] = 865, [0x67] = 861, [0x6A] = 737, [0x6B] = 857, [0x78] = 950, [0x79] = 949,
        [0x7A] = 936, [0x7B] = 932, [0x7C] = 874, [0x7D] = 1255, [0x7E] = 1256, [0x96] = 10007,
        [0x97] = 10029, [0x98] = 10006, [0xC8] = 1250, [0xC9] = 1251, [0xCA] = 1254, [0xCB] = 1253,
        [0xCC] = 1257,
    };

    static CodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding? FromMark(byte mark) =>
        Marks.TryGetValue(mark, out var cp) ? Encoding.GetEncoding(cp) : null;

    public static Encoding Get(int codePage) => Encoding.GetEncoding(codePage);

    public static byte ToMark(int codePage) => Marks.FirstOrDefault(kv => kv.Value == codePage).Key;
}
