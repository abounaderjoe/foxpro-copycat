using System.Buffers.Binary;
using System.Text;

namespace JoePro.Core;

/// <summary>
/// Encodes index key values into byte strings whose byte-wise (memcmp) order matches FoxPro's
/// MACHINE collation order. The storage kernel indexes these bytes, which is how Joe Pro
/// supports arbitrary FoxPro index expressions (like CDX keys) on top of SQLite.
/// </summary>
public static class KeyEncoder
{
    private const byte NullMarker = 0x00;
    private const byte ValueMarker = 0x01;

    public static byte[] Encode(Value v)
    {
        if (v.IsNull) return [NullMarker];
        switch (v.Kind)
        {
            case ValueKind.Character:
                // Trailing blanks are not significant in FoxPro keys (keys are blank padded).
                var s = v.AsString.TrimEnd(' ');
                var bytes = new byte[1 + s.Length * 2];
                bytes[0] = ValueMarker;
                Encoding.BigEndianUnicode.GetBytes(s, 0, s.Length, bytes, 1);
                return bytes;
            case ValueKind.Number:
                return EncodeDouble(v.AsNumber);
            case ValueKind.Currency:
                return EncodeDouble((double)v.AsCurrency);
            case ValueKind.Date:
                return EncodeDouble(v.JulianDay);
            case ValueKind.DateTime:
                return EncodeDouble(v.JulianMs);
            case ValueKind.Logical:
                return [ValueMarker, v.AsBool ? (byte)1 : (byte)0];
            case ValueKind.Binary:
                var b = v.AsBinary;
                var r = new byte[b.Length + 1];
                r[0] = ValueMarker;
                b.CopyTo(r, 1);
                return r;
            default:
                throw new VfpException(ErrorCodes.InvalidArgument, "Invalid index key type.");
        }
    }

    private static byte[] EncodeDouble(double d)
    {
        if (d == 0) d = 0; // normalize -0
        var bits = BitConverter.DoubleToInt64Bits(d);
        ulong u = (ulong)bits;
        u = bits < 0 ? ~u : u ^ 0x8000_0000_0000_0000UL;
        var r = new byte[9];
        r[0] = ValueMarker;
        BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(1), u);
        return r;
    }

    /// <summary>True when <paramref name="key"/> starts with <paramref name="prefix"/>.</summary>
    public static bool StartsWith(byte[] key, byte[] prefix) => key.AsSpan().StartsWith(prefix);

    /// <summary>The smallest byte string greater than every string starting with <paramref name="prefix"/>, or null if none.</summary>
    public static byte[]? PrefixUpperBound(byte[] prefix)
    {
        var r = (byte[])prefix.Clone();
        for (int i = r.Length - 1; i >= 0; i--)
        {
            if (r[i] != 0xFF)
            {
                r[i]++;
                return r[..(i + 1)];
            }
        }
        return null;
    }
}
