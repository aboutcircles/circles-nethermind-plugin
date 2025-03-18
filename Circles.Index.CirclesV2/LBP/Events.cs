using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.LBP;

public record CirclesBackingDeployed(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Backer,
    string CirclesBackingInstance) : IIndexedEventV2;

public record LbpDeployed(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string CirclesBackingInstance,
    string Lbp) : IIndexedEventV2;

public record CirclesBackingInitiated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Backer,
    string CirclesBackingInstance,
    string BackingAsset,
    string PersonalCirclesAddress) : IIndexedEventV2;

public record CirclesBackingCompleted(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Backer,
    string CirclesBackingInstance,
    string Lbp) : IIndexedEventV2;

public record Released(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    int BatchIndex,
    string TransactionHash,
    string Emitter,
    string Backer,
    string CirclesBackingInstance,
    string Lbp) : IIndexedEventV2;