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
    }
}