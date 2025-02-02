using Circles.Index.Common;

namespace Circles.Index.CirclesViews;

public class DatabaseSchema : IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; } = new SchemaPropertyMap();

    public IEventDtoTableMap EventDtoTableMap { get; } = new EventDtoTableMap();

    public static readonly EventSchema Crc_UITransferHistory = new("Crc", "UITransferHistory", new byte[32], [
        new EventFieldSchema("blockNumber", ValueTypes.Int, true),
        new EventFieldSchema("transactionIndex", ValueTypes.Int, true),
        new EventFieldSchema("logIndex", ValueTypes.Int, true),
        new EventFieldSchema("batchIndex", ValueTypes.Int, true, true),
        new EventFieldSchema("type", ValueTypes.String, true),
        new EventFieldSchema("timestamp", ValueTypes.Int, true),
        new EventFieldSchema("transactionHash", ValueTypes.String, true),
        new EventFieldSchema("tokenAddress", ValueTypes.Address, true),
        new EventFieldSchema("tokenType", ValueTypes.String, true),
        new EventFieldSchema("from", ValueTypes.Address, true),
        new EventFieldSchema("to", ValueTypes.Address, true),
        new EventFieldSchema("amount", ValueTypes.BigInt, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            CREATE TABLE IF NOT EXISTS ""Crc_UITransferHistory"" (
                                                                         ""blockNumber""       BIGINT          NOT NULL,
                                                                         ""transactionIndex""  INT             NOT NULL,
                                                                         ""logIndex""          INT             NOT NULL,
                                                                         ""batchIndex""        INT             NOT NULL DEFAULT 0,
                                                                         ""type""              TEXT            NOT NULL,
                                                                         ""timestamp""         BIGINT          NOT NULL,
                                                                         ""transactionHash""   TEXT            NOT NULL,
                                                                         ""from""              TEXT            NOT NULL,
                                                                         ""to""                TEXT            NOT NULL,
                                                                         ""amount""            NUMERIC(78,0)   NOT NULL DEFAULT 0,
                                                                         CONSTRAINT pk_crc_summary
                                                                             PRIMARY KEY (""blockNumber"", ""transactionIndex"", ""logIndex"", ""batchIndex"")
            );

            CREATE INDEX IF NOT EXISTS idx_crc_summary_from_desc
                ON ""Crc_UITransferHistory"" (
                                                  ""from"",
                                                  ""blockNumber"" DESC,
                                                  ""transactionIndex"" DESC,
                                                  ""logIndex"" DESC,
                                                  ""batchIndex"" DESC
                    );

            CREATE INDEX IF NOT EXISTS idx_crc_summary_to_desc
                ON ""Crc_UITransferHistory"" (
                                                  ""to"",
                                                  ""blockNumber"" DESC,
                                                  ""transactionIndex"" DESC,
                                                  ""logIndex"" DESC,
                                                  ""batchIndex"" DESC
                    );


            CREATE OR REPLACE PROCEDURE ""Update_Crc_UITransferHistory""()
                LANGUAGE plpgsql
            AS $$
            DECLARE
                last_block_number BIGINT;
            BEGIN
                -- Get the latest block number we've already processed in the summary table
                SELECT COALESCE(MAX(""blockNumber""), 0)
                INTO last_block_number
                FROM ""Crc_UITransferHistory"";

                -- Insert new rows from your union query
                INSERT INTO ""Crc_UITransferHistory"" (
                    ""blockNumber"",
                    ""transactionIndex"",
                    ""logIndex"",
                    ""batchIndex"",
                    ""type"",
                    ""timestamp"",
                    ""transactionHash"",
                    ""from"",
                    ""to"",
                    ""amount""
                )
                WITH v1_user_transfers AS (
                    SELECT ""blockNumber"",
                           ""transactionIndex"",
                           ""logIndex"",
                           ""timestamp"",
                           ""transactionHash"",
                           ""from"",
                           ""to"",
                           ""amount""
                    FROM ""CrcV1_HubTransfer""
                ),
                     v2_user_transfers AS (
                         SELECT ""blockNumber"",
                                ""transactionIndex"",
                                ""logIndex"",
                                ""timestamp"",
                                MIN(""transactionHash"") AS ""transactionHash"",
                                MIN(""operator"") AS ""operator"",
                                MIN(""from"") AS ""from"",
                                MIN(""to"") AS ""to"",
                                SUM(""amount"") AS ""amount""
                         FROM ""CrcV2_StreamCompleted""
                         GROUP BY ""blockNumber"",""transactionIndex"",""logIndex"",""timestamp""
                     ),
                     v1_raw_transfers AS (
                         SELECT t.""blockNumber"",
                                t.""transactionIndex"",
                                t.""logIndex"",
                                t.""timestamp"",
                                t.""transactionHash"",
                                t.""tokenAddress"",
                                t.""from"",
                                t.""to"",
                                t.""amount""
                         FROM ""CrcV1_Transfer"" t
                                  LEFT JOIN ""CrcV1_HubTransfer"" ht
                                            ON ht.""transactionHash"" = t.""transactionHash""
                         WHERE ht.""transactionHash"" IS NULL
                     ),
                     v2_all_raw_transfers AS (
                         SELECT ""blockNumber"",
                                ""transactionIndex"",
                                ""logIndex"",
                                0 AS ""batchIndex"",
                                'CrcV2_TransferSingle' AS ""type"",
                                ""timestamp"",
                                ""transactionHash"",
                                ""operator"",
                                ""from"",
                                ""to"",
                                ""id"",
                                ""value"",
                                ""tokenAddress""
                         FROM ""CrcV2_TransferSingle""
                         UNION ALL
                         SELECT ""blockNumber"",
                                ""transactionIndex"",
                                ""logIndex"",
                                ""batchIndex"",
                                'CrcV2_TransferBatch' AS ""type"",
                                ""timestamp"",
                                ""transactionHash"",
                                ""operator"",
                                ""from"",
                                ""to"",
                                ""id"",
                                ""value"",
                                ""tokenAddress""
                         FROM ""CrcV2_TransferBatch""
                     ),
                     v2_raw_transfers AS (
                         SELECT rt.""blockNumber"",
                                rt.""transactionIndex"",
                                rt.""logIndex"",
                                rt.""batchIndex"",
                                rt.""type"",
                                rt.""timestamp"",
                                rt.""transactionHash"",
                                rt.""operator"",
                                rt.""from"",
                                rt.""to"",
                                rt.""id"",
                                rt.""value"",
                                rt.""tokenAddress""
                         FROM v2_all_raw_transfers rt
                                  LEFT JOIN ""CrcV2_StreamCompleted"" st
                                            ON rt.""transactionHash"" = st.""transactionHash""
                         WHERE st.""transactionHash"" IS NULL
                     ),
                     all_transfers AS (
                         SELECT ""blockNumber"",
                                ""transactionIndex"",
                                ""logIndex"",
                                0 AS ""batchIndex"",
                                'CrcV1_HubTransfer' AS ""type"",
                                ""timestamp"",
                                ""transactionHash"",
                                ""from"",
                                ""to"",
                                ""amount""
                         FROM v1_user_transfers

                         UNION ALL

                         SELECT ""blockNumber"",
                                ""transactionIndex"",
                                ""logIndex"",
                                0 AS ""batchIndex"",
                                'CrcV1_Transfer' AS ""type"",
                                ""timestamp"",
                                ""transactionHash"",
                                ""from"",
                                ""to"",
                                ""amount""
                         FROM v1_raw_transfers

                         UNION ALL

                         SELECT ""blockNumber"",
                                ""transactionIndex"",
                                ""logIndex"",
                                0 AS ""batchIndex"",
                                'CrcV2_StreamCompleted' AS ""type"",
                                ""timestamp"",
                                ""transactionHash"",
                                ""from"",
                                ""to"",
                                ""amount""
                         FROM v2_user_transfers

                         UNION ALL

                         SELECT ""blockNumber"",
                                ""transactionIndex"",
                                ""logIndex"",
                                ""batchIndex"",
                                ""type"",
                                ""timestamp"",
                                ""transactionHash"",
                                ""from"",
                                ""to"",
                                ""value"" AS ""amount""
                         FROM v2_raw_transfers
                     )
                SELECT
                    ""blockNumber"",
                    ""transactionIndex"",
                    ""logIndex"",
                    ""batchIndex"",
                    ""type"",
                    ""timestamp"",
                    ""transactionHash"",
                    ""from"",
                    ""to"",
                    ""amount""
                FROM all_transfers
                WHERE ""blockNumber"" > last_block_number
                ON CONFLICT (""blockNumber"", ""transactionIndex"", ""logIndex"", ""batchIndex"")
                    DO NOTHING;
            END;
            $$;
        ")
    };

    public static readonly EventSchema V_CrcV2_TotalSupply = new("V_CrcV2", "TotalSupply", new byte[32], [
        new("tokenAddress", ValueTypes.Address, true),
        new("tokenId", ValueTypes.BigInt, true),
        new("totalSupply", ValueTypes.BigInt, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view ""V_CrcV2_TotalSupply"" 
            as
               with combined_transfers as (
                   select
                       ""tokenAddress"",
                       id,
                       ""from"",
                       ""to"",
                       value
                   from ""CrcV2_TransferSingle""
                   union all
                   select
                       ""tokenAddress"",
                       id,
                       ""from"",
                       ""to"",
                       value
                   from ""CrcV2_TransferBatch""
               )
               select
                   ""tokenAddress"",
                   ""id"" as ""tokenId"",
                   sum(
                           case
                               when ""from"" = '0x0000000000000000000000000000000000000000' then value
                               when ""to""   = '0x0000000000000000000000000000000000000000' then -value
                               else 0
                               end
                   ) as total_supply
               from combined_transfers
               group by
                   ""tokenAddress"",
                   ""tokenId"";
         ")
    };

    public static readonly EventSchema V_CrcV1_TotalSupply = new("V_CrcV1", "TotalSupply", new byte[32], [
        new("tokenAddress", ValueTypes.Address, true),
        new("user", ValueTypes.Address, true),
        new("totalSupply", ValueTypes.BigInt, false),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
                 create or replace view ""V_CrcV1_TotalSupply""
       as
       with t as (
           select
               ""tokenAddress"",
               SUM(
                       case
                           when ""from"" = '0x0000000000000000000000000000000000000000' then amount
                           when ""to""   = '0x0000000000000000000000000000000000000000' then -amount
                           else 0
                           end
               ) as ""totalSupply""
           from ""CrcV1_Transfer""
           group by ""tokenAddress""
       )
       select t.""tokenAddress"",
              s.""user"",
              t.""totalSupply""
       from ""t""
       join ""CrcV1_Signup"" s on ""s"".""token"" = t.""tokenAddress"";")
    };

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
            new("type", ValueTypes.String, true),
            new("tokenType", ValueTypes.String, true)
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view public.""V_CrcV1_Transfers""
                    (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", ""from"", ""to"", ""tokenAddress"",
                     amount, type)
            as
            WITH ""allTransfers"" AS (SELECT ""CrcV1_HubTransfer"".""blockNumber"",
                                       ""CrcV1_HubTransfer"".""timestamp"",
                                       ""CrcV1_HubTransfer"".""transactionIndex"",
                                       ""CrcV1_HubTransfer"".""logIndex"",
                                       ""CrcV1_HubTransfer"".""transactionHash"",
                                       ""CrcV1_HubTransfer"".""from"",
                                       ""CrcV1_HubTransfer"".""to"",
                                       NULL::text                AS ""tokenAddress"",
                                       ""CrcV1_HubTransfer"".amount,
                                       'CrcV1_HubTransfer'::text AS type
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
                                       'CrcV1_Transfer'::text AS type
                                FROM ""CrcV1_Transfer"" t)
            SELECT t.""blockNumber"",
               t.""timestamp"",
               t.""transactionIndex"",
               t.""logIndex"",
               t.""transactionHash"",
               t.""from"",
               t.""to"",
               t.""tokenAddress"",
               t.amount,
               t.type,
               tt.type as ""tokenType""
            FROM ""allTransfers"" t
            LEFT JOIN ""V_Crc_Tokens"" tt on tt.token = t.""tokenAddress""
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
            new("type", ValueTypes.String, true),
            new("tokenType", ValueTypes.String, true)
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
                                           'CrcV2_TransferSingle'::text    AS type,
                                           ""tokenAddress""
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
                                           'CrcV2_TransferBatch'::text    AS type,
                                           ""tokenAddress""
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
                                           'CrcV2_Erc20WrapperTransfer'::text          AS type,
                                           ""tokenAddress""
                                    FROM ""CrcV2_Erc20WrapperTransfer"")
            SELECT t.""blockNumber"",
                   t.""timestamp"",
                   t.""transactionIndex"",
                   t.""logIndex"",
                   t.""batchIndex"",
                   t.""transactionHash"",
                   t.operator,
                   t.""from"",
                   t.""to"",
                   t.id,
                   t.value,
                   t.type,
                   t.""tokenAddress"",
                   tt.type as ""tokenType""
            FROM ""allTransfers"" t
            JOIN ""V_Crc_Tokens"" tt on tt.token = t.""tokenAddress""
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
            new("type", ValueTypes.String, true),
            new("tokenType", ValueTypes.String, true)
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
                                           ""V_CrcV1_Transfers"".type,
                                           ""V_CrcV1_Transfers"".""tokenType""
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
                                           ""V_CrcV2_Transfers"".type,
                                           ""V_CrcV2_Transfers"".""tokenType""
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
                   type,
                   ""tokenType""
            FROM ""allTransfers"" t;
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
        new("memberCount", ValueTypes.Int, true),
        new("trustedCount", ValueTypes.Int, true),
    ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view public.""V_CrcV2_Groups""
                        (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", ""group"", mint, treasury, name,
                         symbol, ""cidV0Digest"", ""memberCount"", ""trustedCount"")
            as
                WITH latestmetadata AS (
                    SELECT u.avatar,
                           u.""metadataDigest"",
                           u.""blockNumber"",
                           u.""transactionIndex"",
                           u.""logIndex"",
                           row_number()
                           OVER (PARTITION BY u.avatar ORDER BY u.""blockNumber"" DESC, u.""transactionIndex"" DESC, u.""logIndex"" DESC) AS rn
                    FROM ""CrcV2_UpdateMetadataDigest"" u
                )
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
                       lm.""metadataDigest""       AS ""cidV0Digest"",
                       count(""outTrust"".trustee) AS ""memberCount"",
                       count(""inTrust"".truster)  AS ""trustedCount""
                FROM ""CrcV2_RegisterGroup"" g
                    JOIN latestmetadata lm ON g.""group"" = lm.avatar
                    LEFT JOIN ""V_CrcV2_TrustRelations"" ""outTrust"" on ""outTrust"".truster = g.""group""
                    LEFT JOIN ""V_CrcV2_TrustRelations"" ""inTrust"" on ""inTrust"".trustee = g.""group""
                WHERE lm.rn = 1
                GROUP BY g.""blockNumber"",
                         g.""timestamp"",
                         g.""transactionIndex"",
                         g.""logIndex"",
                         g.""transactionHash"",
                         g.""group"",
                         g.mint,
                         g.treasury,
                         g.name,
                         g.symbol,
                         lm.""metadataDigest"";
        ")
    };

    public static readonly EventSchema V_CrcV1_BalancesByAccountAndToken = new("V_CrcV1", "BalancesByAccountAndToken",
        new byte[32],
        [
            new("account", ValueTypes.Address, true),
            new("tokenId", ValueTypes.String, true),
            new("tokenAddress", ValueTypes.String, true),
            new("lastActivity", ValueTypes.Int, true),
            new("totalBalance", ValueTypes.BigInt, true),
        ])
    {
        SqlMigrationItem = new SqlMigrationItem(@"
            create or replace view public.""V_CrcV1_BalancesByAccountAndToken""(account, ""tokenAddress"", ""lastActivity"", ""totalBalance"", ""tokenOwner"") as
            WITH transfers AS (SELECT ""CrcV1_Transfer"".""timestamp"",
                                      ""CrcV1_Transfer"".""from"",
                                      ""CrcV1_Transfer"".""to"",
                                      ""CrcV1_Transfer"".amount AS value,
                                      ""CrcV1_Transfer"".""tokenAddress""
                               FROM ""CrcV1_Transfer""),
                 ""accountBalances"" AS (SELECT all_transfers.account,
                                              sum(all_transfers.amount)      AS balance,
                                              max(all_transfers.""timestamp"") AS ""timestamp"",
                                              all_transfers.""tokenAddress""
                                       FROM (SELECT transfers.""from""  AS account,
                                                    - transfers.value AS amount,
                                                    transfers.""timestamp"",
                                                    transfers.""tokenAddress""
                                             FROM transfers
                                             UNION ALL
                                             SELECT transfers.""to""  AS account,
                                                    transfers.value AS amount,
                                                    transfers.""timestamp"",
                                                    transfers.""tokenAddress""
                                             FROM transfers) all_transfers
                                       GROUP BY all_transfers.account, all_transfers.""tokenAddress"")
            SELECT ""accountBalances"".account,
                   ""accountBalances"".""tokenAddress"",
                   ""accountBalances"".""timestamp"" AS ""lastActivity"",
                   ""accountBalances"".balance     AS ""totalBalance"",
                   ""CrcV1_Signup"".""user""         AS ""tokenOwner""
            FROM ""accountBalances""
                     JOIN ""CrcV1_Signup"" ON ""accountBalances"".""tokenAddress"" = ""CrcV1_Signup"".token
            WHERE ""accountBalances"".account <> '0x0000000000000000000000000000000000000000'::text
              AND ""accountBalances"".balance > 0::numeric;
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

            create or replace view public.""V_CrcV2_BalancesByAccountAndToken""
                        (account, ""tokenId"", ""tokenAddress"", ""lastActivity"", ""demurragedTotalBalance"") as
            WITH transfers AS (SELECT ""CrcV2_TransferSingle"".""timestamp"",
                                      ""CrcV2_TransferSingle"".""from"",
                                      ""CrcV2_TransferSingle"".""to"",
                                      ""CrcV2_TransferSingle"".id,
                                      ""CrcV2_TransferSingle"".value,
                                      ""CrcV2_TransferSingle"".""tokenAddress""
                               FROM ""CrcV2_TransferSingle""
                               UNION ALL
                               SELECT ""CrcV2_TransferBatch"".""timestamp"",
                                      ""CrcV2_TransferBatch"".""from"",
                                      ""CrcV2_TransferBatch"".""to"",
                                      ""CrcV2_TransferBatch"".id,
                                      ""CrcV2_TransferBatch"".value,
                                      ""CrcV2_TransferBatch"".""tokenAddress""
                               FROM ""CrcV2_TransferBatch""),
                 ""accountBalances"" AS (SELECT all_transfers.account,
                                              all_transfers.id,
                                              sum(all_transfers.amount)      AS balance,
                                              max(all_transfers.""timestamp"") AS ""timestamp"",
                                              all_transfers.""tokenAddress""
                                       FROM (SELECT transfers.""from""  AS account,
                                                    transfers.id,
                                                    - transfers.value AS amount,
                                                    transfers.""timestamp"",
                                                    transfers.""tokenAddress""
                                             FROM transfers
                                             UNION ALL
                                             SELECT transfers.""to""  AS account,
                                                    transfers.id,
                                                    transfers.value AS amount,
                                                    transfers.""timestamp"",
                                                    transfers.""tokenAddress""
                                             FROM transfers) all_transfers
                                       GROUP BY all_transfers.account, all_transfers.id, all_transfers.""tokenAddress"")
            SELECT account,
                   id::text                                                       AS ""tokenId"",
                   ""tokenAddress"",
                   ""timestamp""                                                    AS ""lastActivity"",
                   floor(crc_demurrage(1675209600::bigint, ""timestamp"", balance)) AS ""demurragedTotalBalance""
            FROM ""accountBalances""
            WHERE account <> '0x0000000000000000000000000000000000000000'::text
              AND balance > 0::numeric;
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
        SqlMigrationItem = new(@"
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
            where ""circlesType"" = 1
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
            where ""circlesType"" = 0;
        ")
    };

    public static readonly EventSchema V_Crc_Stats = new("V_Crc", "Stats", new byte[32], [
        new("measure", ValueTypes.String, false),
        new("value", ValueTypes.Int, false)
    ])
    {
        SqlMigrationItem = new(@"
        create or replace view ""V_Crc_Stats""(""measure"", ""value"") 
        as
            select 'avatar_count_v1' as measure, count(""user"") as value
            from ""V_CrcV1_Avatars""
            union all
            select 'organization_count_v1' as measure, count(""user"") as value
            from ""V_CrcV1_Avatars""
            where token is null
            union all
            select 'human_count_v1' as measure, count(""user"") as value
            from ""V_CrcV1_Avatars""
            where token is not null
            union all
            select 'avatar_count_v2', count(""avatar"")
            from ""V_CrcV2_Avatars""
            union all
            select 'organization_count_v2', count(organization)
            from ""CrcV2_RegisterOrganization""
            union all
            select 'human_count_v2', count(avatar)
            from ""CrcV2_RegisterHuman""
            union all
            select 'group_count_v2', count(""group"")
            from ""CrcV2_RegisterGroup""
            union all
            select 'trust_count_v1',
                   (SELECT COUNT(*)
                    FROM (SELECT DISTINCT ON (""user"", ""canSendTo"") ""user"", ""canSendTo"", ""limit""
                          FROM ""CrcV1_Trust""
                          ORDER BY ""user"", ""canSendTo"", ""blockNumber"" DESC, ""transactionIndex"" DESC, ""logIndex"" DESC) t
                    WHERE ""limit"" > 0)
            union all
            select 'trust_count_v2', count(*)
            from ""V_CrcV2_TrustRelations""
            union all
            select 'token_count_v1', count(*)
            from ""V_Crc_Tokens""
            where version = 1
            union all
            select 'token_count_v2', count(*)
            from ""V_Crc_Tokens""
            where version = 2
            union all
            select 'transitive_transfer_count_v1', count(*)
            from ""CrcV1_HubTransfer""
            union all
            select 'transitive_transfer_count_v2', count(*)
            from ""CrcV2_StreamCompleted""
            union all 
            select 'circles_transfer_count_v1', count(*)
            from ""CrcV1_Transfer""
            union all 
            select 'circles_transfer_count_v2', (
                select sum(t.value) from (
                  select count(*) as value
                  from ""CrcV2_TransferSingle""
                  union all
                  select count(*)
                  from ""CrcV2_TransferBatch""
                ) as t
            )
            union all 
            select 'erc20_wrapper_token_count_v2', count(*)
            from ""CrcV2_ERC20WrapperDeployed"";
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
                ("V_CrcV2", "Avatars"),
                V_CrcV2_Avatars
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
                ("V_Crc", "Tokens"),
                V_Crc_Tokens
            },
            {
                ("V_CrcV1", "Transfers"),
                V_CrcV1_Transfers
            },
            {
                ("V_CrcV2", "Transfers"),
                V_CrcV2_Transfers
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
                ("V_CrcV1", "BalancesByAccountAndToken"),
                V_CrcV1_BalancesByAccountAndToken
            },
            {
                ("V_CrcV2", "BalancesByAccountAndToken"),
                V_CrcV2_BalancesByAccountAndToken
            },
            {
                ("V_Crc", "Stats"),
                V_Crc_Stats
            },
            {
                ("V_CrcV1", "TotalSupply"),
                V_CrcV1_TotalSupply
            },
            {
                ("V_CrcV2", "TotalSupply"),
                V_CrcV2_TotalSupply
            },
            {
                ("Crc", "UITransferHistory"),
                Crc_UITransferHistory
            }
        };
}