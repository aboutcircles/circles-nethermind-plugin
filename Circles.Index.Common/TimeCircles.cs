namespace Circles.Index.Common;

public static class TimeCirclesConverter
{
    private static readonly long CirclesInceptionTimestamp =
        new DateTime(2020, 10, 15, 0, 0, 0, DateTimeKind.Utc).Ticks / TimeSpan.TicksPerMillisecond;

    private const decimal OneDayInMilliseconds = 86400m * 1000m;
    private const decimal OneCirclesYearInDays = 365.25m;
    private const decimal OneCirclesYearInMilliseconds = OneCirclesYearInDays * 24m * 60m * 60m * 1000m;

    private static decimal GetCrcPayoutAt(long timestamp)
    {
        decimal daysSinceCirclesInception = (timestamp - CirclesInceptionTimestamp) / OneDayInMilliseconds;
        decimal circlesYearsSince = (timestamp - CirclesInceptionTimestamp) / OneCirclesYearInMilliseconds;
        decimal daysInCurrentCirclesYear = daysSinceCirclesInception % OneCirclesYearInDays;

        decimal initialDailyCrcPayout = 8m;
        decimal circlesPayoutInCurrentYear = initialDailyCrcPayout;
        decimal previousCirclesPerDayValue = initialDailyCrcPayout;

        for (int index = 0; index < circlesYearsSince; index++)
        {
            previousCirclesPerDayValue = circlesPayoutInCurrentYear;
            circlesPayoutInCurrentYear *= 1.07m;
        }

        decimal x = previousCirclesPerDayValue;
        decimal y = circlesPayoutInCurrentYear;
        decimal a = daysInCurrentCirclesYear / OneCirclesYearInDays;

        return x * (1 - a) + y * a;
    }

    public static decimal CrcToTc(DateTime timestamp, decimal amount)
    {
        long ts = timestamp.Ticks / TimeSpan.TicksPerMillisecond;
        decimal payoutAtTimestamp = GetCrcPayoutAt(ts);
        return amount / payoutAtTimestamp * 24m;
    }
}