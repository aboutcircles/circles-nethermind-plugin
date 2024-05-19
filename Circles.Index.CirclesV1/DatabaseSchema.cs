using System.Numerics;
using Circles.Index.Common;
using Nethermind.Core.Crypto;

namespace Circles.Index.CirclesV1;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public static readonly EventSchema HubTransfer = EventSchema.FromSolidity("CrcV1",
        "event HubTransfer(address indexed from, address indexed to, uint256 amount)");

    public static readonly EventSchema Signup = EventSchema.FromSolidity("CrcV1",
        "event Signup(address indexed user, address indexed token)");

    public static readonly EventSchema OrganizationSignup = EventSchema.FromSolidity("CrcV1",
        "event OrganizationSignup(address indexed organization)");

    public static readonly EventSchema Trust = EventSchema.FromSolidity("CrcV1",
        "event Trust(address indexed canSendTo, address indexed user, uint256 limit)");

    public static readonly EventSchema Transfer = new("CrcV1", "Transfer",
        Keccak.Compute("Transfer(address,address,uint256)").BytesToArray(),
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("tokenAddress", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false)
        ]);

    public static readonly EventSchema TrustRelations = new("V_CrcV1", "TrustRelations", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("batchIndex", ValueTypes.Int, true, true),
        new("transactionHash", ValueTypes.String, true),
        new("user", ValueTypes.Address, true),
        new("canSendTo", ValueTypes.Address, true),
        new("limit", ValueTypes.Int, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
        create or replace view ""V_CrcV1_TrustRelations"" as 
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   ""user"",
                   ""canSendTo"",
                   ""limit""
            from (
                     select ""blockNumber"",
                            ""timestamp"",
                            ""transactionIndex"",
                            ""logIndex"",
                            ""transactionHash"",
                            ""user"",
                            ""canSendTo"",
                            ""limit"",
                            row_number() over (partition by ""user"", ""canSendTo"" order by ""blockNumber"" desc, ""transactionIndex"" desc, ""logIndex"" desc) as ""rn""
                     from ""CrcV1_Trust""
                 ) t
            where ""rn"" = 1
            and ""limit"" > 0
            order by ""blockNumber"" desc, ""transactionIndex"" desc, ""logIndex"" desc;
        ")
    };

    public static readonly EventSchema Avatars = new("V_CrcV1", "Avatars", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("user", ValueTypes.Address, true),
        new("token", ValueTypes.Address, true),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
        create or replace view ""V_CrcV1_Avatars"" as
          select ""blockNumber"",
                 ""timestamp"",
                 ""transactionIndex"",
                 ""logIndex"",
                 ""transactionHash"",
                 'human' as ""type"",
                 ""user"",
                 ""token""
          from ""CrcV1_Signup""
          union all 
          select ""blockNumber"",
                 ""timestamp"",
                 ""transactionIndex"",
                 ""logIndex"",
                 ""transactionHash"",
                 'organization' as ""type"",
                 ""organization"",
                 null as ""token""
          from ""CrcV1_OrganizationSignup"";
        ")
    };

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("CrcV1", "HubTransfer"),
                HubTransfer
            },
            {
                ("CrcV1", "Signup"),
                Signup
            },
            {
                ("CrcV1", "OrganizationSignup"),
                OrganizationSignup
            },
            {
                ("CrcV1", "Trust"),
                Trust
            },
            {
                ("CrcV1", "Transfer"),
                Transfer
            },
            {
                ("V_CrcV1", "TrustRelations"),
                TrustRelations
            },
            {
                ("V_CrcV1", "Avatars"),
                Avatars
            }
        };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<Signup>(("CrcV1", "Signup"));
        SchemaPropertyMap.Add(("CrcV1", "Signup"),
            new Dictionary<string, Func<Signup, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "user", e => e.User },
                { "token", e => e.Token }
            });

        EventDtoTableMap.Add<OrganizationSignup>(("CrcV1", "OrganizationSignup"));
        SchemaPropertyMap.Add(("CrcV1", "OrganizationSignup"),
            new Dictionary<string, Func<OrganizationSignup, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "organization", e => e.Organization }
            });

        EventDtoTableMap.Add<Trust>(("CrcV1", "Trust"));
        SchemaPropertyMap.Add(("CrcV1", "Trust"),
            new Dictionary<string, Func<Trust, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "canSendTo", e => e.CanSendTo },
                { "user", e => e.User },
                { "limit", e => e.Limit }
            });

        EventDtoTableMap.Add<HubTransfer>(("CrcV1", "HubTransfer"));
        SchemaPropertyMap.Add(("CrcV1", "HubTransfer"),
            new Dictionary<string, Func<HubTransfer, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "from", e => e.From },
                { "to", e => e.To },
                { "amount", e => (BigInteger)e.Amount }
            });

        EventDtoTableMap.Add<Transfer>(("CrcV1", "Transfer"));
        SchemaPropertyMap.Add(("CrcV1", "Transfer"),
            new Dictionary<string, Func<Transfer, object?>>
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
    }
}