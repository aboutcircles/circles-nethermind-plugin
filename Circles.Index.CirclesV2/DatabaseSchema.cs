using System.Numerics;
using Circles.Index.Common;
using Circles.Index.Utils;
using Nethermind.Core.Crypto;

namespace Circles.Index.CirclesV2;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public static readonly EventSchema InviteHuman =
        EventSchema.FromSolidity("CrcV2", "event InviteHuman(address indexed inviter, address indexed invited);");

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

    //
    // public static readonly EventSchema TransferSingle = EventSchema.FromSolidity(
    //     "CrcV2",
    //     "event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 indexed id, uint256 value)");
    public static readonly EventSchema TransferSingle = new("CrcV2", "TransferSingle",
        Keccak.Compute("TransferSingle(address,address,address,uint256,uint256)").BytesToArray(), [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("transactionHash", ValueTypes.String, true),
            new("operator", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false),
            new("tokenAddress", ValueTypes.Address, true)
        ]);

    // public static readonly EventSchema URI =
    //     EventSchema.FromSolidity("CrcV2", "event URI(string value, uint256 indexed id)");

    public static readonly EventSchema URI = new EventSchema("CrcV2", "URI",
        Keccak.Compute("URI(string,uint256)").BytesToArray(), [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("value", ValueTypes.String, true),
            new("id", ValueTypes.BigInt, true),
            new("tokenAddress", ValueTypes.Address, true)
        ]);

    public static readonly EventSchema ApprovalForAll =
        EventSchema.FromSolidity(
            "CrcV2", "event ApprovalForAll(address indexed account, address indexed operator, bool approved)");

    public static readonly EventSchema TransferBatch = new("CrcV2", "TransferBatch",
        Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
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
        Keccak.Compute("Transfer(address,address,uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
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
        Keccak.Compute("StreamCompleted(address,address,address,uint256[],uint256[])").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("operator", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("amount", ValueTypes.BigInt, false),
            new("tokenAddress", ValueTypes.Address, true)
        ]);

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("CrcV2", "InviteHuman"),
                InviteHuman
            },
            {
                ("CrcV2", "PersonalMint"),
                PersonalMint
            },
            {
                ("CrcV2", "RegisterGroup"),
                RegisterGroup
            },
            {
                ("CrcV2", "RegisterHuman"),
                RegisterHuman
            },
            {
                ("CrcV2", "RegisterOrganization"),
                RegisterOrganization
            },
            {
                ("CrcV2", "Stopped"),
                Stopped
            },
            {
                ("CrcV2", "Trust"),
                Trust
            },
            {
                ("CrcV2", "TransferSingle"),
                TransferSingle
            },
            {
                ("CrcV2", "URI"),
                URI
            },
            {
                ("CrcV2", "ApprovalForAll"),
                ApprovalForAll
            },
            {
                ("CrcV2", "TransferBatch"),
                TransferBatch
            },
            {
                ("CrcV2", "ERC20WrapperDeployed"),
                ERC20WrapperDeployed
            },
            {
                ("CrcV2", "Erc20WrapperTransfer"),
                Erc20WrapperTransfer
            },
            {
                ("CrcV2", "DepositInflationary"),
                DepositInflationary
            },
            {
                ("CrcV2", "WithdrawInflationary"),
                WithdrawInflationary
            },
            {
                ("CrcV2", "DepositDemurraged"),
                DepositDemurraged
            },
            {
                ("CrcV2", "WithdrawDemurraged"),
                WithdrawDemurraged
            },
            {
                ("CrcV2", "StreamCompleted"),
                StreamCompleted
            },
            {
                ("CrcV2", "DiscountCost"),
                DiscountCost
            }
        };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<InviteHuman>(("CrcV2", "InviteHuman"));
        SchemaPropertyMap.Add(("CrcV2", "InviteHuman"),
            new Dictionary<string, Func<InviteHuman, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "inviter", e => e.Inviter },
                { "invited", e => e.Invited }
            });

        EventDtoTableMap.Add<PersonalMint>(("CrcV2", "PersonalMint"));
        SchemaPropertyMap.Add(("CrcV2", "PersonalMint"),
            new Dictionary<string, Func<PersonalMint, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "human", e => e.Human },
                { "amount", e => (BigInteger)e.Amount },
                { "startPeriod", e => (BigInteger)e.StartPeriod },
                { "endPeriod", e => (BigInteger)e.EndPeriod }
            });

        EventDtoTableMap.Add<RegisterGroup>(("CrcV2", "RegisterGroup"));
        SchemaPropertyMap.Add(("CrcV2", "RegisterGroup"),
            new Dictionary<string, Func<RegisterGroup, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "group", e => e.Group },
                { "mint", e => e.Mint },
                { "treasury", e => e.Treasury },
                { "name", e => e.Name },
                { "symbol", e => e.Symbol }
            });

        EventDtoTableMap.Add<RegisterHuman>(("CrcV2", "RegisterHuman"));
        SchemaPropertyMap.Add(("CrcV2", "RegisterHuman"),
            new Dictionary<string, Func<RegisterHuman, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "avatar", e => e.Avatar },
                { "inviter", e => e.Inviter }
            });

        EventDtoTableMap.Add<RegisterOrganization>(("CrcV2", "RegisterOrganization"));
        SchemaPropertyMap.Add(("CrcV2", "RegisterOrganization"),
            new Dictionary<string, Func<RegisterOrganization, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "organization", e => e.Organization },
                { "name", e => e.Name }
            });

        EventDtoTableMap.Add<Stopped>(("CrcV2", "Stopped"));
        SchemaPropertyMap.Add(("CrcV2", "Stopped"),
            new Dictionary<string, Func<Stopped, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "avatar", e => e.Avatar }
            });

        EventDtoTableMap.Add<Trust>(("CrcV2", "Trust"));
        SchemaPropertyMap.Add(("CrcV2", "Trust"),
            new Dictionary<string, Func<Trust, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "truster", e => e.Truster },
                { "trustee", e => e.Trustee },
                { "expiryTime", e => (BigInteger)e.ExpiryTime }
            });

        EventDtoTableMap.Add<ApprovalForAll>(("CrcV2", "ApprovalForAll"));
        SchemaPropertyMap.Add(("CrcV2", "ApprovalForAll"),
            new Dictionary<string, Func<ApprovalForAll, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "account", e => e.Account },
                { "operator", e => e.Operator },
                { "approved", e => e.Approved }
            });

        EventDtoTableMap.Add<TransferSingle>(("CrcV2", "TransferSingle"));
        SchemaPropertyMap.Add(("CrcV2", "TransferSingle"),
            new Dictionary<string, Func<TransferSingle, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "operator", e => e.Operator },
                { "from", e => e.From },
                { "to", e => e.To },
                { "id", e => (BigInteger)e.Id },
                { "value", e => (BigInteger)e.Value },
                { "tokenAddress", e => ConversionUtils.UInt256ToAddress(e.Id).ToString(true, false) }
            });

        EventDtoTableMap.Add<TransferBatch>(("CrcV2", "TransferBatch"));
        SchemaPropertyMap.Add(("CrcV2", "TransferBatch"),
            new Dictionary<string, Func<TransferBatch, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "batchIndex", e => e.BatchIndex },
                { "transactionHash", e => e.TransactionHash },
                { "operator", e => e.Operator },
                { "from", e => e.From },
                { "to", e => e.To },
                { "id", e => (BigInteger)e.Id },
                { "value", e => (BigInteger)e.Value },
                { "tokenAddress", e => ConversionUtils.UInt256ToAddress(e.Id).ToString(true, false) }
            });

        EventDtoTableMap.Add<ERC20WrapperDeployed>(("CrcV2", "ERC20WrapperDeployed"));
        SchemaPropertyMap.Add(("CrcV2", "ERC20WrapperDeployed"),
            new Dictionary<string, Func<ERC20WrapperDeployed, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "avatar", e => e.Avatar },
                { "erc20Wrapper", e => e.Erc20Wrapper },
                { "circlesType", e => e.CirclesType }
            });

        EventDtoTableMap.Add<Erc20WrapperTransfer>(("CrcV2", "Erc20WrapperTransfer"));
        SchemaPropertyMap.Add(("CrcV2", "Erc20WrapperTransfer"),
            new Dictionary<string, Func<Erc20WrapperTransfer, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "tokenAddress", e => e.TokenAddress },
                { "from", e => e.From },
                { "to", e => e.To },
                { "amount", e => (BigInteger)e.Value }
            });

        EventDtoTableMap.Add<DepositInflationary>(("CrcV2", "DepositInflationary"));
        SchemaPropertyMap.Add(("CrcV2", "DepositInflationary"),
            new Dictionary<string, Func<DepositInflationary, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "account", e => e.Account },
                { "amount", e => (BigInteger)e.Amount },
                { "demurragedAmount", e => (BigInteger)e.DemurragedAmount }
            });

        EventDtoTableMap.Add<WithdrawInflationary>(("CrcV2", "WithdrawInflationary"));
        SchemaPropertyMap.Add(("CrcV2", "WithdrawInflationary"),
            new Dictionary<string, Func<WithdrawInflationary, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "account", e => e.Account },
                { "amount", e => (BigInteger)e.Amount },
                { "demurragedAmount", e => (BigInteger)e.DemurragedAmount }
            });

        EventDtoTableMap.Add<DepositDemurraged>(("CrcV2", "DepositDemurraged"));
        SchemaPropertyMap.Add(("CrcV2", "DepositDemurraged"),
            new Dictionary<string, Func<DepositDemurraged, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "account", e => e.Account },
                { "amount", e => (BigInteger)e.Amount },
                { "inflationaryAmount", e => (BigInteger)e.InflationaryAmount }
            });

        EventDtoTableMap.Add<WithdrawDemurraged>(("CrcV2", "WithdrawDemurraged"));
        SchemaPropertyMap.Add(("CrcV2", "WithdrawDemurraged"),
            new Dictionary<string, Func<WithdrawDemurraged, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "account", e => e.Account },
                { "amount", e => (BigInteger)e.Amount },
                { "inflationaryAmount", e => (BigInteger)e.InflationaryAmount }
            });

        EventDtoTableMap.Add<StreamCompleted>(("CrcV2", "StreamCompleted"));
        SchemaPropertyMap.Add(("CrcV2", "StreamCompleted"),
            new Dictionary<string, Func<StreamCompleted, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "batchIndex", e => e.BatchIndex },
                { "transactionHash", e => e.TransactionHash },
                { "operator", e => e.Operator },
                { "from", e => e.From },
                { "to", e => e.To },
                { "id", e => (BigInteger)e.Id },
                { "amount", e => (BigInteger)e.Amount },
                { "tokenAddress", e => ConversionUtils.UInt256ToAddress(e.Id).ToString(true, false) }
            });

        EventDtoTableMap.Add<DiscountCost>(("CrcV2", "DiscountCost"));
        SchemaPropertyMap.Add(("CrcV2", "DiscountCost"),
            new Dictionary<string, Func<DiscountCost, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "account", e => e.Account },
                { "id", e => (BigInteger)e.Id },
                { "discountCost", e => (BigInteger)e.Cost }
            });
    }
}