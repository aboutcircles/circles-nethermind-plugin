using System.Numerics;
using Circles.Common;

namespace Circles.Index.CirclesV2;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema PersonalMint = EventSchema.FromSolidity("CrcV2",
        "event PersonalMint(address indexed human, uint256 amount, uint256 startPeriod, uint256 endPeriod)");

    public static readonly EventSchema RegisterGroup = EventSchema.FromSolidity("CrcV2",
        "event RegisterGroup(address indexed group, address indexed mint, address indexed treasury, string indexed name, string indexed symbol)");

    public static readonly EventSchema RegisterHuman =
        EventSchema.FromSolidity("CrcV2", "event RegisterHuman(address indexed avatar, address indexed inviter)");

    public static readonly EventSchema RegisterOrganization =
        EventSchema.FromSolidity("CrcV2",
            "event RegisterOrganization(address indexed organization, string indexed name)");

    public static readonly EventSchema Stopped =
        EventSchema.FromSolidity("CrcV2", "event Stopped(address indexed avatar)");

    public static readonly EventSchema Trust =
        EventSchema.FromSolidity("CrcV2",
            "event Trust(address indexed truster, address indexed trustee, uint256 expiryTime)");

    public static readonly EventSchema DiscountCost =
        EventSchema.FromSolidity("CrcV2",
            "event DiscountCost(address indexed account, uint256 indexed id, uint256 discountCost)");

    public static readonly EventSchema TransferSingle = new("CrcV2", "TransferSingle",
        KeccakHelper.ComputeHash("TransferSingle(address,address,address,uint256,uint256)"), [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("operator", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false),
            new("tokenAddress", ValueTypes.Address, true)
        ]);

    public static readonly EventSchema ApprovalForAll =
        EventSchema.FromSolidity(
            "CrcV2", "event ApprovalForAll(address indexed account, address indexed operator, bool approved)");

    public static readonly EventSchema TransferBatch = new("CrcV2", "TransferBatch",
        KeccakHelper.ComputeHash("TransferBatch(address,address,address,uint256[],uint256[])"),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("operator", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false),
            new("tokenAddress", ValueTypes.Address, true)
        ]);

    public static readonly EventSchema Erc20WrapperTransfer = new("CrcV2", "Erc20WrapperTransfer",
        KeccakHelper.ComputeHash("Transfer(address,address,uint256)"),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("tokenAddress", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false)
        ]);

    public static readonly EventSchema ERC20WrapperDeployed = EventSchema.FromSolidity("CrcV2",
        "event ERC20WrapperDeployed(address indexed avatar, address indexed erc20Wrapper, uint8 circlesType)");

    public static readonly EventSchema DepositInflationary = EventSchema.FromSolidity("CrcV2",
        "event DepositInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)");

    public static readonly EventSchema WithdrawInflationary = EventSchema.FromSolidity("CrcV2",
        "event WithdrawInflationary(address indexed account, uint256 amount, uint256 demurragedAmount)");

    public static readonly EventSchema DepositDemurraged = EventSchema.FromSolidity("CrcV2",
        "event DepositDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)");

    public static readonly EventSchema WithdrawDemurraged = EventSchema.FromSolidity("CrcV2",
        "event WithdrawDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount)");

    public static readonly EventSchema StreamCompleted = new("CrcV2", "StreamCompleted",
        KeccakHelper.ComputeHash("StreamCompleted(address,address,address,uint256[],uint256[])"),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("operator", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("amount", ValueTypes.BigInt, false),
            new("tokenAddress", ValueTypes.Address, true)
        ]);

    public static readonly EventSchema GroupMint = new(
        "CrcV2",
        "GroupMint",
        KeccakHelper.ComputeHash("GroupMint(address,address,address,uint256[],uint256[])"),
        [
            new EventFieldSchema("blockNumber", ValueTypes.Int, true, true),
            new EventFieldSchema("timestamp", ValueTypes.Int, true),
            new EventFieldSchema("transactionIndex", ValueTypes.Int, true, true),
            new EventFieldSchema("logIndex", ValueTypes.Int, true, true),
            new EventFieldSchema("batchIndex", ValueTypes.Int, true, true),
            new EventFieldSchema("transactionHash", ValueTypes.String, true),
            new EventFieldSchema("sender", ValueTypes.Address, true),
            new EventFieldSchema("receiver", ValueTypes.Address, true),
            new EventFieldSchema("group", ValueTypes.Address, true),
            new EventFieldSchema("collateral", ValueTypes.BigInt, false),
            new EventFieldSchema("amount", ValueTypes.BigInt, false)
        ]
    );

    public static readonly EventSchema FlowEdgesScopeSingleStarted =
        EventSchema.FromSolidity("CrcV2",
            "event FlowEdgesScopeSingleStarted(uint256 indexed flowEdgeId, uint16 streamId)");

    public static readonly EventSchema SetAdvancedUsageFlag =
        EventSchema.FromSolidity("CrcV2",
            "event SetAdvancedUsageFlag(address indexed avatar, bytes32 flag)");

    public static readonly EventSchema FlowEdgesScopeLastEnded = new(
        "CrcV2",
        "FlowEdgesScopeLastEnded",
        KeccakHelper.ComputeHash("FlowEdgesScopeLastEnded()"),
        [
            new EventFieldSchema("blockNumber", ValueTypes.Int, true, true),
            new EventFieldSchema("timestamp", ValueTypes.Int, true),
            new EventFieldSchema("transactionIndex", ValueTypes.Int, true, true),
            new EventFieldSchema("logIndex", ValueTypes.Int, true, true),
            new EventFieldSchema("transactionHash", ValueTypes.String, true)
        ]
    );

    public static readonly EventSchema TransferSummary = new("CrcV2", "TransferSummary",
        new byte[32],
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false),
            new("events", ValueTypes.Json, false)
        ]);

    // Constructor that uses the shared helper AddMappings<>() 
    // to register each event's table and property map in one pass.
    public DatabaseSchema()
    {
        AddMappings<PersonalMint>(
            ns: "CrcV2",
            table: "PersonalMint",
            eventSchema: PersonalMint,
            databaseFieldMap:
            [
                ("human", e => e.Human),
                ("amount", e => (BigInteger)e.Amount),
                ("startPeriod", e => (BigInteger)e.StartPeriod),
                ("endPeriod", e => (BigInteger)e.EndPeriod)
            ]
        );

        AddMappings<RegisterGroup>(
            ns: "CrcV2",
            table: "RegisterGroup",
            eventSchema: RegisterGroup,
            databaseFieldMap:
            [
                ("group", e => e.Group),
                ("mint", e => e.Mint),
                ("treasury", e => e.Treasury),
                ("name", e => e.Name),
                ("symbol", e => e.Symbol)
            ]
        );

        AddMappings<RegisterHuman>(
            ns: "CrcV2",
            table: "RegisterHuman",
            eventSchema: RegisterHuman,
            databaseFieldMap:
            [
                ("avatar", e => e.Avatar),
                ("inviter", e => e.Inviter)
            ]
        );

        AddMappings<RegisterOrganization>(
            ns: "CrcV2",
            table: "RegisterOrganization",
            eventSchema: RegisterOrganization,
            databaseFieldMap:
            [
                ("organization", e => e.Organization),
                ("name", e => e.Name)
            ]
        );

        AddMappings<Stopped>(
            ns: "CrcV2",
            table: "Stopped",
            eventSchema: Stopped,
            databaseFieldMap:
            [
                ("avatar", e => e.Avatar)
            ]
        );

        AddMappings<Trust>(
            ns: "CrcV2",
            table: "Trust",
            eventSchema: Trust,
            databaseFieldMap:
            [
                ("truster", e => e.Truster),
                ("trustee", e => e.Trustee),
                ("expiryTime", e => (BigInteger)e.ExpiryTime)
            ]
        );

        AddMappings<DiscountCost>(
            ns: "CrcV2",
            table: "DiscountCost",
            eventSchema: DiscountCost,
            databaseFieldMap:
            [
                ("account", e => e.Account),
                ("id", e => (BigInteger)e.Id),
                ("discountCost", e => (BigInteger)e.Cost)
            ]
        );

        AddMappings<TransferSingle>(
            ns: "CrcV2",
            table: "TransferSingle",
            eventSchema: TransferSingle,
            databaseFieldMap:
            [
                ("operator", e => e.Operator),
                ("from", e => e.From),
                ("to", e => e.To),
                ("id", e => (BigInteger)e.Id),
                ("value", e => (BigInteger)e.Value),
                ("tokenAddress", e => AddressConverter.UInt256ToAddress(e.Id).ToLowerHex())
            ]
        );

        AddMappings<ApprovalForAll>(
            ns: "CrcV2",
            table: "ApprovalForAll",
            eventSchema: ApprovalForAll,
            databaseFieldMap:
            [
                ("account", e => e.Account),
                ("operator", e => e.Operator),
                ("approved", e => e.Approved)
            ]
        );

        AddMappings<TransferBatch>(
            ns: "CrcV2",
            table: "TransferBatch",
            eventSchema: TransferBatch,
            databaseFieldMap:
            [
                ("batchIndex", e => e.BatchIndex),
                ("operator", e => e.Operator),
                ("from", e => e.From),
                ("to", e => e.To),
                ("id", e => (BigInteger)e.Id),
                ("value", e => (BigInteger)e.Value),
                ("tokenAddress", e => AddressConverter.UInt256ToAddress(e.Id).ToLowerHex())
            ]
        );

        AddMappings<ERC20WrapperDeployed>(
            ns: "CrcV2",
            table: "ERC20WrapperDeployed",
            eventSchema: ERC20WrapperDeployed,
            databaseFieldMap:
            [
                ("avatar", e => e.Avatar),
                ("erc20Wrapper", e => e.Erc20Wrapper),
                ("circlesType", e => e.CirclesType)
            ]
        );

        AddMappings<Erc20WrapperTransfer>(
            ns: "CrcV2",
            table: "Erc20WrapperTransfer",
            eventSchema: Erc20WrapperTransfer,
            databaseFieldMap:
            [
                ("tokenAddress", e => e.TokenAddress),
                ("from", e => e.From),
                ("to", e => e.To),
                ("amount", e => (BigInteger)e.Value)
            ]
        );

        AddMappings<DepositInflationary>(
            ns: "CrcV2",
            table: "DepositInflationary",
            eventSchema: DepositInflationary,
            databaseFieldMap:
            [
                ("account", e => e.Account),
                ("amount", e => (BigInteger)e.Amount),
                ("demurragedAmount", e => (BigInteger)e.DemurragedAmount)
            ]
        );

        AddMappings<WithdrawInflationary>(
            ns: "CrcV2",
            table: "WithdrawInflationary",
            eventSchema: WithdrawInflationary,
            databaseFieldMap:
            [
                ("account", e => e.Account),
                ("amount", e => (BigInteger)e.Amount),
                ("demurragedAmount", e => (BigInteger)e.DemurragedAmount)
            ]
        );

        AddMappings<DepositDemurraged>(
            ns: "CrcV2",
            table: "DepositDemurraged",
            eventSchema: DepositDemurraged,
            databaseFieldMap:
            [
                ("account", e => e.Account),
                ("amount", e => (BigInteger)e.Amount),
                ("inflationaryAmount", e => (BigInteger)e.InflationaryAmount)
            ]
        );

        AddMappings<WithdrawDemurraged>(
            ns: "CrcV2",
            table: "WithdrawDemurraged",
            eventSchema: WithdrawDemurraged,
            databaseFieldMap:
            [
                ("account", e => e.Account),
                ("amount", e => (BigInteger)e.Amount),
                ("inflationaryAmount", e => (BigInteger)e.InflationaryAmount)
            ]
        );

        AddMappings<StreamCompleted>(
            ns: "CrcV2",
            table: "StreamCompleted",
            eventSchema: StreamCompleted,
            databaseFieldMap:
            [
                ("batchIndex", e => e.BatchIndex),
                ("operator", e => e.Operator),
                ("from", e => e.From),
                ("to", e => e.To),
                ("id", e => (BigInteger)e.Id),
                ("amount", e => (BigInteger)e.Amount),
                ("tokenAddress", e => AddressConverter.UInt256ToAddress(e.Id).ToLowerHex())
            ]
        );

        AddMappings<GroupMint>(
            ns: "CrcV2",
            table: "GroupMint",
            eventSchema: GroupMint,
            databaseFieldMap:
            [
                ("batchIndex", e => e.BatchIndex),
                ("sender", e => e.Sender),
                ("receiver", e => e.Receiver),
                ("group", e => e.Group),
                ("collateral", e => (BigInteger)e.Collateral),
                ("amount", e => (BigInteger)e.Amount)
            ]
        );

        AddMappings<FlowEdgesScopeSingleStarted>(
            ns: "CrcV2",
            table: "FlowEdgesScopeSingleStarted",
            eventSchema: FlowEdgesScopeSingleStarted,
            databaseFieldMap:
            [
                ("flowEdgeId", e => (BigInteger)e.FlowEdgeId),
                ("streamId", e => (long)e.StreamId)
            ]
        );

        AddMappings<FlowEdgesScopeLastEnded>(
            ns: "CrcV2",
            table: "FlowEdgesScopeLastEnded",
            eventSchema: FlowEdgesScopeLastEnded,
            databaseFieldMap: []
        );

        AddMappings<TransferSummary>(
            ns: "CrcV2",
            table: "TransferSummary",
            eventSchema: TransferSummary,
            databaseFieldMap:
            [
                ("from", e => e.From),
                ("to", e => e.To),
                ("amount", e => (BigInteger)e.Amount),
                ("events", e => e.Events)
            ]
        );

        AddMappings<SetAdvancedUsageFlag>(
            ns: "CrcV2",
            table: "SetAdvancedUsageFlag",
            eventSchema: SetAdvancedUsageFlag,
            databaseFieldMap:
            [
                ("avatar", e => e.Avatar),
                ("flag", e => e.Flag)
            ]
        );
    }
}