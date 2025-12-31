using Circles.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.TokenOffers;

// Factory
public record AccountWeightProviderCreated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Provider,
    string Admin
) : IIndexEvent;

public record ERC20TokenOfferCreated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string TokenOffer,
    string OfferOwner,
    string AccountWeightProvider,
    string OfferToken,
    UInt256 TokenPriceInCRC,
    UInt256 OfferLimitInCRC,
    UInt256 OfferStart,
    UInt256 OfferEnd,
    string OrgName,
    string[] AcceptedCRC
) : IIndexEvent;

public record ERC20TokenOfferCycleCreated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string OfferCycle,
    string CycleOwner,
    string OfferToken,
    UInt256 OffersStart,
    UInt256 OfferDuration,
    string OfferName,
    string CycleName
) : IIndexEvent;

// Cycle
public record CycleConfiguration(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Admin,
    string AccountWeightProvider,
    string OfferToken,
    UInt256 OffersStart,
    UInt256 OfferDuration,
    bool SoftLockEnabled
) : IIndexEvent;

public record NextOfferCreated(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string NextOffer,
    UInt256 TokenPriceInCRC,
    UInt256 OfferLimitInCRC,
    string[] AcceptedCRC
) : IIndexEvent;

public record NextOfferTokensDeposited(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string NextOffer,
    UInt256 Amount
) : IIndexEvent;

public record OfferTrustSynced(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    UInt256 OfferId,
    string Offer
) : IIndexEvent;

public record OfferClaimedFromCycle(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Offer,
    string Account,
    UInt256 Received,
    UInt256 Spent
) : IIndexEvent;

public record UnclaimedTokensWithdrawn(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Offer,
    UInt256 Amount
) : IIndexEvent;

// Offer
public record OfferClaimed(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Account,
    UInt256 Spent,
    UInt256 Received
) : IIndexEvent;

public record OfferTokensDeposited(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    UInt256 Amount
) : IIndexEvent;

// Provider
public record AccountWeightSet(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Offer,
    string Account,
    UInt256 Weight
) : IIndexEvent;

public record WeightsFinalized(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Emitter,
    string Offer,
    UInt256 AccountsCount,
    UInt256 TotalWeight
) : IIndexEvent;
