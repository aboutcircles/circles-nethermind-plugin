using System.Collections.Concurrent;

namespace Circles.Common;

public class InsertBuffer<T>
{
    private ConcurrentQueue<T> _currentSegment = new();

    public int Length => _currentSegment.Count;

    public void Add(T item) => _currentSegment.Enqueue(item);

    public void AddRange(IReadOnlyList<T> items)
    {
        for (int i = 0; i < items.Count; i++)
            _currentSegment.Enqueue(items[i]);
    }

    public ConcurrentQueue<T> TakeSnapshot() => Interlocked.Exchange(ref _currentSegment, new ConcurrentQueue<T>());
}
