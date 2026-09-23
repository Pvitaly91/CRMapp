namespace CheckboxBatchPrinter.Core.Services;

public static class DateRangeBuilder
{
    public static TimeZoneInfo KyivZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
    public static DateTime TodayKyiv => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, KyivZone).Date;
    public static (DateTimeOffset From, DateTimeOffset ToExclusive) ForLocalDates(DateOnly from, DateOnly to, TimeZoneInfo? timeZone = null)
    {
        if (to < from)
            throw new ArgumentException("Дата «до» не може бути раніше дати «від».");

        timeZone ??= KyivZone;
        var fromLocal = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var toLocal = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return (new DateTimeOffset(fromLocal, timeZone.GetUtcOffset(fromLocal)),
            new DateTimeOffset(toLocal, timeZone.GetUtcOffset(toLocal)));
    }
}
