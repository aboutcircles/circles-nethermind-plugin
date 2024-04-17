using System.Collections.Concurrent;

namespace Circles.Index.Data;

public interface ISnapshot<out T>
{
    IEnumerable<T> TakeSnapshot();
}

public class InsertBuffer<T> : ISnapshot<T>
{
    private ConcurrentQueue<T> _currentSegment = new();

    public void Add(T item) => _currentSegment.Enqueue(item);

    public IEnumerable<T> TakeSnapshot() => Interlocked.Exchange(ref _currentSegment, new ConcurrentQueue<T>());
}