using Circles.Index.Common;

namespace Circles.Index.CirclesViews;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public static readonly EventSchema V_CrcV1_TrustRelations = new("V_CrcV1", "TrustRelations", new byte[32], [
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

    public static readonly EventSchema V_CrcV1_Avatars = new("V_CrcV1", "Avatars", new byte[32], [
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

    /// <summary>
    /// All Circles v1 hub transfers + personal minting
    /// </summary>
    public static readonly EventSchema V_CrcV1_Transfers = new("V_CrcV1", "Transfers",
        new byte[32],
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("transactionHash", ValueTypes.String, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view ""V_CrcV1_Transfers"" (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", ""from"", ""to"", ""tokenAddress"", ""amount"") as
                            WITH ""allTransfers"" AS (SELECT ""CrcV1_HubTransfer"".""blockNumber"",
                                                           ""CrcV1_HubTransfer"".""timestamp"",
                                                           ""CrcV1_HubTransfer"".""transactionIndex"",
                                                           ""CrcV1_HubTransfer"".""logIndex"",
                                                           ""CrcV1_HubTransfer"".""transactionHash"",
                                                           ""CrcV1_HubTransfer"".""from"",
                                                           ""CrcV1_HubTransfer"".""to"",
                                                           null as ""tokenAddress"",
                                                           ""CrcV1_HubTransfer"".""amount""
                                                    FROM ""CrcV1_HubTransfer""
                                                    UNION ALL
                                                    SELECT t.""blockNumber"",
                                                           t.""timestamp"",
                                                           t.""transactionIndex"",
                                                           t.""logIndex"",
                                                           t.""transactionHash"",
                                                           t.""from"",
                                                           t.""to"",
                                                           t.""tokenAddress"",
                                                           t.""amount""
                                                    FROM ""CrcV1_Transfer"" t
                                                             JOIN ""CrcV1_Signup"" s ON s.""token"" = t.""tokenAddress"" AND s.""user"" = t.""to""
                                                    WHERE t.""from"" = '0x0000000000000000000000000000000000000000'::text)
                            SELECT ""blockNumber"",
                                   ""timestamp"",
                                   ""transactionIndex"",
                                   ""logIndex"",
                                   ""transactionHash"",
                                   ""from"",
                                   ""to"",
                                   ""tokenAddress"",
                                   ""amount""
                            FROM ""allTransfers""
                            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC;
        ")
    };

    public static readonly EventSchema V_CrcV2_Avatars = new("V_CrcV2", "Avatars", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("type", ValueTypes.String, false),
        new("invitedBy", ValueTypes.String, false),
        new("avatar", ValueTypes.String, false),
        new("tokenId", ValueTypes.String, false),
        new("name", ValueTypes.String, false),
        new("cidV0Digest", ValueTypes.Bytes, false),
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
                       null as ""invitedBy"",
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
                       null as ""invitedBy"",
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
                       null as ""invitedBy"",
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
                       ""inviter"" as ""invitedBy"",
                       ""invited"",
                       ""invited"" as ""tokenId"",
                       null as ""name""
                from ""CrcV2_InviteHuman""
            )
            select a.*, cid.""cidV0Digest""
            from ""avatars"" a
            left join (
                SELECT cid_1.avatar,
                           cid_1.""metadataDigest""        AS ""cidV0Digest"",
                           max(cid_1.""blockNumber"")      AS ""blockNumber"",
                           max(cid_1.""transactionIndex"") AS ""transactionIndex"",
                           max(cid_1.""logIndex"")         AS ""logIndex""
                    FROM ""CrcV2_UpdateMetadataDigest"" cid_1
                    GROUP BY cid_1.avatar, cid_1.""metadataDigest""
            ) as cid on cid.""avatar"" = a.""avatar""; 
        ")
    };

    public static readonly EventSchema V_CrcV2_Transfers = new("V_CrcV2", "Transfers",
        new byte[32],
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
            new("value", ValueTypes.BigInt, false)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view ""V_CrcV2_Transfers"" (
                ""blockNumber""
                , ""timestamp""
                , ""transactionIndex""
                , ""logIndex""
                , ""batchIndex""
                , ""transactionHash""
                , ""operator""
                , ""from""
                , ""to""
                , ""id""
                , ""value""
            ) as
                WITH ""allTransfers"" AS (
                    SELECT ""CrcV2_TransferSingle"".""blockNumber"",
                           ""CrcV2_TransferSingle"".""timestamp"",
                           ""CrcV2_TransferSingle"".""transactionIndex"",
                           ""CrcV2_TransferSingle"".""logIndex"",
                           0 AS ""batchIndex"",
                           ""CrcV2_TransferSingle"".""transactionHash"",
                           ""CrcV2_TransferSingle"".""operator"",
                           ""CrcV2_TransferSingle"".""from"",
                           ""CrcV2_TransferSingle"".""to"",
                           ""CrcV2_TransferSingle"".""id""::text,
                           ""CrcV2_TransferSingle"".""value"",
                           'erc1155' as type
                    FROM ""CrcV2_TransferSingle""
                    UNION ALL
                    SELECT ""CrcV2_TransferBatch"".""blockNumber"",
                           ""CrcV2_TransferBatch"".""timestamp"",
                           ""CrcV2_TransferBatch"".""transactionIndex"",
                           ""CrcV2_TransferBatch"".""logIndex"",
                           ""CrcV2_TransferBatch"".""batchIndex"",
                           ""CrcV2_TransferBatch"".""transactionHash"",
                           ""CrcV2_TransferBatch"".""operator"",
                           ""CrcV2_TransferBatch"".""from"",
                           ""CrcV2_TransferBatch"".""to"",
                           ""CrcV2_TransferBatch"".""id""::text,
                           ""CrcV2_TransferBatch"".""value"",
                           'erc1155' as type
                    FROM ""CrcV2_TransferBatch""
                    UNION ALL
                    SELECT ""CrcV2_Erc20WrapperTransfer"".""blockNumber"",
                           ""CrcV2_Erc20WrapperTransfer"".""timestamp"",
                           ""CrcV2_Erc20WrapperTransfer"".""transactionIndex"",
                           ""CrcV2_Erc20WrapperTransfer"".""logIndex"",
                           0 as ""batchIndex"",
                           ""CrcV2_Erc20WrapperTransfer"".""transactionHash"",
                           null as ""operator"",
                           ""CrcV2_Erc20WrapperTransfer"".""from"",
                           ""CrcV2_Erc20WrapperTransfer"".""to"",
                           ""CrcV2_Erc20WrapperTransfer"".""tokenAddress"" as ""id"",
                           ""CrcV2_Erc20WrapperTransfer"".""amount"" as ""value"",
                           'erc20' as type
                    FROM ""CrcV2_Erc20WrapperTransfer""
                )
                SELECT ""blockNumber"",
                       ""timestamp"",
                       ""transactionIndex"",
                       ""logIndex"",
                       ""batchIndex"",
                       ""transactionHash"",
                       ""operator"",
                       ""from"",
                       ""to"",
                       ""id"",
                       ""value""
                       --,""type""
                FROM ""allTransfers""
                ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC, ""batchIndex"" DESC;
        ")
    };

    public static readonly EventSchema V_CrcV2_GroupMemberships = new("V_CrcV2", "GroupMemberships", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("group", ValueTypes.Address, true),
        new("member", ValueTypes.Address, true),
        new("expiryTime", ValueTypes.BigInt, true),
    ])
    {
        SqlMigrationItem = new(@"
        create or replace view ""V_CrcV2_GroupMemberships"" as
            select t.""blockNumber""
                 , t.timestamp
                 , t.""transactionIndex""
                 , t.""logIndex""
                 , t.""transactionHash""
                 , t.truster as ""group""
                 , t.trustee as ""member""
                 , t.""expiryTime""
            from ""V_CrcV2_TrustRelations"" t
            join ""CrcV2_RegisterGroup"" g on t.truster = g.""group""
            join ""V_CrcV2_Avatars"" a on a.""avatar"" = t.trustee;
        ")
    };

    public static readonly EventSchema V_CrcV2_TrustRelations = new("V_CrcV2", "TrustRelations", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("batchIndex", ValueTypes.Int, true, true),
        new("transactionHash", ValueTypes.String, true),
        new("trustee", ValueTypes.Address, true),
        new("truster", ValueTypes.Address, true),
        new("expiryTime", ValueTypes.BigInt, true),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
        create or replace view ""V_CrcV2_TrustRelations"" as
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   ""trustee"",
                   ""truster"",
                   ""expiryTime""
            from (
                     select ""blockNumber"",
                            ""timestamp"",
                            ""transactionIndex"",
                            ""logIndex"",
                            ""transactionHash"",
                            ""truster"",
                            ""trustee"",
                            ""expiryTime"",
                            row_number() over (partition by ""truster"", ""trustee"" order by ""blockNumber"" desc, ""transactionIndex"" desc, ""logIndex"" desc) as ""rn""
                     from ""CrcV2_Trust""
                 ) t
            where ""rn"" = 1
              and ""expiryTime"" > (select max(""timestamp"") from ""System_Block"")
            order by ""blockNumber"" desc, ""transactionIndex"" desc, ""logIndex"" desc;    
        ")
    };

    public static readonly EventSchema V_Crc_TrustRelations = new("V_Crc", "TrustRelations", new byte[32], [
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

    public static readonly EventSchema V_Crc_Avatars = new("V_Crc", "Avatars", new byte[32], [
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

    public static readonly EventSchema V_Crc_Transfers = new("V_Crc", "Transfers",
        new byte[32],
        [
            new("blockNumber", ValueTypes.Int, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true),
            new("logIndex", ValueTypes.Int, true),
            new("batchIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("version", ValueTypes.Int, false),
            new("operator", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("id", ValueTypes.BigInt, true),
            new("value", ValueTypes.BigInt, false)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
        create or replace view ""V_Crc_Transfers"" as
            with ""allTransfers"" as (select ""blockNumber"",
                                           ""timestamp"",
                                           ""transactionIndex"",
                                           ""logIndex"",
                                           0        as ""batchIndex"",
                                           ""transactionHash"",
                                           1        as ""version"",
                                           null     as ""operator"",
                                           ""from"",
                                           ""to"",
                                           ""tokenAddress"" as ""id"",
                                           ""amount"" as ""value""
                                    from ""V_CrcV1_Transfers""
                                    union all
                                    select ""blockNumber"",
                                           ""timestamp"",
                                           ""transactionIndex"",
                                           ""logIndex"",
                                           ""batchIndex"",
                                           ""transactionHash"",
                                           2 as ""version"",
                                           ""operator"",
                                           ""from"",
                                           ""to"",
                                           ""id"",
                                           ""value""
                                    from ""V_CrcV2_Transfers"")
            select *
            from ""allTransfers"";
        ")
    };

    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; } =
        new Dictionary<(string Namespace, string Table), EventSchema>
        {
            {
                ("V_CrcV1", "TrustRelations"),
                V_CrcV1_TrustRelations
            },
            {
                ("V_CrcV1", "Avatars"),
                V_CrcV1_Avatars
            },
            {
                ("V_CrcV1", "Transfers"),
                V_CrcV1_Transfers
            },
            {
                ("V_CrcV2", "Avatars"),
                V_CrcV2_Avatars
            },
            {
                ("V_CrcV2", "Transfers"),
                V_CrcV2_Transfers
            },
            {
                ("V_CrcV2", "TrustRelations"),
                V_CrcV2_TrustRelations
            },
            {
                ("V_CrcV2", "GroupMemberships"),
                V_CrcV2_GroupMemberships
            },
            {
                ("V_Crc", "Avatars"),
                V_Crc_Avatars
            },
            {
                ("V_Crc", "TrustRelations"),
                V_Crc_TrustRelations
            },
            {
                ("V_Crc", "Transfers"),
                V_Crc_Transfers
            }
        };
}