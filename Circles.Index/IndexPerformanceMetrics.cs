using System.Globalization;

namespace Circles.Index;

public class IndexPerformanceMetrics
{
    private long _startProcessTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long _startPeriodTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private long _totalTransactionsInPeriod = 0;
    private long _totalTransactions = 0;

    private long _totalLogsInPeriod = 0;
    private long _totalLogs = 0;

    private long _totalBlocksInPeriod = 0;
    private long _totalBlocks = 0;

    private Timer _metricsOutputTimer;

    public IndexPerformanceMetrics()
    {
        _metricsOutputTimer = new Timer((_) =>
        {
            var metrics = GetAveragesOverLastPeriod();
            var output = string.Format(CultureInfo.CurrentCulture,
                "[Metrics] Time: {0,-10} {1,-14} {2,9:n0} ({3,15:000,000,000,000}), {4,-16} {5,9:n0} ({6,15:000,000,000,000}), {7,-14} {8,9:n0} ({9,15:000,000,000,000})",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _startProcessTimestamp,
                "Blocks/s:", metrics.BlocksPerSecond, _totalBlocks,
                "Transactions/s:", metrics.TransactionsPerSecond, _totalTransactions,
                "Logs/s:", metrics.LogsPerSecond, _totalLogs);

            Console.WriteLine(output);
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public void LogBlockWithReceipts(BlockWithReceipts blockWithReceipts)
    {
        var logs = blockWithReceipts.Receipts.Sum(receipt => receipt.Logs?.Length ?? 0);
        Interlocked.Add(ref _totalLogsInPeriod, logs);
        Interlocked.Add(ref _totalLogs, logs);

        var transactions = blockWithReceipts.Block.Transactions.Length;
        Interlocked.Add(ref _totalTransactionsInPeriod, transactions);
        Interlocked.Add(ref _totalTransactions, transactions);

        Interlocked.Increment(ref _totalBlocksInPeriod);
        Interlocked.Increment(ref _totalBlocks);
    }

    private (double TransactionsPerSecond, double BlocksPerSecond, double LogsPerSecond)
        GetAveragesOverLastPeriod()
    {
        long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        double elapsedSecondsInPeriod = currentTimestamp - _startPeriodTimestamp;
        elapsedSecondsInPeriod = Math.Max(elapsedSecondsInPeriod, 1);

        double transactionsPerSecond =
            Interlocked.Read(ref _totalTransactionsInPeriod) / elapsedSecondsInPeriod;
        Interlocked.Exchange(ref _totalTransactionsInPeriod, 0);

        double blocksPerSecond = Interlocked.Read(ref _totalBlocksInPeriod) / elapsedSecondsInPeriod;
        Interlocked.Exchange(ref _totalBlocksInPeriod, 0);

        double logsPerSecond = Interlocked.Read(ref _totalLogsInPeriod) / elapsedSecondsInPeriod;
        Interlocked.Exchange(ref _totalLogsInPeriod, 0);

        _startPeriodTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return (TransactionsPerSecond: transactionsPerSecond,
            BlocksPerSecond: blocksPerSecond, LogsPerSecond: logsPerSecond);
    }
}