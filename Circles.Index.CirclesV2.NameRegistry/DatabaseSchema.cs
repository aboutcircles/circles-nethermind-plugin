using Circles.Index.Common;

namespace Circles.Index.CirclesV2.NameRegistry;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public static readonly EventSchema RegisterShortName =
        EventSchema.FromSolidity("CrcV2",
            "event RegisterShortName(address indexed avatar, uint72 shortName, uint256 nonce)");

    public static readonly EventSchema UpdateMetadataDigest = EventSchema.FromSolidity("CrcV2",
        "event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest)");

    public static readonly EventSchema CidV0 = EventSchema.FromSolidity("CrcV2",
        "event CidV0(address indexed avatar, bytes32 cidV0Digest)");

    public static readonly EventSchema Avatars = new ("V_CrcV2", "Avatars", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("type", ValueTypes.String, false, false),
        new("avatar", ValueTypes.String, false, false),
        new("tokenId", ValueTypes.String, false, false),
        new("name", ValueTypes.String, false, false),
        new("cidV0Digest", ValueTypes.Bytes, false, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
        create or replace view ""V_CrcV2_Avatars"" as
            with ""avatars"" as (
                select ""blockNumber"", 
                       ""timestamp"", 
                       ""transactionIndex"", 
                       ""logIndex"", 
                       ""transactionHash"", 
                       'organization' as ""type"",
                       ""organization"" as ""avatar"",
                       null as ""tokenId"",
                       ""name""
                from ""CrcV2_RegisterOrganization""
                union all
                select ""blockNumber"",
                       ""timestamp"",
                       ""transactionIndex"",
                       ""logIndex"",
                       ""transactionHash"",
                       'group' as ""type"",
                       ""group"" as ""avatar"",
                       ""group"" as ""tokenId"",
                       ""name""
                from ""CrcV2_RegisterGroup""
                union all
                select ""blockNumber"", 
                       ""timestamp"", 
                       ""transactionIndex"", 
                       ""logIndex"", 
                       ""transactionHash"",
                       'human' as ""type"",
                       ""avatar"",
                       ""avatar"" as ""tokenId"",
                       null as ""name""
                from ""CrcV2_RegisterHuman""
                union all
                select ""blockNumber"",
                       ""timestamp"",
                       ""transactionIndex"",
                       ""logIndex"",
                       ""transactionHash"",
                       'human' as ""type"",
                       ""invited"",
                       ""invited"" as ""tokenId"",
                       null as ""name""
                from ""CrcV2_InviteHuman""
            )
            select a.*, cid.""cidV0Digest""
            from ""avatars"" a
            left join (
                select cid.""avatar"", cid.""cidV0Digest"",
                       max(cid.""blockNumber"") as ""blockNumber"",
                       max(cid.""transactionIndex"") as ""transactionIndex"",
                       max(cid.""logIndex"") as ""logIndex""
                from ""CrcV2_CidV0"" cid
                group by cid.""avatar"", cid.""cidV0Digest""
            ) as cid on cid.""avatar"" = a.""avatar""; 
        ")
    };

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("CrcV2", "RegisterShortName"),
                RegisterShortName
            },
            {
                ("CrcV2", "UpdateMetadataDigest"),
                UpdateMetadataDigest
            },
            {
                ("CrcV2", "CidV0"),
                CidV0
            },
            {
                ("V_CrcV2", "Avatars"),
                Avatars
            }
        };

    public DatabaseSchema()
    {
        EventDtoTableMap.Add<RegisterShortName>(("CrcV2", "RegisterShortName"));
        SchemaPropertyMap.Add(("CrcV2", "RegisterShortName"),
            new Dictionary<string, Func<RegisterShortName, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "avatar", e => e.Avatar },
                { "shortName", e => e.ShortName },
                { "nonce", e => e.Nonce }
            });

        EventDtoTableMap.Add<UpdateMetadataDigest>(("CrcV2", "UpdateMetadataDigest"));
        SchemaPropertyMap.Add(("CrcV2", "UpdateMetadataDigest"),
            new Dictionary<string, Func<UpdateMetadataDigest, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "avatar", e => e.Avatar },
                { "metadataDigest", e => e.MetadataDigest }
            });
        
        EventDtoTableMap.Add<CidV0>(("CrcV2", "CidV0"));
        SchemaPropertyMap.Add(("CrcV2", "CidV0"),
            new Dictionary<string, Func<CidV0, object?>>
            {
                { "blockNumber", e => e.BlockNumber },
                { "timestamp", e => e.Timestamp },
                { "transactionIndex", e => e.TransactionIndex },
                { "logIndex", e => e.LogIndex },
                { "transactionHash", e => e.TransactionHash },
                { "avatar", e => e.Avatar },
                { "cidV0Digest", e => e.CidV0Digest }
            });
    }
}