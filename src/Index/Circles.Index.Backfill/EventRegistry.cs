using System.Numerics;
using System.Text;
using Nethereum.Util;

namespace Circles.Index.Backfill;

/// <summary>
/// Registry of all backfillable event schemas.
/// To add a new event:
/// 1. Define the event schema using RegisterEvent() or RegisterEventManual()
/// 2. Map the contract address from Settings.cs
/// 3. Rebuild: docker build -t circles-backfill:local -f docker/backfill.Dockerfile .
/// </summary>
public static class EventRegistry
{
    /// <summary>
    /// All registered events that can be backfilled.
    /// Key: Table name (e.g., "CrcV2_PaymentGateway_GatewayCreated")
    /// </summary>
    public static readonly Dictionary<string, EventDefinition> Events = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Contract addresses to filter by for each event.
    /// Key: Topic hash, Value: Set of contract addresses (lowercase)
    /// If not specified, matches any address.
    /// </summary>
    public static readonly Dictionary<string, HashSet<string>> ContractFilters = new(StringComparer.OrdinalIgnoreCase);

    static EventRegistry()
    {
        // ============================================================
        // Contract addresses from Settings.cs (authoritative source)
        // ============================================================
        var v1Hub = "0x29b9a7fbb8995b2423a71cc17cf9810798f6c543";
        var v1NameRegistry = "0x1ead7f904f6ffc619c58b85e04f890b394e08172";
        var v2Hub = "0xc12c1e50abb450d6205ea2c3fa861b3b834d13e8";
        var v2NameRegistry = "0xa27566fd89162cc3d40cb59c87aaaa49b85f3474";
        var v2Erc20Lift = "0x5f99a795dd2743c36d63511f0d4bc667e6d3cdb5";
        var v2StandardTreasury = "0x08f90ab73a515308f03a718257ff9887ed330c6e";
        var paymentGatewayFactory = "0x186725d8fe10a573dc73144f7a317fcae5314f19";
        var tokenOfferFactory = "0x43c8e7cb2fea3a55b52867bb521ebf8cb072feca";
        var affiliateGroupRegistry = "0xca8222e780d046707083f51377b5fd85e2866014";
        var multiAffiliateGroupRegistry = "0x4a25a7cf216351963f1637ad965d77b3ae277ef3";
        var oicContract = "0x6fff09332ae273ba7095a2a949a7f4b89eb37c52";
        var baseGroupDeployer = "0xd0b5bd9962197beac4cba24244ec3587f19bd06d";
        var invitationModule = "0x00738aca013b7b2e6cfe1690f0021c3182fa40b5";
        var referralsModule = "0x12105a9b291af2abb0591001155a75949b062ce5";
        var invitationFarm = "0xd28b7c4f148b1f1e190840a1f7a796c5525d8902";

        // Multiple addresses
        var lbpFactories = new[] {
            "0xd10d53ec77ce25829b7d270d736403218af22ad9",
            "0x4bb5a425a68ed73cf0b26ce79f5eead9103c30fc",
            "0xeced91232c609a42f6016860e8223b8aecaa7bd0"
        };
        var cmGroupDeployers = new[] {
            "0x55785b41703728f1f1f05e77e22b13c3fcc9ce65",
            "0xfeca40eb02fb1f4f5f795fc7a03c1a27819b1ded"
        };
        var safeProxyFactories = new[] {
            "0x8b4404de0caece4b966a9959f134f0efda636156",
            "0x12302fe9c02ff50939baaaaf415fc226c078613c",
            "0x76e2cfc1f5fa8f6a5b3fc4c8f4788f0116861f9b",
            "0xa6b71e26c5e0845f74c812102ca7114b6a896ab2",
            "0x4e1dcf7ad4e460cfd30791ccc4f9c8a4f820ec67"
        };
        var invitationEscrowContracts = new[] {
            "0x0956c08ad2dcc6f4a1e0cc5ffa3a08d2a6d85f29",
            "0x8f8b74fa13eaaff4176d061a0f98ad5c8e19c903"
        };

        // ============================================================
        // CrcV1 Hub Events
        // ============================================================
        RegisterEvent("CrcV1_HubTransfer",
            "event HubTransfer(address indexed from, address indexed to, uint256 amount)",
            v1Hub);

        RegisterEvent("CrcV1_Signup",
            "event Signup(address indexed user, address indexed token)",
            v1Hub);

        RegisterEvent("CrcV1_OrganizationSignup",
            "event OrganizationSignup(address indexed organization)",
            v1Hub);

        RegisterEvent("CrcV1_Trust",
            "event Trust(address indexed canSendTo, address indexed user, uint256 limit)",
            v1Hub);

        // CrcV1_Transfer - emitted by individual token contracts, no fixed address filter
        RegisterEventManual("CrcV1_Transfer",
            "Transfer(address,address,uint256)",
            new[]
            {
                ("from", FieldType.Address, true),
                ("to", FieldType.Address, true),
                ("amount", FieldType.BigInt, false)
            });

        // ============================================================
        // CrcV1 NameRegistry Events
        // ============================================================
        RegisterEvent("CrcV1_UpdateMetadataDigest",
            "event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest)",
            v1NameRegistry);

        // ============================================================
        // CrcV2 Hub Events
        // ============================================================
        RegisterEvent("CrcV2_PersonalMint",
            "event PersonalMint(address indexed human, uint256 amount, uint256 startPeriod, uint256 endPeriod)",
            v2Hub);

        RegisterEvent("CrcV2_RegisterGroup",
            "event RegisterGroup(address indexed group, address indexed mint, address indexed treasury, string name, string symbol)",
            v2Hub);

        RegisterEvent("CrcV2_RegisterHuman",
            "event RegisterHuman(address indexed avatar, address indexed inviter)",
            v2Hub);

        RegisterEvent("CrcV2_RegisterOrganization",
            "event RegisterOrganization(address indexed organization, string name)",
            v2Hub);

        RegisterEvent("CrcV2_Stopped",
            "event Stopped(address indexed avatar)",
            v2Hub);

        RegisterEvent("CrcV2_Trust",
            "event Trust(address indexed truster, address indexed trustee, uint256 expiryTime)",
            v2Hub);

        RegisterEvent("CrcV2_DiscountCost",
            "event DiscountCost(address indexed account, uint256 indexed id, uint256 discountCost)",
            v2Hub);

        RegisterEventManual("CrcV2_TransferSingle",
            "TransferSingle(address,address,address,uint256,uint256)",
            new[]
            {
                ("operator", FieldType.Address, true),
                ("from", FieldType.Address, true),
                ("to", FieldType.Address, true),
                ("id", FieldType.BigInt, false),
                ("value", FieldType.BigInt, false)
            },
            v2Hub);

        RegisterEvent("CrcV2_ApprovalForAll",
            "event ApprovalForAll(address indexed account, address indexed operator, bool approved)",
            v2Hub);

        RegisterEventManual("CrcV2_TransferBatch",
            "TransferBatch(address,address,address,uint256[],uint256[])",
            new[]
            {
                ("operator", FieldType.Address, true),
                ("from", FieldType.Address, true),
                ("to", FieldType.Address, true),
                ("ids", FieldType.BigIntArray, false),
                ("values", FieldType.BigIntArray, false)
            },
            v2Hub);

        RegisterEvent("CrcV2_ERC20WrapperDeployed",
            "event ERC20WrapperDeployed(address indexed avatar, address indexed erc20Wrapper, uint8 circlesType)",
            v2Hub);

        RegisterEvent("CrcV2_DepositInflationary",
            "event DepositInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)",
            v2Hub);

        RegisterEvent("CrcV2_WithdrawInflationary",
            "event WithdrawInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)",
            v2Hub);

        RegisterEvent("CrcV2_DepositDemurraged",
            "event DepositDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)",
            v2Hub);

        RegisterEvent("CrcV2_WithdrawDemurraged",
            "event WithdrawDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)",
            v2Hub);

        RegisterEventManual("CrcV2_StreamCompleted",
            "StreamCompleted(address,address,address,uint256[],uint256[])",
            new[]
            {
                ("operator", FieldType.Address, true),
                ("from", FieldType.Address, true),
                ("to", FieldType.Address, true),
                ("ids", FieldType.BigIntArray, false),
                ("amounts", FieldType.BigIntArray, false)
            },
            v2Hub);

        RegisterEventManual("CrcV2_GroupMint",
            "GroupMint(address,address,address,uint256[],uint256[])",
            new[]
            {
                ("sender", FieldType.Address, true),
                ("receiver", FieldType.Address, true),
                ("group", FieldType.Address, true),
                ("collaterals", FieldType.BigIntArray, false),
                ("amounts", FieldType.BigIntArray, false)
            },
            v2Hub);

        RegisterEvent("CrcV2_FlowEdgesScopeSingleStarted",
            "event FlowEdgesScopeSingleStarted(uint256 indexed flowEdgeId, uint16 streamId)",
            v2Hub);

        RegisterEventManual("CrcV2_FlowEdgesScopeLastEnded",
            "FlowEdgesScopeLastEnded()",
            Array.Empty<(string, FieldType, bool)>(),
            v2Hub);

        RegisterEvent("CrcV2_SetAdvancedUsageFlag",
            "event SetAdvancedUsageFlag(address indexed avatar, bytes32 flag)",
            v2Hub);

        // ============================================================
        // CrcV2 NameRegistry Events
        // ============================================================
        RegisterEventManual("CrcV2_RegisterShortName",
            "RegisterShortName(address,uint72,uint256)",
            new[]
            {
                ("avatar", FieldType.Address, true),
                ("shortName", FieldType.BigInt, false),
                ("nonce", FieldType.BigInt, false)
            },
            v2NameRegistry);

        RegisterEvent("CrcV2_UpdateMetadataDigest",
            "event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest)",
            v2NameRegistry);

        RegisterEvent("CrcV2_CidV0",
            "event CidV0(address indexed avatar, bytes32 cidV0Digest)",
            v2NameRegistry);

        // ============================================================
        // CrcV2 ERC20 Wrapper Transfer (from individual wrapper contracts)
        // ============================================================
        // No fixed address - matches ERC20 Transfer from any wrapper contract
        RegisterEventManual("CrcV2_Erc20WrapperTransfer",
            "Transfer(address,address,uint256)",
            new[]
            {
                ("from", FieldType.Address, true),
                ("to", FieldType.Address, true),
                ("amount", FieldType.BigInt, false)
            });

        // ============================================================
        // CrcV2 ERC20 Lift / Standard Treasury Events
        // ============================================================
        RegisterEvent("CrcV2_CreateVault",
            "event CreateVault(address indexed group, address indexed vault)",
            v2Erc20Lift, v2StandardTreasury);

        RegisterEvent("CrcV2_GroupMintSingle",
            "event GroupMintSingle(address indexed group, uint256 indexed id, uint256 value, bytes userData)",
            v2Erc20Lift);

        RegisterEventManual("CrcV2_GroupMintBatch",
            "GroupMintBatch(address,uint256[],uint256[],bytes)",
            new[]
            {
                ("group", FieldType.Address, true),
                ("ids", FieldType.BigIntArray, false),
                ("values", FieldType.BigIntArray, false),
                ("userData", FieldType.Bytes, false)
            },
            v2Erc20Lift);

        RegisterEvent("CrcV2_GroupRedeem",
            "event GroupRedeem(address indexed group, uint256 indexed id, uint256 value, bytes data)",
            v2Erc20Lift, v2StandardTreasury);

        RegisterEventManual("CrcV2_GroupRedeemCollateralReturn",
            "GroupRedeemCollateralReturn(address,address,uint256[],uint256[])",
            new[]
            {
                ("group", FieldType.Address, true),
                ("to", FieldType.Address, true),
                ("ids", FieldType.BigIntArray, false),
                ("values", FieldType.BigIntArray, false)
            },
            v2Erc20Lift, v2StandardTreasury);

        RegisterEventManual("CrcV2_GroupRedeemCollateralBurn",
            "GroupRedeemCollateralBurn(address,uint256[],uint256[])",
            new[]
            {
                ("group", FieldType.Address, true),
                ("ids", FieldType.BigIntArray, false),
                ("values", FieldType.BigIntArray, false)
            },
            v2Erc20Lift, v2StandardTreasury);

        // Standard Treasury specific
        RegisterEvent("CrcV2_CollateralLockedSingle",
            "event CollateralLockedSingle(address indexed group, uint256 indexed id, uint256 value, bytes userData)",
            v2StandardTreasury);

        RegisterEventManual("CrcV2_CollateralLockedBatch",
            "CollateralLockedBatch(address,uint256[],uint256[],bytes)",
            new[]
            {
                ("group", FieldType.Address, true),
                ("ids", FieldType.BigIntArray, false),
                ("values", FieldType.BigIntArray, false),
                ("userData", FieldType.Bytes, false)
            },
            v2StandardTreasury);

        // ============================================================
        // PaymentGateway Events
        // ============================================================
        RegisterEvent("CrcV2_PaymentGateway_GatewayCreated",
            "event GatewayCreated(address indexed owner, address indexed gateway)",
            paymentGatewayFactory);

        RegisterEvent("CrcV2_PaymentGateway_PaymentReceived",
            "event PaymentReceived(address indexed payer, address indexed payee, address indexed gateway, uint256 tokenId, uint256 amount, bytes data)",
            paymentGatewayFactory);

        RegisterEventManual("CrcV2_PaymentGateway_TrustUpdated",
            "TrustUpdated(address,address,uint96)",
            new[]
            {
                ("gateway", FieldType.Address, true),
                ("trustReceiver", FieldType.Address, true),
                ("expiry", FieldType.BigInt, false)
            },
            paymentGatewayFactory);

        // ============================================================
        // LBP Events
        // ============================================================
        RegisterEventManual("CrcV2_CirclesBackingDeployed",
            "CirclesBackingDeployed(address,address)",
            new[]
            {
                ("backer", FieldType.Address, true),
                ("circlesBackingInstance", FieldType.Address, true)
            },
            lbpFactories);

        RegisterEventManual("CrcV2_LBPDeployed",
            "LBPDeployed(address,address)",
            new[]
            {
                ("circlesBackingInstance", FieldType.Address, true),
                ("lbp", FieldType.Address, true)
            },
            lbpFactories);

        RegisterEventManual("CrcV2_CirclesBackingInitiated",
            "CirclesBackingInitiated(address,address,address,address)",
            new[]
            {
                ("backer", FieldType.Address, true),
                ("circlesBackingInstance", FieldType.Address, true),
                ("backingAsset", FieldType.Address, true),
                ("personalCirclesAddress", FieldType.Address, false)
            },
            lbpFactories);

        RegisterEventManual("CrcV2_CirclesBackingCompleted",
            "CirclesBackingCompleted(address,address,address)",
            new[]
            {
                ("backer", FieldType.Address, true),
                ("circlesBackingInstance", FieldType.Address, true),
                ("lbp", FieldType.Address, true)
            },
            lbpFactories);

        RegisterEventManual("CrcV2_Released",
            "Released(address,address,address)",
            new[]
            {
                ("backer", FieldType.Address, true),
                ("circlesBackingInstance", FieldType.Address, true),
                ("lbp", FieldType.Address, true)
            },
            lbpFactories);

        // ============================================================
        // TokenOffers Events
        // ============================================================
        RegisterEventManual("CrcV2_TokenOffers_AccountWeightProviderCreated",
            "AccountWeightProviderCreated(address,address)",
            new[]
            {
                ("provider", FieldType.Address, false),
                ("admin", FieldType.Address, false)
            },
            tokenOfferFactory);

        RegisterEventManual("CrcV2_TokenOffers_ERC20TokenOfferCreated",
            "ERC20TokenOfferCreated(address,address,address,address,uint256,uint256,uint256,uint256,string,address[])",
            new[]
            {
                ("tokenOffer", FieldType.Address, false),
                ("offerOwner", FieldType.Address, false),
                ("accountWeightProvider", FieldType.Address, false),
                ("offerToken", FieldType.Address, false),
                ("tokenPriceInCRC", FieldType.BigInt, false),
                ("offerLimitInCRC", FieldType.BigInt, false),
                ("offerStart", FieldType.BigInt, false),
                ("offerEnd", FieldType.BigInt, false),
                ("orgName", FieldType.String, false),
                ("acceptedCRC", FieldType.AddressArray, false)
            },
            tokenOfferFactory);

        RegisterEventManual("CrcV2_TokenOffers_ERC20TokenOfferCycleCreated",
            "ERC20TokenOfferCycleCreated(address,address,address,uint256,uint256,string,string)",
            new[]
            {
                ("offerCycle", FieldType.Address, false),
                ("cycleOwner", FieldType.Address, false),
                ("offerToken", FieldType.Address, false),
                ("offersStart", FieldType.BigInt, false),
                ("offerDuration", FieldType.BigInt, false),
                ("offerName", FieldType.String, false),
                ("cycleName", FieldType.String, false)
            },
            tokenOfferFactory);

        // TokenOffers events emitted from individual offer/cycle contracts (no fixed address)
        RegisterEventManual("CrcV2_TokenOffers_CycleConfiguration",
            "CycleConfiguration(address,address,address,uint256,uint256,bool)",
            new[]
            {
                ("admin", FieldType.Address, false),
                ("accountWeightProvider", FieldType.Address, false),
                ("offerToken", FieldType.Address, false),
                ("offersStart", FieldType.BigInt, false),
                ("offerDuration", FieldType.BigInt, false),
                ("softLockEnabled", FieldType.Boolean, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_NextOfferCreated",
            "NextOfferCreated(address,uint256,uint256,address[])",
            new[]
            {
                ("nextOffer", FieldType.Address, false),
                ("tokenPriceInCRC", FieldType.BigInt, false),
                ("offerLimitInCRC", FieldType.BigInt, false),
                ("acceptedCRC", FieldType.AddressArray, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_NextOfferTokensDeposited",
            "NextOfferTokensDeposited(address,uint256)",
            new[]
            {
                ("nextOffer", FieldType.Address, false),
                ("amount", FieldType.BigInt, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_OfferTrustSynced",
            "OfferTrustSynced(uint256,address)",
            new[]
            {
                ("offerId", FieldType.BigInt, false),
                ("offer", FieldType.Address, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_OfferClaimedFromCycle",
            "OfferClaimed(address,address,uint256,uint256)",
            new[]
            {
                ("offer", FieldType.Address, false),
                ("account", FieldType.Address, false),
                ("received", FieldType.BigInt, false),
                ("spent", FieldType.BigInt, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_UnclaimedTokensWithdrawn",
            "UnclaimedTokensWithdrawn(address,uint256)",
            new[]
            {
                ("offer", FieldType.Address, false),
                ("amount", FieldType.BigInt, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_OfferClaimed",
            "OfferClaimed(address,uint256,uint256)",
            new[]
            {
                ("account", FieldType.Address, false),
                ("spent", FieldType.BigInt, false),
                ("received", FieldType.BigInt, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_OfferTokensDeposited",
            "OfferTokensDeposited(uint256)",
            new[]
            {
                ("amount", FieldType.BigInt, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_AccountWeightSet",
            "AccountWeightSet(address,address,uint256)",
            new[]
            {
                ("offer", FieldType.Address, false),
                ("account", FieldType.Address, false),
                ("weight", FieldType.BigInt, false)
            });

        RegisterEventManual("CrcV2_TokenOffers_WeightsFinalized",
            "WeightsFinalized(address,uint256,uint256)",
            new[]
            {
                ("offer", FieldType.Address, false),
                ("accountsCount", FieldType.BigInt, false),
                ("totalWeight", FieldType.BigInt, false)
            });

        // ============================================================
        // AffiliateGroupRegistry Events
        // ============================================================
        RegisterEvent("CrcV2_AffiliateGroupChanged",
            "event AffiliateGroupChanged(address indexed human, address oldGroup, address newGroup)",
            affiliateGroupRegistry);

        RegisterEvent("CrcV2_NotificationFailed",
            "event NotificationFailed(address indexed group, address indexed human)",
            affiliateGroupRegistry);

        RegisterEvent("CrcV2_NotificationSuccessful",
            "event NotificationSuccessful(address indexed group, address indexed human)",
            affiliateGroupRegistry);

        // ============================================================
        // MultiAffiliateGroupRegistry Events
        // Both parameters are NON-indexed (affiliateGroup, avatar live in log.Data).
        // ============================================================
        RegisterEvent("CrcV2_AffiliateGroupAdded",
            "event AffiliateGroupAdded(address affiliateGroup, address avatar)",
            multiAffiliateGroupRegistry);

        RegisterEvent("CrcV2_AffiliateGroupRemoved",
            "event AffiliateGroupRemoved(address affiliateGroup, address avatar)",
            multiAffiliateGroupRegistry);

        // ============================================================
        // OIC Events
        // ============================================================
        RegisterEvent("CrcV2_OIC_OpenMiddlewareTransfer",
            "event OpenMiddlewareTransfer(address indexed onBehalf, address indexed sender, address indexed recipient, uint256 amount, uint256 inflationaryAmount, bytes data)",
            oicContract);

        // ============================================================
        // BaseGroupDeployer Events
        // ============================================================
        RegisterEvent("CrcV2_BaseGroupCreated",
            "event BaseGroupCreated(address indexed group, address indexed owner, address indexed mintHandler, address treasury)",
            baseGroupDeployer);

        // BaseGroup events emitted from individual group contracts (no fixed address)
        RegisterEventManual("CrcV2_BaseGroupOwnerUpdated",
            "OwnerUpdated(address)",
            new[]
            {
                ("owner", FieldType.Address, true)
            });

        RegisterEventManual("CrcV2_BaseGroupServiceUpdated",
            "ServiceUpdated(address)",
            new[]
            {
                ("newService", FieldType.Address, true)
            });

        RegisterEventManual("CrcV2_BaseGroupFeeCollectionUpdated",
            "FeeCollectionUpdated(address)",
            new[]
            {
                ("feeCollection", FieldType.Address, true)
            });

        // ============================================================
        // CMGroupDeployer Events
        // Note: CMGroupCreated has no topic hash in schema (new byte[32])
        // This means it's a computed/synthetic event, not backfillable from logs
        // ============================================================
        // CMGroupCreated is computed, not a real log event

        // ============================================================
        // InvitationsAtScale Events
        // ============================================================
        RegisterEvent("CrcV2_InvitationsAtScale_RegisterHuman",
            "event RegisterHuman(address indexed human, address indexed originInviter, address indexed proxyInviter)",
            invitationModule);

        RegisterEvent("CrcV2_InvitationsAtScale_AccountCreated",
            "event AccountCreated(address indexed account)",
            referralsModule);

        RegisterEvent("CrcV2_InvitationsAtScale_AccountClaimed",
            "event AccountClaimed(address indexed account)",
            referralsModule);

        RegisterEvent("CrcV2_InvitationsAtScale_AdminSet",
            "event AdminSet(address indexed newAdmin)",
            invitationFarm);

        RegisterEvent("CrcV2_InvitationsAtScale_MaintainerSet",
            "event MaintainerSet(address indexed maintainer)",
            invitationFarm);

        RegisterEvent("CrcV2_InvitationsAtScale_SeederSet",
            "event SeederSet(address indexed seeder)",
            invitationFarm);

        RegisterEvent("CrcV2_InvitationsAtScale_InviterQuotaUpdated",
            "event InviterQuotaUpdated(address indexed inviter, uint256 indexed quota)",
            invitationFarm);

        RegisterEvent("CrcV2_InvitationsAtScale_InvitationModuleUpdated",
            "event InvitationModuleUpdated(address indexed module, address indexed genericCallProxy)",
            invitationFarm);

        RegisterEvent("CrcV2_InvitationsAtScale_BotCreated",
            "event BotCreated(address indexed createdBot)",
            invitationFarm);

        RegisterEvent("CrcV2_InvitationsAtScale_InvitesClaimed",
            "event InvitesClaimed(address indexed inviter, uint256 indexed count)",
            invitationFarm);

        RegisterEvent("CrcV2_InvitationsAtScale_FarmGrown",
            "event FarmGrown(address indexed maintainer, uint256 indexed numberOfBots, uint256 indexed totalNumberOfBots)",
            invitationFarm);

        // ============================================================
        // InvitationEscrow Events
        // ============================================================
        RegisterEvent("CrcV2_InvitationEscrow_InvitationEscrowed",
            "event InvitationEscrowed(address indexed inviter, address indexed invitee, uint256 indexed amount)",
            invitationEscrowContracts);

        RegisterEvent("CrcV2_InvitationEscrow_InvitationRedeemed",
            "event InvitationRedeemed(address indexed inviter, address indexed invitee, uint256 indexed amount)",
            invitationEscrowContracts);

        RegisterEvent("CrcV2_InvitationEscrow_InvitationRefunded",
            "event InvitationRefunded(address indexed inviter, address indexed invitee, uint256 indexed amount)",
            invitationEscrowContracts);

        RegisterEvent("CrcV2_InvitationEscrow_InvitationRevoked",
            "event InvitationRevoked(address indexed inviter, address indexed invitee, uint256 indexed amount)",
            invitationEscrowContracts);

        // ============================================================
        // Safe Events (matches any Safe contract)
        // ============================================================
        RegisterEvent("Safe_ProxyCreation",
            "event ProxyCreation(address indexed proxy, address singleton)",
            safeProxyFactories);

        RegisterEventManual("Safe_SafeSetup",
            "SafeSetup(address,address[],uint256,address,address)",
            new[]
            {
                ("initiator", FieldType.Address, true),
                ("owners", FieldType.AddressArray, false),
                ("threshold", FieldType.BigInt, false),
                ("initializer", FieldType.Address, false),
                ("fallbackHandler", FieldType.Address, false)
            });

        RegisterEventManual("Safe_AddedOwner",
            "AddedOwner(address)",
            new[]
            {
                ("owner", FieldType.Address, true)
            });

        RegisterEventManual("Safe_RemovedOwner",
            "RemovedOwner(address)",
            new[]
            {
                ("owner", FieldType.Address, true)
            });
    }

    /// <summary>
    /// Register an event from a Solidity event signature.
    /// </summary>
    public static void RegisterEvent(string tableName, string soliditySignature, params string[] contractAddresses)
    {
        var def = EventDefinition.FromSolidity(tableName, soliditySignature);
        Events[tableName] = def;

        if (contractAddresses.Length > 0)
        {
            ContractFilters[def.TopicHex] = contractAddresses
                .Select(a => a.ToLowerInvariant())
                .ToHashSet();
        }
    }

    /// <summary>
    /// Register an event with manual field definitions (for non-standard types or array params).
    /// </summary>
    public static void RegisterEventManual(string tableName, string eventSignature,
        (string name, FieldType type, bool indexed)[] fields, params string[] contractAddresses)
    {
        var topic = ComputeTopicHash(eventSignature);
        var fieldList = fields.Select(f => new FieldDefinition(f.name, f.type, f.indexed)).ToList();
        var def = new EventDefinition(tableName, topic, fieldList);
        Events[tableName] = def;

        if (contractAddresses.Length > 0)
        {
            ContractFilters[def.TopicHex] = contractAddresses
                .Select(a => a.ToLowerInvariant())
                .ToHashSet();
        }
    }

    public static string ComputeTopicHash(string eventSignature)
    {
        var hash = Sha3Keccack.Current.CalculateHash(eventSignature);
        return "0x" + hash.ToLowerInvariant();
    }

    /// <summary>
    /// Get all topic hashes for the specified tables.
    /// </summary>
    public static List<string> GetTopicsForTables(IEnumerable<string> tableNames)
    {
        return tableNames
            .Where(t => Events.ContainsKey(t))
            .Select(t => Events[t].TopicHex)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Get contract addresses to filter for the specified tables.
    /// Returns null if no filter should be applied (match all addresses).
    /// </summary>
    public static HashSet<string>? GetContractAddressesForTables(IEnumerable<string> tableNames)
    {
        var addresses = new HashSet<string>();
        foreach (var table in tableNames)
        {
            if (Events.TryGetValue(table, out var evt) &&
                ContractFilters.TryGetValue(evt.TopicHex, out var filter))
            {
                foreach (var addr in filter)
                    addresses.Add(addr);
            }
        }
        return addresses.Count > 0 ? addresses : null;
    }
}

public enum FieldType
{
    Address,
    BigInt,
    Int,
    Bytes,
    Bytes32,
    String,
    Boolean,
    AddressArray,
    BigIntArray
}

public record FieldDefinition(string Name, FieldType Type, bool IsIndexed);

public class EventDefinition
{
    public string TableName { get; }
    public string TopicHex { get; }
    public List<FieldDefinition> Fields { get; }

    public EventDefinition(string tableName, string topicHex, List<FieldDefinition> fields)
    {
        TableName = tableName;
        TopicHex = topicHex.ToLowerInvariant();
        Fields = fields;
    }

    /// <summary>
    /// Parse a Solidity event signature and create an EventDefinition.
    /// Example: "event TransferBatch(address indexed _operator, address indexed _from, uint256 amount)"
    /// </summary>
    public static EventDefinition FromSolidity(string tableName, string soliditySignature)
    {
        var sig = soliditySignature.Trim();
        if (sig.StartsWith("event "))
            sig = sig.Substring(6);

        var openParen = sig.IndexOf('(');
        var closeParen = sig.LastIndexOf(')');
        if (openParen == -1 || closeParen == -1)
            throw new ArgumentException($"Invalid event signature: {soliditySignature}");

        var eventName = sig.Substring(0, openParen).Trim();
        var paramsStr = sig.Substring(openParen + 1, closeParen - openParen - 1);

        // Build canonical signature for topic hash
        var canonicalParams = new StringBuilder();
        var fields = new List<FieldDefinition>();

        var paramParts = SplitParams(paramsStr);
        for (int i = 0; i < paramParts.Count; i++)
        {
            var param = paramParts[i].Trim();
            if (string.IsNullOrEmpty(param)) continue;

            var parts = param.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                throw new ArgumentException($"Invalid parameter: {param}");

            var solType = parts[0];
            var isIndexed = parts.Length >= 3 && parts[1] == "indexed";
            var name = parts[^1]; // last part is always the name

            if (i > 0) canonicalParams.Append(',');
            canonicalParams.Append(solType);

            var fieldType = MapSolidityType(solType);
            fields.Add(new FieldDefinition(name, fieldType, isIndexed));
        }

        var topicSig = $"{eventName}({canonicalParams})";
        var topicHex = EventRegistry.ComputeTopicHash(topicSig);

        return new EventDefinition(tableName, topicHex, fields);
    }

    private static List<string> SplitParams(string paramsStr)
    {
        // Handle nested types like "bytes data" correctly
        var result = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        foreach (var c in paramsStr)
        {
            if (c == ',' && depth == 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                if (c == '(' || c == '[') depth++;
                if (c == ')' || c == ']') depth--;
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static FieldType MapSolidityType(string solType)
    {
        return solType switch
        {
            "address" => FieldType.Address,
            "address[]" => FieldType.AddressArray,
            "bool" => FieldType.Boolean,
            "string" => FieldType.String,
            "bytes" => FieldType.Bytes,
            "bytes32" => FieldType.Bytes32,
            "uint8" or "uint16" or "uint32" or "uint64" or "int8" or "int16" or "int32" or "int64" => FieldType.Int,
            "uint96" or "uint128" or "uint256" or "int96" or "int128" or "int256" => FieldType.BigInt,
            "uint256[]" or "int256[]" => FieldType.BigIntArray,
            _ when solType.StartsWith("uint") && solType.EndsWith("[]") => FieldType.BigIntArray,
            _ when solType.StartsWith("int") && solType.EndsWith("[]") => FieldType.BigIntArray,
            _ when solType.StartsWith("uint") || solType.StartsWith("int") => FieldType.BigInt,
            _ => throw new ArgumentException($"Unsupported Solidity type: {solType}")
        };
    }
}
