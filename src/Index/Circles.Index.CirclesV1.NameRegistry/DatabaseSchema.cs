using Circles.Common;

namespace Circles.Index.CirclesV1.NameRegistry;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema UpdateMetadataDigest = EventSchema.FromSolidity("CrcV1",
        "event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest)");

    public DatabaseSchema()
    {
        AddMappings<UpdateMetadataDigest>(
            ns: "CrcV1",
            table: "UpdateMetadataDigest",
            eventSchema: UpdateMetadataDigest,
            databaseFieldMap:
            [
                ("avatar", e => e.Avatar),
                ("metadataDigest", e => e.MetadataDigest)
            ]
        );
    }
}