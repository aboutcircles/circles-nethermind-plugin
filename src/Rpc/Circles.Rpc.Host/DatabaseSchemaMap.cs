using System.Collections.Generic;
using System.Linq;

namespace Circles.Rpc.Host;

/// <summary>
/// Provides a comprehensive mapping of all database tables and their address/filterable columns.
/// This is used by GetEvents and other RPC methods to dynamically query the database schema.
/// </summary>
public static class DatabaseSchemaMap
{
    /// <summary>
    /// Maps table names to their address-containing columns.
    /// Key: Table name (e.g., "CrcV1_Signup")
    /// Value: Array of column names that contain addresses
    /// </summary>
    public static readonly Dictionary<string, string[]> TableAddressColumns = new()
    {
        // ===== CrcV1 Tables =====
        { "CrcV1_HubTransfer", new[] { "from", "to" } },
        { "CrcV1_Signup", new[] { "user", "token" } },
        { "CrcV1_OrganizationSignup", new[] { "organization" } },
        { "CrcV1_Trust", new[] { "user", "canSendTo" } },
        { "CrcV1_Transfer", new[] { "from", "to", "tokenAddress" } },
        { "CrcV1_TransferSummary", new[] { "from", "to" } },
        { "CrcV1_UpdateMetadataDigest", new[] { "avatar" } },

        // ===== CrcV2 Core Tables =====
        { "CrcV2_PersonalMint", new[] { "human" } },
        { "CrcV2_RegisterGroup", new[] { "group", "mint", "treasury" } },
        { "CrcV2_RegisterHuman", new[] { "avatar", "inviter" } },
        { "CrcV2_RegisterOrganization", new[] { "organization" } },
        { "CrcV2_Stopped", new[] { "avatar" } },
        { "CrcV2_Trust", new[] { "truster", "trustee" } },
        { "CrcV2_DiscountCost", new[] { "account" } },
        { "CrcV2_TransferSingle", new[] { "operator", "from", "to", "tokenAddress" } },
        { "CrcV2_ApprovalForAll", new[] { "account", "operator" } },
        { "CrcV2_TransferBatch", new[] { "operator", "from", "to", "tokenAddress" } },
        { "CrcV2_Erc20WrapperTransfer", new[] { "from", "to", "tokenAddress" } },
        { "CrcV2_ERC20WrapperDeployed", new[] { "avatar", "erc20Wrapper" } },
        { "CrcV2_DepositInflationary", new[] { "account" } },
        { "CrcV2_WithdrawInflationary", new[] { "account" } },
        { "CrcV2_DepositDemurraged", new[] { "account" } },
        { "CrcV2_WithdrawDemurraged", new[] { "account" } },
        { "CrcV2_StreamCompleted", new[] { "operator", "from", "to", "tokenAddress" } },
        { "CrcV2_GroupMint", new[] { "sender", "receiver", "group" } },
        { "CrcV2_FlowEdgesScopeSingleStarted", new string[] { } },
        { "CrcV2_FlowEdgesScopeLastEnded", new string[] { } },
        { "CrcV2_TransferSummary", new[] { "from", "to" } },

        // ===== CrcV2 Name Registry =====
        { "CrcV2_RegisterShortName", new[] { "avatar" } },
        { "CrcV2_UpdateMetadataDigest", new[] { "avatar" } },
        { "CrcV2_CidV0", new[] { "avatar" } },

        // ===== CrcV2 Group Deployers =====
        { "CrcV2_CMGroupCreated", new[] { "emitter", "proxy", "owner", "mintHandler", "redemptionHandler", "liquidityProvider" } },
        { "CrcV2_BaseGroupCreated", new[] { "emitter", "group", "owner", "mintHandler", "treasury" } },
        { "CrcV2_BaseGroupOwnerUpdated", new[] { "emitter", "owner" } },
        { "CrcV2_BaseGroupServiceUpdated", new[] { "emitter", "newService" } },
        { "CrcV2_BaseGroupFeeCollectionUpdated", new[] { "emitter", "feeCollection" } },

        // ===== CrcV2 Affiliate Group Registry =====
        { "CrcV2_AffiliateGroupChanged", new[] { "emitter", "human", "oldGroup", "newGroup" } },
        { "CrcV2_NotificationFailed", new[] { "emitter", "group", "human" } },
        { "CrcV2_NotificationSuccessful", new[] { "emitter", "group", "human" } },

        // ===== CrcV2 Invitation Escrow =====
        { "CrcV2_InvitationEscrow_InvitationEscrowed", new[] { "emitter", "inviter", "invitee" } },
        { "CrcV2_InvitationEscrow_InvitationRedeemed", new[] { "emitter", "inviter", "invitee" } },
        { "CrcV2_InvitationEscrow_InvitationRefunded", new[] { "emitter", "inviter", "invitee" } },
        { "CrcV2_InvitationEscrow_InvitationRevoked", new[] { "emitter", "inviter", "invitee" } },

        // ===== CrcV2 ERC20 Lift (Mint/Redeem) =====
        { "CrcV2_CreateVault", new[] { "group", "vault" } },
        { "CrcV2_GroupMintSingle", new[] { "group" } },
        { "CrcV2_GroupMintBatch", new[] { "group" } },
        { "CrcV2_GroupRedeem", new[] { "group" } },
        { "CrcV2_GroupRedeemCollateralReturn", new[] { "group", "to" } },
        { "CrcV2_GroupRedeemCollateralBurn", new[] { "group" } },

        // ===== CrcV2 Standard Treasury =====
        { "CrcV2_CollateralLockedSingle", new[] { "group" } },
        { "CrcV2_CollateralLockedBatch", new[] { "group" } },

        // ===== CrcV2 LBP (Liquidity Bootstrapping Pool) =====
        { "CrcV2_CirclesBackingDeployed", new[] { "emitter", "backer", "circlesBackingInstance" } },
        { "CrcV2_LBPDeployed", new[] { "emitter", "circlesBackingInstance", "lbp" } },
        { "CrcV2_CirclesBackingInitiated", new[] { "emitter", "backer", "circlesBackingInstance", "backingAsset", "personalCirclesAddress" } },
        { "CrcV2_CirclesBackingCompleted", new[] { "emitter", "backer", "circlesBackingInstance", "lbp" } },
        { "CrcV2_Released", new[] { "emitter", "backer", "circlesBackingInstance", "lbp" } },

        // ===== CrcV2 Token Offers =====
        { "CrcV2_TokenOffers_AccountWeightProviderCreated", new[] { "emitter", "provider", "admin" } },
        { "CrcV2_TokenOffers_ERC20TokenOfferCreated", new[] { "emitter", "tokenOffer", "offerOwner", "accountWeightProvider", "offerToken" } },
        { "CrcV2_TokenOffers_ERC20TokenOfferCycleCreated", new[] { "emitter", "offerCycle", "cycleOwner", "offerToken" } },
        { "CrcV2_TokenOffers_CycleConfiguration", new[] { "emitter", "admin", "accountWeightProvider", "offerToken" } },
        { "CrcV2_TokenOffers_NextOfferCreated", new[] { "emitter", "nextOffer" } },
        { "CrcV2_TokenOffers_NextOfferTokensDeposited", new[] { "emitter", "nextOffer" } },
        { "CrcV2_TokenOffers_OfferTrustSynced", new[] { "emitter", "offer" } },
        { "CrcV2_TokenOffers_OfferClaimedFromCycle", new[] { "emitter", "offer", "account" } },
        { "CrcV2_TokenOffers_UnclaimedTokensWithdrawn", new[] { "emitter", "offer" } },
        { "CrcV2_TokenOffers_OfferClaimed", new[] { "emitter", "account" } },
        { "CrcV2_TokenOffers_OfferTokensDeposited", new[] { "emitter" } },
        { "CrcV2_TokenOffers_AccountWeightSet", new[] { "emitter", "offer", "account" } },
        { "CrcV2_TokenOffers_WeightsFinalized", new[] { "emitter", "offer" } },

        // ===== CrcV2 OIC (Open Incentive Circles) =====
        { "CrcV2_OIC_OpenMiddlewareTransfer", new[] { "onBehalf", "sender", "recipient" } }
    };

