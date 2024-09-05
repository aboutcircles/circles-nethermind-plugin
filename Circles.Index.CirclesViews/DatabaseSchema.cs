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
            create or replace view public.""V_CrcV1_TrustRelations""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", ""user"", ""canSendTo"",
                         ""limit"") as
            SELECT ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   ""user"",
                   ""canSendTo"",
                   ""limit""
            FROM (SELECT ""CrcV1_Trust"".""blockNumber"",
                         ""CrcV1_Trust"".""timestamp"",
                         ""CrcV1_Trust"".""transactionIndex"",
                         ""CrcV1_Trust"".""logIndex"",
                         ""CrcV1_Trust"".""transactionHash"",
                         ""CrcV1_Trust"".""user"",
                         ""CrcV1_Trust"".""canSendTo"",
                         ""CrcV1_Trust"".""limit"",
                         row_number()
                         OVER (PARTITION BY ""CrcV1_Trust"".""user"", ""CrcV1_Trust"".""canSendTo"" ORDER BY ""CrcV1_Trust"".""blockNumber"" DESC, ""CrcV1_Trust"".""transactionIndex"" DESC, ""CrcV1_Trust"".""logIndex"" DESC) AS rn
                  FROM ""CrcV1_Trust"") t
            WHERE rn = 1
              AND ""limit"" > 0::numeric
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC;
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
            create or replace view public.""V_CrcV1_Avatars""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", type, ""user"", token) as
            SELECT ""CrcV1_Signup"".""blockNumber"",
                   ""CrcV1_Signup"".""timestamp"",
                   ""CrcV1_Signup"".""transactionIndex"",
                   ""CrcV1_Signup"".""logIndex"",
                   ""CrcV1_Signup"".""transactionHash"",
                   'CrcV1_Signup'::text AS type,
                   ""CrcV1_Signup"".""user"",
                   ""CrcV1_Signup"".token
            FROM ""CrcV1_Signup""
            UNION ALL
            SELECT ""CrcV1_OrganizationSignup"".""blockNumber"",
                   ""CrcV1_OrganizationSignup"".""timestamp"",
                   ""CrcV1_OrganizationSignup"".""transactionIndex"",
                   ""CrcV1_OrganizationSignup"".""logIndex"",
                   ""CrcV1_OrganizationSignup"".""transactionHash"",
                   'CrcV1_OrganizationSignup'::text        AS type,
                   ""CrcV1_OrganizationSignup"".organization AS ""user"",
                   NULL::text                              AS token
            FROM ""CrcV1_OrganizationSignup"";
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
            new("amount", ValueTypes.BigInt, false),
            new("type", ValueTypes.String, false)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view public.""V_CrcV1_Transfers""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", ""from"", ""to"", ""tokenAddress"",
                         amount, type) as
            WITH ""allTransfers"" AS (SELECT ""CrcV1_HubTransfer"".""blockNumber"",
                                           ""CrcV1_HubTransfer"".""timestamp"",
                                           ""CrcV1_HubTransfer"".""transactionIndex"",
                                           ""CrcV1_HubTransfer"".""logIndex"",
                                           ""CrcV1_HubTransfer"".""transactionHash"",
                                           ""CrcV1_HubTransfer"".""from"",
                                           ""CrcV1_HubTransfer"".""to"",
                                           NULL::text AS ""tokenAddress"",
                                           ""CrcV1_HubTransfer"".amount,
                                           'CrcV1_HubTransfer' as type
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
                                           t.amount,
                                           'CrcV1_Transfer' as type
                                    FROM ""CrcV1_Transfer"" t)
            SELECT ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   ""from"",
                   ""to"",
                   ""tokenAddress"",
                   amount,
                   type
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
            create or replace view public.""V_CrcV2_Avatars""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", type, ""invitedBy"", avatar,
                         ""tokenId"", name, ""cidV0Digest"")
            as
            WITH avatars AS (SELECT ""CrcV2_RegisterOrganization"".""blockNumber"",
                                    ""CrcV2_RegisterOrganization"".""timestamp"",
                                    ""CrcV2_RegisterOrganization"".""transactionIndex"",
                                    ""CrcV2_RegisterOrganization"".""logIndex"",
                                    ""CrcV2_RegisterOrganization"".""transactionHash"",
                                    NULL::text                                AS ""invitedBy"",
                                    ""CrcV2_RegisterOrganization"".organization AS avatar,
                                    NULL::text                                AS ""tokenId"",
                                    ""CrcV2_RegisterOrganization"".name,
                                    'CrcV2_RegisterOrganization'              as type
                             FROM ""CrcV2_RegisterOrganization""
                             UNION ALL
                             SELECT ""CrcV2_RegisterGroup"".""blockNumber"",
                                    ""CrcV2_RegisterGroup"".""timestamp"",
                                    ""CrcV2_RegisterGroup"".""transactionIndex"",
                                    ""CrcV2_RegisterGroup"".""logIndex"",
                                    ""CrcV2_RegisterGroup"".""transactionHash"",
                                    NULL::text                    AS ""invitedBy"",
                                    ""CrcV2_RegisterGroup"".""group"" AS avatar,
                                    ""CrcV2_RegisterGroup"".""group"" AS ""tokenId"",
                                    ""CrcV2_RegisterGroup"".name,
                                    'CrcV2_RegisterGroup'         as type
                             FROM ""CrcV2_RegisterGroup""
                             UNION ALL
                             SELECT ""CrcV2_RegisterHuman"".""blockNumber"",
                                    ""CrcV2_RegisterHuman"".""timestamp"",
                                    ""CrcV2_RegisterHuman"".""transactionIndex"",
                                    ""CrcV2_RegisterHuman"".""logIndex"",
                                    ""CrcV2_RegisterHuman"".""transactionHash"",
                                    NULL::text                   AS ""invitedBy"",
                                    ""CrcV2_RegisterHuman"".avatar,
                                    ""CrcV2_RegisterHuman"".avatar AS ""tokenId"",
                                    NULL::text                   AS name,
                                    'CrcV2_RegisterHuman'        as type
                             FROM ""CrcV2_RegisterHuman"")
            SELECT a.""blockNumber"",
                   a.""timestamp"",
                   a.""transactionIndex"",
                   a.""logIndex"",
                   a.""transactionHash"",
                   a.type,
                   a.""invitedBy"",
                   a.avatar,
                   a.""tokenId"",
                   a.name,
                   cid.""cidV0Digest""
            FROM avatars a
                     LEFT JOIN (SELECT cid_1.avatar,
                                       cid_1.""metadataDigest""                                                                                                   AS ""cidV0Digest"",
                                       row_number()
                                       OVER (PARTITION BY cid_1.avatar ORDER BY cid_1.""blockNumber"" DESC, cid_1.""transactionIndex"" DESC, cid_1.""logIndex"" DESC) AS rn
                                FROM ""CrcV2_UpdateMetadataDigest"" cid_1) cid ON cid.avatar = a.avatar AND cid.rn = 1;
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
            new("value", ValueTypes.BigInt, false),
            new("type", ValueTypes.String, false)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
          create or replace view public.""V_CrcV2_Transfers""
                    (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""batchIndex"", ""transactionHash"", operator,
                     ""from"", ""to"", id, value, type)
            as
            WITH ""allTransfers"" AS (SELECT ""CrcV2_TransferSingle"".""blockNumber"",
                                           ""CrcV2_TransferSingle"".""timestamp"",
                                           ""CrcV2_TransferSingle"".""transactionIndex"",
                                           ""CrcV2_TransferSingle"".""logIndex"",
                                           0                               AS ""batchIndex"",
                                           ""CrcV2_TransferSingle"".""transactionHash"",
                                           ""CrcV2_TransferSingle"".operator,
                                           ""CrcV2_TransferSingle"".""from"",
                                           ""CrcV2_TransferSingle"".""to"",
                                           ""CrcV2_TransferSingle"".id::text AS id,
                                           ""CrcV2_TransferSingle"".value,
                                           'CrcV2_TransferSingle'::text AS type
                                    FROM ""CrcV2_TransferSingle""
                                    UNION ALL
                                    SELECT ""CrcV2_TransferBatch"".""blockNumber"",
                                           ""CrcV2_TransferBatch"".""timestamp"",
                                           ""CrcV2_TransferBatch"".""transactionIndex"",
                                           ""CrcV2_TransferBatch"".""logIndex"",
                                           ""CrcV2_TransferBatch"".""batchIndex"",
                                           ""CrcV2_TransferBatch"".""transactionHash"",
                                           ""CrcV2_TransferBatch"".operator,
                                           ""CrcV2_TransferBatch"".""from"",
                                           ""CrcV2_TransferBatch"".""to"",
                                           ""CrcV2_TransferBatch"".id::text AS id,
                                           ""CrcV2_TransferBatch"".value,
                                           'CrcV2_TransferBatch'::text AS type
                                    FROM ""CrcV2_TransferBatch""
                                    UNION ALL
                                    SELECT ""CrcV2_Erc20WrapperTransfer"".""blockNumber"",
                                           ""CrcV2_Erc20WrapperTransfer"".""timestamp"",
                                           ""CrcV2_Erc20WrapperTransfer"".""transactionIndex"",
                                           ""CrcV2_Erc20WrapperTransfer"".""logIndex"",
                                           0                                           AS ""batchIndex"",
                                           ""CrcV2_Erc20WrapperTransfer"".""transactionHash"",
                                           NULL::text                                  AS operator,
                                           ""CrcV2_Erc20WrapperTransfer"".""from"",
                                           ""CrcV2_Erc20WrapperTransfer"".""to"",
                                           ""CrcV2_Erc20WrapperTransfer"".""tokenAddress"" AS id,
                                           ""CrcV2_Erc20WrapperTransfer"".amount         AS value,
                                           'CrcV2_Erc20WrapperTransfer'::text          AS type
                                    FROM ""CrcV2_Erc20WrapperTransfer"")
            SELECT ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""batchIndex"",
                   ""transactionHash"",
                   operator,
                   ""from"",
                   ""to"",
                   id,
                   value,
                   type
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
            create or replace view public.""V_CrcV2_GroupMemberships""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", ""group"", member,
                         ""expiryTime"", ""memberType"") as
            SELECT t.""blockNumber"",
                   t.""timestamp"",
                   t.""transactionIndex"",
                   t.""logIndex"",
                   t.""transactionHash"",
                   t.truster AS ""group"",
                   t.trustee AS member,
                   t.""expiryTime"",
                   a.type as ""memberType""
            FROM ""V_CrcV2_TrustRelations"" t
                     JOIN ""CrcV2_RegisterGroup"" g ON t.truster = g.""group""
                     JOIN ""V_CrcV2_Avatars"" a ON a.avatar = t.trustee;
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
            create or replace view public.""V_CrcV2_TrustRelations""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", trustee, truster,
                         ""expiryTime"") as
            SELECT t.""blockNumber"",
                   t.""timestamp"",
                   t.""transactionIndex"",
                   t.""logIndex"",
                   t.""transactionHash"",
                   trustee,
                   truster,
                   ""expiryTime""
            FROM (SELECT ""CrcV2_Trust"".""blockNumber"",
                         ""CrcV2_Trust"".""timestamp"",
                         ""CrcV2_Trust"".""transactionIndex"",
                         ""CrcV2_Trust"".""logIndex"",
                         ""CrcV2_Trust"".""transactionHash"",
                         ""CrcV2_Trust"".truster,
                         ""CrcV2_Trust"".trustee,
                         ""CrcV2_Trust"".""expiryTime"",
                         row_number()
                         OVER (PARTITION BY ""CrcV2_Trust"".truster, ""CrcV2_Trust"".trustee ORDER BY ""CrcV2_Trust"".""blockNumber"" DESC, ""CrcV2_Trust"".""transactionIndex"" DESC, ""CrcV2_Trust"".""logIndex"" DESC) AS rn
                  FROM ""CrcV2_Trust"") t
            WHERE rn = 1
              AND ""expiryTime"" > ((SELECT max(""System_Block"".""timestamp"") AS max
                                   FROM ""System_Block""))::numeric
            ORDER BY ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC;
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
            create or replace view public.""V_Crc_TrustRelations""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", version, trustee, truster,
                         ""expiryTime"", ""limit"")
            as
            SELECT ""V_CrcV2_TrustRelations"".""blockNumber"",
                   ""V_CrcV2_TrustRelations"".""timestamp"",
                   ""V_CrcV2_TrustRelations"".""transactionIndex"",
                   ""V_CrcV2_TrustRelations"".""logIndex"",
                   ""V_CrcV2_TrustRelations"".""transactionHash"",
                   2             AS version,
                   ""V_CrcV2_TrustRelations"".trustee,
                   ""V_CrcV2_TrustRelations"".truster,
                   ""V_CrcV2_TrustRelations"".""expiryTime"",
                   NULL::numeric AS ""limit""
            FROM ""V_CrcV2_TrustRelations""
            UNION ALL
            SELECT ""V_CrcV1_TrustRelations"".""blockNumber"",
                   ""V_CrcV1_TrustRelations"".""timestamp"",
                   ""V_CrcV1_TrustRelations"".""transactionIndex"",
                   ""V_CrcV1_TrustRelations"".""logIndex"",
                   ""V_CrcV1_TrustRelations"".""transactionHash"",
                   1                                    AS version,
                   ""V_CrcV1_TrustRelations"".""user""      AS trustee,
                   ""V_CrcV1_TrustRelations"".""canSendTo"" AS truster,
                   NULL::numeric                        AS ""expiryTime"",
                   ""V_CrcV1_TrustRelations"".""limit""
            FROM ""V_CrcV1_TrustRelations"";
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
            create or replace view public.""V_Crc_Avatars""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", version, type, ""invitedBy"",
                         avatar, ""tokenId"", name, ""cidV0Digest"")
            as
            SELECT ""V_CrcV2_Avatars"".""blockNumber"",
                   ""V_CrcV2_Avatars"".""timestamp"",
                   ""V_CrcV2_Avatars"".""transactionIndex"",
                   ""V_CrcV2_Avatars"".""logIndex"",
                   ""V_CrcV2_Avatars"".""transactionHash"",
                   2 AS version,
                   ""V_CrcV2_Avatars"".type,
                   ""V_CrcV2_Avatars"".""invitedBy"",
                   ""V_CrcV2_Avatars"".avatar,
                   ""V_CrcV2_Avatars"".""tokenId"",
                   ""V_CrcV2_Avatars"".name,
                   ""V_CrcV2_Avatars"".""cidV0Digest""
            FROM ""V_CrcV2_Avatars""
            UNION ALL
            SELECT ""V_CrcV1_Avatars"".""blockNumber"",
                   ""V_CrcV1_Avatars"".""timestamp"",
                   ""V_CrcV1_Avatars"".""transactionIndex"",
                   ""V_CrcV1_Avatars"".""logIndex"",
                   ""V_CrcV1_Avatars"".""transactionHash"",
                   1                        AS version,
                   ""V_CrcV1_Avatars"".type,
                   NULL::text               AS ""invitedBy"",
                   ""V_CrcV1_Avatars"".""user"" AS avatar,
                   ""V_CrcV1_Avatars"".token  AS ""tokenId"",
                   NULL::text               AS name,
                   NULL::bytea              AS ""cidV0Digest""
            FROM ""V_CrcV1_Avatars"";
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
            new("value", ValueTypes.BigInt, false),
            new("type", ValueTypes.String, false)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view public.""V_Crc_Transfers""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""batchIndex"", ""transactionHash"", version,
                         operator, ""from"", ""to"", id, value, type)
            as
            WITH ""allTransfers"" AS (SELECT ""V_CrcV1_Transfers"".""blockNumber"",
                                           ""V_CrcV1_Transfers"".""timestamp"",
                                           ""V_CrcV1_Transfers"".""transactionIndex"",
                                           ""V_CrcV1_Transfers"".""logIndex"",
                                           0                                  AS ""batchIndex"",
                                           ""V_CrcV1_Transfers"".""transactionHash"",
                                           1                                  AS version,
                                           NULL::text                         AS operator,
                                           ""V_CrcV1_Transfers"".""from"",
                                           ""V_CrcV1_Transfers"".""to"",
                                           ""V_CrcV1_Transfers"".""tokenAddress"" AS id,
                                           ""V_CrcV1_Transfers"".amount         AS value,
                                           ""V_CrcV1_Transfers"".type
                                    FROM ""V_CrcV1_Transfers""
                                    UNION ALL
                                    SELECT ""V_CrcV2_Transfers"".""blockNumber"",
                                           ""V_CrcV2_Transfers"".""timestamp"",
                                           ""V_CrcV2_Transfers"".""transactionIndex"",
                                           ""V_CrcV2_Transfers"".""logIndex"",
                                           ""V_CrcV2_Transfers"".""batchIndex"",
                                           ""V_CrcV2_Transfers"".""transactionHash"",
                                           2 AS version,
                                           ""V_CrcV2_Transfers"".operator,
                                           ""V_CrcV2_Transfers"".""from"",
                                           ""V_CrcV2_Transfers"".""to"",
                                           ""V_CrcV2_Transfers"".id,
                                           ""V_CrcV2_Transfers"".value,
                                           ""V_CrcV2_Transfers"".type
                                    FROM ""V_CrcV2_Transfers"")
            SELECT ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""batchIndex"",
                   ""transactionHash"",
                   version,
                   operator,
                   ""from"",
                   ""to"",
                   id,
                   value,
                   type
            FROM ""allTransfers"";
        ")
    };

    public static readonly EventSchema V_CrcV2_Groups = new("V_CrcV2", "Groups", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("group", ValueTypes.Address, true),
        new("mint", ValueTypes.Address, true),
        new("treasury", ValueTypes.Address, true),
        new("name", ValueTypes.String, true),
        new("symbol", ValueTypes.String, true),
        new("cidV0Digest", ValueTypes.Bytes, true),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view public.""V_CrcV2_Groups""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", ""group"", mint, treasury, name,
                         symbol, ""cidV0Digest"")
            as
            WITH latestmetadata AS (SELECT u.avatar,
                                           u.""metadataDigest"",
                                           u.""blockNumber"",
                                           u.""transactionIndex"",
                                           u.""logIndex"",
                                           row_number()
                                           OVER (PARTITION BY u.avatar ORDER BY u.""blockNumber"" DESC, u.""transactionIndex"" DESC, u.""logIndex"" DESC) AS rn
                                    FROM ""CrcV2_UpdateMetadataDigest"" u)
            SELECT g.""blockNumber"",
                   g.""timestamp"",
                   g.""transactionIndex"",
                   g.""logIndex"",
                   g.""transactionHash"",
                   g.""group"",
                   g.mint,
                   g.treasury,
                   g.name,
                   g.symbol,
                   lm.""metadataDigest"" AS ""cidV0Digest""
            FROM ""CrcV2_RegisterGroup"" g
                     JOIN latestmetadata lm ON g.""group"" = lm.avatar
            WHERE lm.rn = 1;
        ")
    };

    public static readonly EventSchema V_CrcV2_BalancesByAccountAndToken = new("V_CrcV2", "BalancesByAccountAndToken",
        new byte[32],
        [
            new("account", ValueTypes.Address, true),
            new("tokenId", ValueTypes.String, true),
            new("lastActivity", ValueTypes.Int, true),
            new("demurragedTotalBalance", ValueTypes.BigInt, true),
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            CREATE OR REPLACE FUNCTION crc_day(""inflationDayZero"" bigint, ""timestamp"" bigint)
                RETURNS bigint AS $$
            DECLARE
                DEMURRAGE_WINDOW bigint := 86400;
            BEGIN
                RETURN (""timestamp"" - ""inflationDayZero"") / DEMURRAGE_WINDOW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE OR REPLACE FUNCTION crc_demurrage(""inflationDayZero"" bigint, ""timestamp"" bigint, ""value"" numeric)
                RETURNS numeric AS $$
            DECLARE
                _day_last_interaction bigint;
                _now bigint := EXTRACT(EPOCH FROM NOW())::bigint;
                _day_now bigint;
                _gamma numeric := 0.9998013320085989574306481700129226782902039065082930593676448873;
            BEGIN
                _day_last_interaction := crc_day(""inflationDayZero"", ""timestamp"");
                _day_now := crc_day(""inflationDayZero"", _now);
                return (value * POWER(_gamma, _day_now - _day_last_interaction));
            END;
            $$ LANGUAGE plpgsql;

            create or replace view ""V_CrcV2_BalancesByAccountAndToken"" as
                WITH ""transfers"" AS (
                    SELECT ""timestamp"",
                           ""from"",
                           ""to"",
                           ""id"",
                           ""value""
                    FROM ""CrcV2_TransferSingle""
                    UNION ALL
                    SELECT ""timestamp"",
                           ""from"",
                           ""to"",
                           ""id"",
                           ""value""
                    FROM ""CrcV2_TransferBatch""
                ), ""accountBalances"" AS (
                    SELECT
                        account,
                        id,
                        SUM(amount) AS balance,
                        MAX(""timestamp"") AS ""timestamp""
                    FROM (
                             SELECT ""from"" AS account, id, -value AS amount, ""timestamp""
                             FROM ""transfers""
                             UNION ALL
                             SELECT ""to"" AS account, id, value AS amount, ""timestamp""
                             FROM ""transfers""
                         ) AS all_transfers
                    GROUP BY account, id
                )
                select account
                     , id::text as ""tokenId""
                     , ""accountBalances"".""timestamp"" as ""lastActivity""
                     , floor(crc_demurrage(1675209600, ""accountBalances"".""timestamp"", balance)) as ""demurragedTotalBalance""
                from ""accountBalances""
                    LEFT JOIN ""CrcV2_RegisterHuman"" hum ON hum.avatar = ""account""
                    LEFT JOIN ""CrcV2_InviteHuman"" hum2 ON hum2.invited = ""account""
                    LEFT JOIN ""CrcV2_RegisterOrganization"" org ON org.""organization"" = ""account""
                    LEFT JOIN ""CrcV2_RegisterGroup"" grp ON grp.""group"" = ""account""
                WHERE ""account"" != '0x0000000000000000000000000000000000000000'
                AND balance > 0;
        ")
    };

    public static readonly EventSchema V_Crc_Tokens = new("V_Crc", "Tokens", new byte[32], [
        new("blockNumber", ValueTypes.Int, true),
        new("timestamp", ValueTypes.Int, true),
        new("transactionIndex", ValueTypes.Int, true),
        new("logIndex", ValueTypes.Int, true),
        new("transactionHash", ValueTypes.String, true),
        new("version", ValueTypes.Int, false),
        new("type", ValueTypes.String, false),
        new("token", ValueTypes.String, true),
        new("tokenOwner", ValueTypes.String, true)
    ])
    {
        SqlMigrationItem = new (@"
        create or replace view ""V_Crc_Tokens""
        as
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   version,
                   type,
                   ""tokenId"" as token,
                   ""avatar"" as ""tokenOwner""
            from ""V_Crc_Avatars""
            where ""tokenId"" is not null
            union all 
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   2,
                   'CrcV2_ERC20WrapperDeployed_Inflationary' as type,
                   ""erc20Wrapper"" as token,
                   ""avatar"" as ""tokenOwner""
            from ""CrcV2_ERC20WrapperDeployed""
            where ""circlesType"" = 0
            union all
            select ""blockNumber"",
                   ""timestamp"",
                   ""transactionIndex"",
                   ""logIndex"",
                   ""transactionHash"",
                   2,
                   'CrcV2_ERC20WrapperDeployed_Demurraged' as type,
                   ""erc20Wrapper"" as token,
                   ""avatar"" as ""tokenOwner""
            from ""CrcV2_ERC20WrapperDeployed""
            where ""circlesType"" = 1;
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
            },
            {
                ("V_CrcV2", "Groups"),
                V_CrcV2_Groups
            },
            {
                ("V_CrcV2", "BalancesByAccountAndToken"),
                V_CrcV2_BalancesByAccountAndToken
            },
            {
                ("V_Crc", "Tokens"),
                V_Crc_Tokens
            }
        };
}