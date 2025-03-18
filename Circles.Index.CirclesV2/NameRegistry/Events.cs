using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.NameRegistry;

public record RegisterShortName(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Avatar,
    UInt256 ShortName,
    UInt256 Nonce) : IIndexedEventV2;

public record UpdateMetadataDigest(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Avatar,
    byte[] MetadataDigest) : IIndexedEventV2;

public record CidV0(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Avatar,
    byte[] CidV0Digest) : IIndexedEventV2;