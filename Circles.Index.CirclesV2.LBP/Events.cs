using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.LBP;

public record CirclesBackingDeployed(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Backer,
    string CirclesBackingInstance) : IIndexEvent;

public record LbpDeployed(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string CirclesBackingInstance,
    string Lbp) : IIndexEvent;

public record CirclesBackingInitiated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Backer,
    string CirclesBackingInstance,
    string BackingAsset,
    string PersonalCirclesAddress) : IIndexEvent;

public record CirclesBackingCompleted(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Backer,
    string CirclesBackingInstance,
    string Lbp) : IIndexEvent;

public record Released(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Backer,
    string CirclesBackingInstance,
    string Lbp) : IIndexEvent;