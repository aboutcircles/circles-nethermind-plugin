namespace Circles.Common;

public interface IIndexEvent
{
    long BlockNumber { get; }
    long Timestamp { get; }
    int TransactionIndex { get; }
    int LogIndex { get; }
    string TransactionHash { get; }
    string Emitter { get; }
}
