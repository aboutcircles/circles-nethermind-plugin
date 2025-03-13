using System.Numerics;
using Circles.Index.Common;

namespace Circles.Index.CirclesV2.NameRegistry;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema RegisterShortName =
        EventSchema.FromSolidity("CrcV2",
            "event RegisterShortName(address indexed avatar, uint72 shortName, uint256 nonce)");

    public static readonly EventSchema UpdateMetadataDigest = EventSchema.FromSolidity("CrcV2",
        "event UpdateMetadataDigest(address indexed avatar, bytes32 metadataDigest)");

    public static readonly EventSchema CidV0 = EventSchema.FromSolidity("CrcV2",
        "event CidV0(address indexed avatar, bytes32 cidV0Digest)");

    public DatabaseSchema()
    {
        AddMappings<RegisterShortName>(
            ns: "CrcV2",
            table: "RegisterShortName",
            eventSchema: RegisterShortName,
            databaseFieldMap:
            [
                ("avatar",    e => e.Avatar),
                ("shortName", e => (BigInteger)e.ShortName),
                ("nonce",     e => (BigInteger)e.Nonce)
            ]
        );

        AddMappings<UpdateMetadataDigest>(
            ns: "CrcV2",
            table: "UpdateMetadataDigest",
            eventSchema: UpdateMetadataDigest,
            databaseFieldMap:
            [
                ("avatar",          e => e.Avatar),
                ("metadataDigest",  e => e.MetadataDigest)
            ]
        );

        AddMappings<CidV0>(
            ns: "CrcV2",
            table: "CidV0",
            eventSchema: CidV0,
            databaseFieldMap:
            [
                ("avatar",     e => e.Avatar),
                ("cidV0Digest", e => e.CidV0Digest)
            ]
        );
    }
}