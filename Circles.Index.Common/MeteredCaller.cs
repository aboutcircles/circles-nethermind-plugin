using System.Collections.Concurrent;
using System.Diagnostics;

namespace Circles.Index.Common;

public static class MeteredCallers
{
    public static readonly ConcurrentDictionary<string, MeteredCaller> Instances = new();
    private static readonly Timer Timer;

    static MeteredCallers()
    {
        Timer = new Timer(_ =>
        {
            // Format all values as table and print to the console
            var columns = new[] { "Name", "Total Calls", "Total Time (ms)", "Avg. Time (ms)", "Avg. Call Interval (ms)" };
            List<object[]> rows = new();

            foreach (MeteredCaller caller in Instances.Values.Reverse())
            {
                rows.Add([
                    caller.Name,
                    caller.TotalCalls,
                    caller.TotalTime,
                    caller.TotalTime / caller.TotalCalls,
                    caller.AverageCallInterval
                ]);
            }

            PrintTable(columns, rows);
        }, null, 10000, 10000);
    }

    static void PrintTable(string[] headers, List<object[]> rows)
    {
        int[] columnWidths = new int[headers.Length];

        for (int i = 0; i < headers.Length; i++)
        {
            columnWidths[i] = headers[i].Length;
            foreach (var row in rows)
            {
                if (row[i].ToString().Length > columnWidths[i])
                    columnWidths[i] = row[i].ToString().Length;
            }
        }

        string horizontalLine = "+" + string.Join("+", columnWidths.Select(w => new string('-', w + 2))) + "+";
        string headerRow = "|" + string.Join("|",
            headers.Select((header, index) => " " + header.PadRight(columnWidths[index]) + " ")) + "|";

        Console.WriteLine(horizontalLine);
        Console.WriteLine(headerRow);
        Console.WriteLine(horizontalLine);

        foreach (var row in rows)
        {
            string bodyRow = "|" + string.Join("|",
                row.Select((cell, index) => " " + cell.ToString().PadRight(columnWidths[index]) + " ")) + "|";
            Console.WriteLine(bodyRow);
        }

        Console.WriteLine(horizontalLine);
    }
}

public abstract class MeteredCaller
{
    public string Name { get; protected init; }

    protected long _totalCalls;
    public long TotalCalls => _totalCalls;

    protected double _totalTime;
    public double TotalTime => _totalTime;

    public DateTime FirstCallTime => _firstCallTime;
    protected DateTime _firstCallTime;

    public DateTime LastCallTime => _lastCallTime;
    protected DateTime _lastCallTime;

    public double AverageCallInterval =>
        _totalCalls > 1 ? (_lastCallTime - _firstCallTime).TotalMilliseconds / (_totalCalls - 1) : 0;
}

public class MeteredCaller<T, TResult> : MeteredCaller
{
    private readonly Func<T, TResult> _func;

    public MeteredCaller(string name, Func<T, TResult> func)
    {
        Name = name;
        _func = func;

        if (MeteredCallers.Instances.TryGetValue(name, out var existing))
        {
            _totalCalls = existing.TotalCalls;
            _totalTime = existing.TotalTime;
            _firstCallTime = existing.FirstCallTime;
            _lastCallTime = existing.LastCallTime;
        }

        MeteredCallers.Instances.AddOrUpdate(name, _ => this, (_, _) => this);
    }

    public override string ToString()
    {
        return $"Average call duration for '{Name}' is {TotalTime / TotalCalls} ms";
    }

    public TResult Call(T arg)
    {
        var sw = Stopwatch.StartNew();
        var result = _func(arg);
        sw.Stop();

        var now = DateTime.UtcNow;
        if (_totalCalls == 0)
            _firstCallTime = now;
        _lastCallTime = now;

        Interlocked.Increment(ref _totalCalls);
        _totalTime += sw.Elapsed.TotalMilliseconds;

        return result;
    }
}