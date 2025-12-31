using Circles.Common;

namespace Circles.Index.CirclesV1.NameRegistry;

public record UpdateMetadataDigest(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Avatar,
    byte[] MetadataDigest) : IIndexEvent;