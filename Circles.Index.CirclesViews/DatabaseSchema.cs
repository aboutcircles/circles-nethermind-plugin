using Circles.Index.Common;

namespace Circles.Index.CirclesViews;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public static readonly EventSchema TrustRelations = new("V_Crc", "TrustRelations", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("version", ValueTypes.Int, false),
        new("trustee", ValueTypes.String, false),
        new("truster", ValueTypes.String, false),
        new("expiryTime", ValueTypes.Int, false),
        new("limit", ValueTypes.Int, false)
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
        create or replace view ""V_Crc_TrustRelations"" as
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   2 as ""version"",
                   ""trustee"",
                   ""truster"",
                   ""expiryTime"",
                   null as ""limit""
            from ""V_CrcV2_TrustRelations""
            union all
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   1 as ""version"",
                   ""user"",
                   ""canSendTo"",
                   null as ""expiryTime"",
                   ""limit""
            from ""V_CrcV1_TrustRelations"";
        ")
    };

    public static readonly EventSchema Avatars = new("V_Crc", "Avatars", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("version", ValueTypes.Int, false),
        new("type", ValueTypes.String, false),
        new("invitedBy", ValueTypes.String, false),
        new("avatar", ValueTypes.String, false),
        new("tokenId", ValueTypes.String, false),
        new("name", ValueTypes.String, false),
        new("cidV0Digest", ValueTypes.Bytes, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
        create or replace view ""V_Crc_Avatars"" as
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   2 as ""version"",
                   ""type"",
                   ""invitedBy"",
                   ""avatar"",
                   ""tokenId"",
                   ""name"",
                   ""cidV0Digest""
            from ""V_CrcV2_Avatars""
            union all 
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   1 as ""version"",
                   ""type"",
                   null as ""invitedBy"",
                   ""user"" as ""avatar"",
                   ""token"" as ""tokenId"",
                   null as ""name"",
                   null as ""cidV0Digest""
            from ""V_CrcV1_Avatars"";
        ")
    };

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("V_Crc", "Avatars"),
                Avatars
            },
            {
                ("V_Crc", "TrustRelations"),
                TrustRelations
            }
        };
}