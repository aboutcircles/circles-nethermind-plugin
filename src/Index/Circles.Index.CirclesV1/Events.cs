using Nethermind.Int256;

namespace Circles.Index.CirclesV1;

public record Signup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string User,
    string Token) : IIndexedEventV1;

public record OrganizationSignup(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Organization) : IIndexedEventV1;

public record Trust(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string User,
    string CanSendTo,
    int Limit) : IIndexedEventV1;

public record HubTransfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string From,
    string To,
    UInt256 Amount) : IIndexedEventV1;

public record Transfer(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string TokenAddress,
    string From,
    string To,
    UInt256 Value) : IIndexedEventV1;

public record TransferSummary(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string From,
    string To,
    UInt256 Amount,
    string Events) : IIndexedEventV1;
