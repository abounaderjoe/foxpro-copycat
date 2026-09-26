using JoePro.Core;

namespace JoePro.Tests.Core;

public class ValueTests
{
    [Fact]
    public void Julian_matches_vfp_sys11()
    {
        Assert.Equal(2451545, Julian.FromDate(new DateOnly(2000, 1, 1)));
        Assert.Equal(new DateOnly(2000, 1, 1), Julian.ToDate(2451545));
    }

    [Theory]
    [InlineData("abc", "ab", StringCompareMode.RightLength, 0)]
    [InlineData("ab", "abc", StringCompareMode.RightLength, -1)]
    [InlineData("abc", "", StringCompareMode.RightLength, 0)]
    [InlineData("abc", "ab", StringCompareMode.Padded, 1)]
    [InlineData("ab", "ab  ", StringCompareMode.Padded, 0)]
    [InlineData("ab", "ab  ", StringCompareMode.Identical, -1)]
    [InlineData("B", "a", StringCompareMode.Padded, -1)]
    public void String_comparison_follows_set_exact(string a, string b, StringCompareMode mode, int expected)
    {
        Assert.Equal(expected, Math.Sign(VfpCompare.CompareStrings(a, b, mode)));
    }

    [Fact]
    public void Comparing_string_with_number_is_a_type_error()
    {
        var ex = Assert.Throws<VfpException>(() => VfpCompare.Compare(Value.String("1"), Value.Number(1), StringCompareMode.Padded));
        Assert.Equal(ErrorCodes.OperatorOperandMismatch, ex.Number);
    }

    [Fact]
    public void Empty_follows_vfp_rules()
    {
        Assert.True(Value.String("   ").IsEmpty);
        Assert.True(Value.Number(0).IsEmpty);
        Assert.True(Value.EmptyDate.IsEmpty);
        Assert.True(Value.False.IsEmpty);
        Assert.False(Value.Null.IsEmpty);
        Assert.False(Value.String(" x").IsEmpty);
    }

    [Fact]
    public void Dates_format_per_set_date()
    {
        var d = Value.DateOf(new DateOnly(2024, 1, 31));
        var o = new SetOptions();
        Assert.Equal("01/31/24", Formatter.DateToString(d.JulianDay, o));
        o.Century = true;
        Assert.Equal("01/31/2024", Formatter.DateToString(d.JulianDay, o));
        o.Date = DateFormat.German;
        Assert.Equal("31.01.2024", Formatter.DateToString(d.JulianDay, o));
        o.Date = DateFormat.Ansi;
        Assert.Equal("2024.01.31", Formatter.DateToString(d.JulianDay, o));
        Assert.Equal("    .  .  ", Formatter.DateToString(0, o));
    }

    [Fact]
    public void Datetime_formats_with_12_hour_clock()
    {
        var t = Value.DateTimeOf(new DateTime(2024, 1, 31, 14, 5, 9));
        Assert.Equal("01/31/24 02:05:09 PM", Formatter.DateTimeToString(t.JulianMs, new SetOptions()));
    }

    [Fact]
    public void Rounding_is_half_away_from_zero()
    {
        Assert.Equal("3", Formatter.FormatNumber(2.5, 0, SetOptions.Default));
        Assert.Equal("-3", Formatter.FormatNumber(-2.5, 0, SetOptions.Default));
        Assert.Equal("0.13", Formatter.FormatNumber(0.125, 2, SetOptions.Default));
    }

    [Fact]
    public void Key_encoding_preserves_order()
    {
        double[] nums = [-1e10, -3.5, -1, 0, 0.25, 1, 2, 1e12];
        for (int i = 1; i < nums.Length; i++)
            Assert.True(Cmp(KeyEncoder.Encode(Value.Number(nums[i - 1])), KeyEncoder.Encode(Value.Number(nums[i]))) < 0);
        string[] strs = ["", "A", "AB", "ABC", "B", "a"];
        for (int i = 1; i < strs.Length; i++)
            Assert.True(Cmp(KeyEncoder.Encode(Value.String(strs[i - 1])), KeyEncoder.Encode(Value.String(strs[i]))) < 0);
        Assert.Equal(KeyEncoder.Encode(Value.String("AB  ")), KeyEncoder.Encode(Value.String("AB")));
        Assert.True(Cmp(KeyEncoder.Encode(Value.Null), KeyEncoder.Encode(Value.String(""))) < 0);
    }

    private static int Cmp(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    [Fact]
    public void Character_fields_pad_and_truncate()
    {
        var f = new FieldDef("name", 'C', 5).Normalize();
        Assert.Equal("ab   ", f.Coerce(Value.String("ab")).AsString);
        Assert.Equal("abcde", f.Coerce(Value.String("abcdefg")).AsString);
        Assert.Throws<VfpException>(() => f.Coerce(Value.Number(1)));
    }

    [Fact]
    public void Numeric_fields_round_and_detect_overflow()
    {
        var f = new FieldDef("amt", 'N', 6, 2).Normalize();
        Assert.Equal(12.35, f.Coerce(Value.Number(12.345)).AsNumber);
        Assert.Throws<VfpException>(() => f.Coerce(Value.Number(12345.5)));
    }
}
