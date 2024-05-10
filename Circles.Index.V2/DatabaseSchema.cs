using Circles.Index.Common;
using Nethermind.Core.Crypto;

namespace Circles.Index.V2;

/*

 hub/
    Hub.sol:
        event PersonalMint(address indexed human, uint256 amount, uint256 startPeriod, uint256 endPeriod);
        event RegisterHuman(address indexed avatar);
        event RegisterGroup(address indexed group, address indexed mint, address indexed treasury, string indexed name, string indexed symbol);
        event InviteHuman(address indexed inviter, address indexed invited);
        event RegisterOrganization(address indexed organization, string name);
        event Stopped(address indexed avatar);
        event Trust(address indexed truster, address indexed trustee, uint256 expiryTime);
        event URI(string value, uint256 indexed id);
        event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 id, uint256 value);
        event ApprovalForAll(address indexed account, address indexed operator, bool approved);

       Manual events:
        event TransferBatch(address indexed operator, address indexed from, address indexed to, uint256[] ids, uint256[] values);

 TODO: 
 lift/
    DemurrageCircles.sol:
        event DepositDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount);
        event WithdrawDemurraged(address indexed account, uint256 amount, uint256 inflationaryAmount);

    InflationaryCircles.sol:
        event DepositInflationary(address indexed account, uint256 amount, uint256 demurragedAmount);
        event WithdrawInflationary(address indexed account, uint256 amount, uint256 demurragedAmount);

 names/
    NameRegistry.sol:
        event RegisterShortName(address indexed avatar, uint72 shortName, uint256 nonce);
        event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest);

 proxy/
    ProxyFactory.sol:
        event ProxyCreation(address proxy, address masterCopy);
 */
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
        EventSchema.FromSolidity("CrcV2", "event RegisterHuman(address indexed avatar)");

    public static readonly EventSchema RegisterOrganization =
        EventSchema.FromSolidity("CrcV2",
            "event RegisterOrganization(address indexed organization, string indexed name)");

    public static readonly EventSchema Stopped =
        EventSchema.FromSolidity("CrcV2", "event Stopped(address indexed avatar)");

    public static readonly EventSchema Trust =
        EventSchema.FromSolidity("CrcV2",
            "event Trust(address indexed truster, address indexed trustee, uint256 expiryTime)");

    public static readonly EventSchema TransferSingle = EventSchema.FromSolidity(
        "CrcV2",
        "event TransferSingle(address indexed operator, address indexed from, address indexed to, uint256 indexed id, uint256 value)");

    public static readonly EventSchema URI =
        EventSchema.FromSolidity("CrcV2", "event URI(string value, uint256 indexed id)");

    public static readonly EventSchema ApprovalForAll =
        EventSchema.FromSolidity(
            "CrcV2", "event ApprovalForAll(address indexed account, address indexed operator, bool approved)");

    public static readonly EventSchema DiscountCost = EventSchema.FromSolidity("CrcV2",
        "event DiscountCost(address indexed account, uint256 indexed id, uint256 discountCost)");

    public static readonly EventSchema TransferBatch = new("CrcV2", "TransferBatch",
        Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])"),
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("batchIndex", ValueTypes.Int, true),
            new("transactionHash", ValueTypes.String, true),
            new("operatorAddress", ValueTypes.Address, true),
            new("fromAddress", ValueTypes.Address, true),
            new("toAddress", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false)
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
                { "amount", e => e.Amount },
                { "startPeriod", e => e.StartPeriod },
                { "endPeriod", e => e.EndPeriod }
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
                { "avatar", e => e.Avatar }
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
                { "expiryTime", e => e.ExpiryTime }
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
                { "id", e => e.Id },
                { "value", e => e.Value }
            });

        EventDtoTableMap.Add<TransferBatch>(("CrcV2", "TransferBatch"));
        SchemaPropertyMap.Add(("CrcV2", "TransferBatch"),
            new Dictionary<string, Func<TransferBatch, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "operator", e => e.Operator },
                { "from", e => e.From },
                { "to", e => e.To },
                { "id", e => e.Id },
                { "value", e => e.Value }
            });

        EventDtoTableMap.Add<URI>(("CrcV2", "URI"));
        SchemaPropertyMap.Add(("CrcV2", "URI"),
            new Dictionary<string, Func<URI, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "value", e => e.Value },
                { "id", e => e.Id }
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
                { "id", e => e.Id },
                { "discountCost", e => e._DiscountCost }
            });
    }
}