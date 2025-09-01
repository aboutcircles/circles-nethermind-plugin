using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core.Crypto;

namespace Circles.Index.CirclesV2.TokenOffers;

public class DatabaseSchema : BaseDatabaseSchema
{
    // Factory events
    public static readonly EventSchema AccountWeightProviderCreated = new(
        "CrcV2_TokenOffers",
        "AccountWeightProviderCreated",
        Keccak.Compute("AccountWeightProviderCreated(address,address)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("provider", ValueTypes.Address, true),
            new("admin", ValueTypes.Address, true)
        ]);

    public static readonly EventSchema ERC20TokenOfferCreated = new(
        "CrcV2_TokenOffers",
        "ERC20TokenOfferCreated",
        Keccak.Compute(
            "ERC20TokenOfferCreated(address,address,address,address,uint256,uint256,uint256,uint256,string,address[])"
        ).BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("tokenOffer", ValueTypes.Address, true),
            new("offerOwner", ValueTypes.Address, true),
            new("accountWeightProvider", ValueTypes.Address, true),
            new("offerToken", ValueTypes.Address, true),
            new("tokenPriceInCRC", ValueTypes.BigInt, true),
            new("offerLimitInCRC", ValueTypes.BigInt, true),
            new("offerStart", ValueTypes.BigInt, true),
            new("offerEnd", ValueTypes.BigInt, true),
            new("orgName", ValueTypes.String, true),
            new("acceptedCRC", ValueTypes.AddressArray, true)
        ]);

    public static readonly EventSchema ERC20TokenOfferCycleCreated = new(
        "CrcV2_TokenOffers",
        "ERC20TokenOfferCycleCreated",
        Keccak.Compute(
            "ERC20TokenOfferCycleCreated(address,address,address,uint256,uint256,string,string)"
        ).BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("offerCycle", ValueTypes.Address, true),
            new("cycleOwner", ValueTypes.Address, true),
            new("offerToken", ValueTypes.Address, true),
            new("offersStart", ValueTypes.BigInt, true),
            new("offerDuration", ValueTypes.BigInt, true),
            new("offerName", ValueTypes.String, true),
            new("cycleName", ValueTypes.String, true)
        ]);

    // Cycle events
    public static readonly EventSchema CycleConfiguration = new(
        "CrcV2_TokenOffers",
        "CycleConfiguration",
        Keccak.Compute(
            "CycleConfiguration(address,address,address,uint256,uint256,bool)"
        ).BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("admin", ValueTypes.Address, true),
            new("accountWeightProvider", ValueTypes.Address, true),
            new("offerToken", ValueTypes.Address, true),
            new("offersStart", ValueTypes.BigInt, true),
            new("offerDuration", ValueTypes.BigInt, true),
            new("softLockEnabled", ValueTypes.Boolean, true)
        ]);

    public static readonly EventSchema NextOfferCreated = new(
        "CrcV2_TokenOffers",
        "NextOfferCreated",
        Keccak.Compute(
            "NextOfferCreated(address,uint256,uint256,address[])"
        ).BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("nextOffer", ValueTypes.Address, true),
            new("tokenPriceInCRC", ValueTypes.BigInt, true),
            new("offerLimitInCRC", ValueTypes.BigInt, true),
            new("acceptedCRC", ValueTypes.AddressArray, true)
        ]);

    public static readonly EventSchema NextOfferTokensDeposited = new(
        "CrcV2_TokenOffers",
        "NextOfferTokensDeposited",
        Keccak.Compute("NextOfferTokensDeposited(address,uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("nextOffer", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, true)
        ]);

    public static readonly EventSchema OfferTrustSynced = new(
        "CrcV2_TokenOffers",
        "OfferTrustSynced",
        Keccak.Compute("OfferTrustSynced(uint256,address)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("offerId", ValueTypes.BigInt, true),
            new("offer", ValueTypes.Address, true)
        ]);

    public static readonly EventSchema OfferClaimedFromCycle = new(
        "CrcV2_TokenOffers",
        "OfferClaimedFromCycle",
        Keccak.Compute("OfferClaimed(address,address,uint256,uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("offer", ValueTypes.Address, true),
            new("account", ValueTypes.Address, true),
            new("received", ValueTypes.BigInt, true),
            new("spent", ValueTypes.BigInt, true)
        ]);

    public static readonly EventSchema UnclaimedTokensWithdrawn = new(
        "CrcV2_TokenOffers",
        "UnclaimedTokensWithdrawn",
        Keccak.Compute("UnclaimedTokensWithdrawn(address,uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("offer", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, true)
        ]);

    // Offer events
    public static readonly EventSchema OfferClaimed = new(
        "CrcV2_TokenOffers",
        "OfferClaimed",
        Keccak.Compute("OfferClaimed(address,uint256,uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("account", ValueTypes.Address, true),
            new("spent", ValueTypes.BigInt, true),
            new("received", ValueTypes.BigInt, true)
        ]);

    public static readonly EventSchema OfferTokensDeposited = new(
        "CrcV2_TokenOffers",
        "OfferTokensDeposited",
        Keccak.Compute("OfferTokensDeposited(uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("amount", ValueTypes.BigInt, true)
        ]);

    // Provider events
    public static readonly EventSchema AccountWeightSet = new(
        "CrcV2_TokenOffers",
        "AccountWeightSet",
        Keccak.Compute("AccountWeightSet(address,address,uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("offer", ValueTypes.Address, true),
            new("account", ValueTypes.Address, true),
            new("weight", ValueTypes.BigInt, true)
        ]);

    public static readonly EventSchema WeightsFinalized = new(
        "CrcV2_TokenOffers",
        "WeightsFinalized",
        Keccak.Compute("WeightsFinalized(address,uint256,uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("offer", ValueTypes.Address, true),
            new("accountsCount", ValueTypes.BigInt, true),
            new("totalWeight", ValueTypes.BigInt, true)
        ]);

    public DatabaseSchema()
    {
        AddMappings<AccountWeightProviderCreated>(
            "CrcV2_TokenOffers",
            "AccountWeightProviderCreated",
            AccountWeightProviderCreated,
            [
                ("emitter", e => e.Emitter),
                ("provider", e => e.Provider),
                ("admin", e => e.Admin)
            ]);

        AddMappings<ERC20TokenOfferCreated>(
            "CrcV2_TokenOffers",
            "ERC20TokenOfferCreated",
            ERC20TokenOfferCreated,
            [
                ("emitter", e => e.Emitter),
                ("tokenOffer", e => e.TokenOffer),
                ("offerOwner", e => e.OfferOwner),
                ("accountWeightProvider", e => e.AccountWeightProvider),
                ("offerToken", e => e.OfferToken),
                ("tokenPriceInCRC", e => e.TokenPriceInCRC),
                ("offerLimitInCRC", e => e.OfferLimitInCRC),
                ("offerStart", e => e.OfferStart),
                ("offerEnd", e => e.OfferEnd),
                ("orgName", e => e.OrgName),
                ("acceptedCRC", e => e.AcceptedCRC)
            ]);

        AddMappings<ERC20TokenOfferCycleCreated>(
            "CrcV2_TokenOffers",
            "ERC20TokenOfferCycleCreated",
            ERC20TokenOfferCycleCreated,
            [
                ("emitter", e => e.Emitter),
                ("offerCycle", e => e.OfferCycle),
                ("cycleOwner", e => e.CycleOwner),
                ("offerToken", e => e.OfferToken),
                ("offersStart", e => e.OffersStart),
                ("offerDuration", e => e.OfferDuration),
                ("offerName", e => e.OfferName),
                ("cycleName", e => e.CycleName)
            ]);

        AddMappings<CycleConfiguration>(
            "CrcV2_TokenOffers",
            "CycleConfiguration",
            CycleConfiguration,
            [
                ("emitter", e => e.Emitter),
                ("admin", e => e.Admin),
                ("accountWeightProvider", e => e.AccountWeightProvider),
                ("offerToken", e => e.OfferToken),
                ("offersStart", e => e.OffersStart),
                ("offerDuration", e => e.OfferDuration),
                ("softLockEnabled", e => e.SoftLockEnabled)
            ]);

        AddMappings<NextOfferCreated>(
            "CrcV2_TokenOffers",
            "NextOfferCreated",
            NextOfferCreated,
            [
                ("emitter", e => e.Emitter),
                ("nextOffer", e => e.NextOffer),
                ("tokenPriceInCRC", e => e.TokenPriceInCRC),
                ("offerLimitInCRC", e => e.OfferLimitInCRC),
                ("acceptedCRC", e => e.AcceptedCRC)
            ]);

        AddMappings<NextOfferTokensDeposited>(
            "CrcV2_TokenOffers",
            "NextOfferTokensDeposited",
            NextOfferTokensDeposited,
            [
                ("emitter", e => e.Emitter),
                ("nextOffer", e => e.NextOffer),
                ("amount", e => e.Amount)
            ]);

        AddMappings<OfferTrustSynced>(
            "CrcV2_TokenOffers",
            "OfferTrustSynced",
            OfferTrustSynced,
            [
                ("emitter", e => e.Emitter),
                ("offerId", e => e.OfferId),
                ("offer", e => e.Offer)
            ]);

        AddMappings<OfferClaimedFromCycle>(
            "CrcV2_TokenOffers",
            "OfferClaimedFromCycle",
            OfferClaimedFromCycle,
            [
                ("emitter", e => e.Emitter),
                ("offer", e => e.Offer),
                ("account", e => e.Account),
                ("received", e => e.Received),
                ("spent", e => e.Spent)
            ]);

        AddMappings<UnclaimedTokensWithdrawn>(
            "CrcV2_TokenOffers",
            "UnclaimedTokensWithdrawn",
            UnclaimedTokensWithdrawn,
            [
                ("emitter", e => e.Emitter),
                ("offer", e => e.Offer),
                ("amount", e => e.Amount)
            ]);

        AddMappings<OfferClaimed>(
            "CrcV2_TokenOffers",
            "OfferClaimed",
            OfferClaimed,
            [
                ("emitter", e => e.Emitter),
                ("account", e => e.Account),
                ("spent", e => e.Spent),
                ("received", e => e.Received)
            ]);

        AddMappings<OfferTokensDeposited>(
            "CrcV2_TokenOffers",
            "OfferTokensDeposited",
            OfferTokensDeposited,
            [
                ("emitter", e => e.Emitter),
                ("amount", e => e.Amount)
            ]);

        AddMappings<AccountWeightSet>(
            "CrcV2_TokenOffers",
            "AccountWeightSet",
            AccountWeightSet,
            [
                ("emitter", e => e.Emitter),
                ("offer", e => e.Offer),
                ("account", e => e.Account),
                ("weight", e => e.Weight)
            ]);

        AddMappings<WeightsFinalized>(
            "CrcV2_TokenOffers",
            "WeightsFinalized",
            WeightsFinalized,
            [
                ("emitter", e => e.Emitter),
                ("offer", e => e.Offer),
                ("accountsCount", e => e.AccountsCount),
                ("totalWeight", e => e.TotalWeight)
            ]);
    }
}