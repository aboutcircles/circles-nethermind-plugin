using System.Collections.Concurrent;

internal static class LatencyStats
{
    private const int MaxSamples = 20_000;                 // fixed-size ring buffer
    private static readonly ConcurrentQueue<double> _q = new();

    public static void Record(double ms)
    {
        _q.Enqueue(ms);
        while (_q.Count > MaxSamples && _q.TryDequeue(out _)) { }
    }

    public static (int Count, double Avg, double P95) Snapshot()
    {
        var snap = _q.ToArray();                           // single copy
        if (snap.Length == 0) return (0, 0, 0);

        Array.Sort(snap);
        var count = snap.Length;
        var avg = snap.Average();
        var p95 = snap[(int)Math.Floor(0.95 * (count - 1))];

        return (count, avg, p95);
    }
}