using System.Collections.Concurrent;

namespace Circles.Index.Common;

public class InsertBuffer<T>
{
    private ConcurrentQueue<T> _currentSegment = new();

    public int Length => _currentSegment.Count;

    public void Add(T item) => _currentSegment.Enqueue(item);

    public ConcurrentQueue<T> TakeSnapshot() => Interlocked.Exchange(ref _currentSegment, new ConcurrentQueue<T>());
}