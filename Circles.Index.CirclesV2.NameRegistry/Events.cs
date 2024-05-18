using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.NameRegistry;

public record RegisterShortName(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Avatar,
    UInt256 ShortName,
    UInt256 Nonce) : IIndexEvent;

public record UpdateMetadataDigest(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Avatar,
    byte[] MetadataDigest) : IIndexEvent;

public record CidV0(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Avatar,
    byte[] CidV0Digest) : IIndexEvent;