namespace Circles.Index.Common
{
    public static class TimeCirclesConverter
    {
        private static readonly long circlesInceptionTimestamp = ConvertToUnixTimestamp(new DateTime(2020, 10, 15, 0, 0, 0, DateTimeKind.Utc));

        private static readonly decimal oneDayInMilliSeconds = 86400m * 1000m;
        private static readonly decimal oneCirclesYearInDays = 365.25m;
        private static readonly decimal oneCirclesYearInMilliSeconds = oneCirclesYearInDays * 24m * 60m * 60m * 1000m;

        public static long ConvertToUnixTimestamp(DateTime dateTime)
        {
            DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)(dateTime - unixEpoch).TotalMilliseconds;
        }
        
        public static decimal GetCrcPayoutAt(long timestamp)
        {
            Console.WriteLine($"GetCrcPayoutAt called with timestamp: {timestamp}");

            decimal daysSinceCirclesInception = (timestamp - circlesInceptionTimestamp) / oneDayInMilliSeconds;
            decimal circlesYearsSince = (timestamp - circlesInceptionTimestamp) / oneCirclesYearInMilliSeconds;
            decimal daysInCurrentCirclesYear = daysSinceCirclesInception % oneCirclesYearInDays;
            decimal initialDailyCrcPayout = 8;

            Console.WriteLine($"daysSinceCirclesInception: {daysSinceCirclesInception}");
            Console.WriteLine($"circlesYearsSince: {circlesYearsSince}");
            Console.WriteLine($"daysInCurrentCirclesYear: {daysInCurrentCirclesYear}");

            decimal circlesPayoutInCurrentYear = initialDailyCrcPayout;
            decimal previousCirclesPerDayValue = initialDailyCrcPayout;

            for (int index = 0; index < circlesYearsSince; index++)
            {
                previousCirclesPerDayValue = circlesPayoutInCurrentYear;
                circlesPayoutInCurrentYear *= 1.07m;
            }

            Console.WriteLine($"previousCirclesPerDayValue: {previousCirclesPerDayValue}");
            Console.WriteLine($"circlesPayoutInCurrentYear: {circlesPayoutInCurrentYear}");

            decimal x = previousCirclesPerDayValue;
            decimal y = circlesPayoutInCurrentYear;
            decimal a = daysInCurrentCirclesYear / oneCirclesYearInDays;

            decimal result = x * (1 - a) + y * a;
            Console.WriteLine($"Interpolated payout: {result}");

            return result;
        }

        public static decimal CrcToTc(DateTime timestamp, decimal amount)
        {
            long ts = ConvertToUnixTimestamp(timestamp);

            Console.WriteLine($"CrcToTc called with timestamp: {ts}, amount: {amount}");

            decimal payoutAtTimestamp = GetCrcPayoutAt(ts);
            decimal result = amount / payoutAtTimestamp * 24m;

            Console.WriteLine($"payoutAtTimestamp: {payoutAtTimestamp}");
            Console.WriteLine($"result: {result}");

            return result;
        }

        public static decimal TcToCrc(DateTime timestamp, decimal amount)
        {
            long ts = ConvertToUnixTimestamp(timestamp);

            Console.WriteLine($"TcToCrc called with timestamp: {ts}, amount: {amount}");

            decimal payoutAtTimestamp = GetCrcPayoutAt(ts);
            decimal result = amount / 24m * payoutAtTimestamp;

            Console.WriteLine($"payoutAtTimestamp: {payoutAtTimestamp}");
            Console.WriteLine($"result: {result}");

            return result;
        }

    }
}