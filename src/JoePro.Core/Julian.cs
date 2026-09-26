namespace JoePro.Core;

/// <summary>Julian day number helpers. VFP stores dates as Julian day numbers (SYS(11)): 2000-01-01 = 2451545.</summary>
public static class Julian
{
    public const long MsPerDay = 86_400_000L;
    private const long DayNumberOffset = 1_721_426L; // Julian day of 0001-01-01 (proleptic Gregorian)

    public static long FromDate(DateOnly d) => d.DayNumber + DayNumberOffset;

    public static DateOnly ToDate(long julianDay) => DateOnly.FromDayNumber((int)(julianDay - DayNumberOffset));

    public static bool IsValidDay(long julianDay) =>
        julianDay - DayNumberOffset >= DateOnly.MinValue.DayNumber && julianDay - DayNumberOffset <= DateOnly.MaxValue.DayNumber;

    public static long ToJulianMs(DateTime dt) =>
        FromDate(DateOnly.FromDateTime(dt)) * MsPerDay + (long)dt.TimeOfDay.TotalMilliseconds;

    public static DateTime ToDateTime(long julianMs)
    {
        var day = julianMs / MsPerDay;
        var ms = julianMs % MsPerDay;
        return ToDate(day).ToDateTime(TimeOnly.MinValue).AddMilliseconds(ms);
    }
}
