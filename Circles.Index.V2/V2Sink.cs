using Circles.Index.Common;

namespace Circles.Index.V2;

public class V2Sink : IEventSink
{
    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public Task AddEvent(IIndexEvent indexEvent)
    {
        throw new NotImplementedException();
    }

    public Task Flush()
    {
        throw new NotImplementedException();
    }
}