    /// <summary>
    /// Maps table names to all their columns.
    /// Key: Table name
    /// Value: Dictionary of column names to their types
    /// </summary>
    public static readonly Dictionary<string, Dictionary<string, string>> TableColumns = new()
    {
        // Standard columns present in all event tables
        // Each table inherits: blockNumber, timestamp, transactionIndex, logIndex, transactionHash

        // ===== CrcV1 Tables =====
        {
            "CrcV1_HubTransfer", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "from", "Address" },
                { "to", "Address" },
                { "amount", "BigInt" }
            }
        },
        {
            "CrcV1_Signup", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "user", "Address" },
                { "token", "Address" }
            }
        },
        {
            "CrcV1_OrganizationSignup", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "organization", "Address" }
            }
        },
        {
            "CrcV1_Trust", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "canSendTo", "Address" },
                { "user", "Address" },
                { "limit", "Int" }
            }
        },
        {
            "CrcV1_Transfer", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "tokenAddress", "Address" },
                { "from", "Address" },
                { "to", "Address" },
                { "amount", "BigInt" }
            }
        },
        {
            "CrcV1_TransferSummary", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "from", "Address" },
                { "to", "Address" },
                { "amount", "BigInt" },
                { "events", "Json" }
            }
        },
        {
            "CrcV1_UpdateMetadataDigest", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "avatar", "Address" },
                { "metadataDigest", "Bytes" }
            }
        },

        // ===== CrcV2 Core Tables =====
        {
            "CrcV2_PersonalMint", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "human", "Address" },
                { "amount", "BigInt" },
                { "startPeriod", "BigInt" },
                { "endPeriod", "BigInt" }
            }
        },
        {
            "CrcV2_RegisterGroup", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "group", "Address" },
                { "mint", "Address" },
                { "treasury", "Address" },
                { "name", "String" },
                { "symbol", "String" }
            }
        },
        {
            "CrcV2_RegisterHuman", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "avatar", "Address" },
                { "inviter", "Address" }
            }
        },
        {
            "CrcV2_RegisterOrganization", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "organization", "Address" },
                { "name", "String" }
            }
        },
        {
            "CrcV2_Stopped", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "avatar", "Address" }
            }
        },
        {
            "CrcV2_Trust", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "truster", "Address" },
                { "trustee", "Address" },
                { "expiryTime", "BigInt" }
            }
        },
        {
            "CrcV2_DiscountCost", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "account", "Address" },
                { "id", "BigInt" },
                { "discountCost", "BigInt" }
            }
        },
        {
            "CrcV2_TransferSingle", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "operator", "Address" },
                { "from", "Address" },
                { "to", "Address" },
                { "id", "BigInt" },
                { "value", "BigInt" },
                { "tokenAddress", "Address" }
            }
        },
        {
            "CrcV2_ApprovalForAll", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "account", "Address" },
                { "operator", "Address" },
                { "approved", "Boolean" }
            }
        },
        {
            "CrcV2_TransferBatch", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "batchIndex", "Int" },
                { "transactionHash", "String" },
                { "operator", "Address" },
                { "from", "Address" },
                { "to", "Address" },
                { "id", "BigInt" },
                { "value", "BigInt" },
                { "tokenAddress", "Address" }
            }
        },
        {
            "CrcV2_Erc20WrapperTransfer", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "tokenAddress", "Address" },
                { "from", "Address" },
                { "to", "Address" },
                { "amount", "BigInt" }
            }
        },
        {
            "CrcV2_ERC20WrapperDeployed", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "avatar", "Address" },
                { "erc20Wrapper", "Address" },
                { "circlesType", "Int" }
            }
        },
        {
            "CrcV2_TransferSummary", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "from", "Address" },
                { "to", "Address" },
                { "amount", "BigInt" },
                { "events", "Json" }
            }
        },
        {
            "CrcV2_RegisterShortName", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "avatar", "Address" },
                { "shortName", "BigInt" },
                { "nonce", "BigInt" }
            }
        },
        {
            "CrcV2_UpdateMetadataDigest", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "avatar", "Address" },
                { "metadataDigest", "Bytes" }
            }
        },
        {
            "CrcV2_CidV0", new Dictionary<string, string>
            {
                { "blockNumber", "Int" },
                { "timestamp", "Int" },
                { "transactionIndex", "Int" },
                { "logIndex", "Int" },
                { "transactionHash", "String" },
                { "avatar", "Address" },
                { "cidV0Digest", "Bytes" }
            }
        }
    };

    /// <summary>
    /// Gets all table names that are queryable.
    /// </summary>
    public static IEnumerable<string> AllTables => TableAddressColumns.Keys;

    /// <summary>
    /// Gets address columns for a specific table.
    /// </summary>
    public static string[] GetAddressColumns(string tableName)
    {
        return TableAddressColumns.TryGetValue(tableName, out var columns) ? columns : Array.Empty<string>();
    }

    /// <summary>
    /// Checks if a table exists in the schema.
    /// </summary>
    public static bool TableExists(string tableName)
    {
        return TableAddressColumns.ContainsKey(tableName);
    }

    /// <summary>
    /// Gets all columns for a specific table.
    /// </summary>
    public static Dictionary<string, string>? GetTableColumns(string tableName)
    {
        return TableColumns.TryGetValue(tableName, out var columns) ? columns : null;
    }
}
