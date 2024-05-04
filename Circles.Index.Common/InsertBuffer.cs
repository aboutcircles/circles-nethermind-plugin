using System.Collections.Concurrent;

namespace Circles.Index.Common;

public interface ISnapshot<T>
{
    ConcurrentQueue<T> TakeSnapshot();
}

public class InsertBuffer<T> : ISnapshot<T>
{
    private ConcurrentQueue<T> _currentSegment = new();

    public int Length => _currentSegment.Count;

    public void Add(T item) => _currentSegment.Enqueue(item);

    public ConcurrentQueue<T> TakeSnapshot() => Interlocked.Exchange(ref _currentSegment, new ConcurrentQueue<T>());
}