using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV1;

public record Signup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string User,
    string Token) : IIndexEvent;

public record OrganizationSignup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Organization) : IIndexEvent;

public record Trust(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string User,
    string CanSendTo,
    int Limit) : IIndexEvent;

public record HubTransfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string From,
    string To,
    UInt256 Amount) : IIndexEvent;

public record Transfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string TokenAddress,
    string From,
    string To,
    UInt256 Value) : IIndexEvent;