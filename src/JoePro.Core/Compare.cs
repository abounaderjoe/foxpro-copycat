namespace JoePro.Core;

/// <summary>How character comparisons treat strings of different lengths.</summary>
public enum StringCompareMode
{
    /// <summary>SET EXACT OFF / SET ANSI OFF: compare until the end of the right operand.</summary>
    RightLength,
    /// <summary>SET EXACT ON / SET ANSI ON: pad the shorter string with blanks.</summary>
    Padded,
    /// <summary>The == operator: character-for-character, lengths must match.</summary>
    Identical,
}

/// <summary>FoxPro comparison semantics (machine collation).</summary>
public static class VfpCompare
{
    /// <summary>
    /// Compares two non-null values. Returns negative, zero or positive.
    /// Throws a data type mismatch error for incompatible types, as VFP does.
    /// </summary>
    public static int Compare(Value a, Value b, StringCompareMode mode)
    {
        switch (a.Kind, b.Kind)
        {
            case (ValueKind.Character, ValueKind.Character):
                return CompareStrings(a.AsString, b.AsString, mode);
            case (ValueKind.Number or ValueKind.Currency, ValueKind.Number or ValueKind.Currency):
                if (a.Kind == ValueKind.Currency || b.Kind == ValueKind.Currency)
                    return a.AsCurrency.CompareTo(b.AsCurrency);
                return a.AsNumber.CompareTo(b.AsNumber);
            case (ValueKind.Date or ValueKind.DateTime, ValueKind.Date or ValueKind.DateTime):
                return a.Kind == ValueKind.Date && b.Kind == ValueKind.Date
                    ? a.JulianDay.CompareTo(b.JulianDay)
                    : a.JulianMs.CompareTo(b.JulianMs);
            case (ValueKind.Logical, ValueKind.Logical):
                return a.AsBool.CompareTo(b.AsBool);
            case (ValueKind.Binary, ValueKind.Binary):
                return CompareBytes(a.AsBinary, b.AsBinary, mode);
            case (ValueKind.Object, ValueKind.Object):
                return ReferenceEquals(a.AsObject, b.AsObject) ? 0 : 1;
            default:
                throw VfpException.OperatorTypeMismatch();
        }
    }

    // TODO(oracle): verify padding behavior of "ab" = "ab  " under SET EXACT OFF against VFP 9.
    public static int CompareStrings(string a, string b, StringCompareMode mode)
    {
        switch (mode)
        {
            case StringCompareMode.Identical:
                return string.CompareOrdinal(a, b);
            case StringCompareMode.RightLength:
                if (b.Length == 0) return 0;
                if (a.Length > b.Length) a = a[..b.Length];
                goto default;
            default:
                var n = Math.Max(a.Length, b.Length);
                for (int i = 0; i < n; i++)
                {
                    var ca = i < a.Length ? a[i] : ' ';
                    var cb = i < b.Length ? b[i] : ' ';
                    if (ca != cb) return ca < cb ? -1 : 1;
                }
                return 0;
        }
    }

    private static int CompareBytes(byte[] a, byte[] b, StringCompareMode mode)
    {
        if (mode == StringCompareMode.RightLength && a.Length > b.Length) a = a[..b.Length];
        var c = a.AsSpan().SequenceCompareTo(b);
        return c;
    }

    /// <summary>Equality used by SEEK-style lookups, INLIST(), ASCAN() and similar functions.</summary>
    public static bool AreEqual(Value a, Value b, StringCompareMode mode)
    {
        if (a.IsNull || b.IsNull) return false;
        return Compare(a, b, mode) == 0;
    }
}
