namespace Circles.Index.Common;

public interface IEventSink : IAsyncDisposable
{
    Task AddEvent(IIndexEvent indexEvent);

    Task Flush();
}