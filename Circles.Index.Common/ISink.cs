namespace Circles.Index.Common;


public interface INewSink
{
    void AddBlock(long blockNumber, long timestamp, string blockHash);

    void AddEvent(IIndexEvent indexIndexEvent);

    Task Flush();
}