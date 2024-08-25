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
         create or replace view public.""V_CrcV2_Avatars""
                    (""blockNumber"", timestamp, ""transactionIndex"", ""logIndex"", ""transactionHash"", type, ""invitedBy"", avatar,
                     ""tokenId"", name, ""cidV0Digest"")
        as
            WITH avatars AS (
                SELECT ""CrcV2_RegisterOrganization"".""blockNumber"",
                       ""CrcV2_RegisterOrganization"".""timestamp"",
                       ""CrcV2_RegisterOrganization"".""transactionIndex"",
                       ""CrcV2_RegisterOrganization"".""logIndex"",
                       ""CrcV2_RegisterOrganization"".""transactionHash"",
                       'organization'::text AS type,
                       NULL::text AS ""invitedBy"",
                       ""CrcV2_RegisterOrganization"".organization AS avatar,
                       NULL::text AS ""tokenId"",
                       ""CrcV2_RegisterOrganization"".name
                FROM ""CrcV2_RegisterOrganization""
                UNION ALL
                SELECT ""CrcV2_RegisterGroup"".""blockNumber"",
                       ""CrcV2_RegisterGroup"".""timestamp"",
                       ""CrcV2_RegisterGroup"".""transactionIndex"",
                       ""CrcV2_RegisterGroup"".""logIndex"",
                       ""CrcV2_RegisterGroup"".""transactionHash"",
                       'group'::text AS type,
                       NULL::text AS ""invitedBy"",
                       ""CrcV2_RegisterGroup"".""group"" AS avatar,
                       ""CrcV2_RegisterGroup"".""group"" AS ""tokenId"",
                       ""CrcV2_RegisterGroup"".name
                FROM ""CrcV2_RegisterGroup""
                UNION ALL
                SELECT ""CrcV2_RegisterHuman"".""blockNumber"",
                       ""CrcV2_RegisterHuman"".""timestamp"",
                       ""CrcV2_RegisterHuman"".""transactionIndex"",
                       ""CrcV2_RegisterHuman"".""logIndex"",
                       ""CrcV2_RegisterHuman"".""transactionHash"",
                       'human'::text AS type,
                       NULL::text AS ""invitedBy"",
                       ""CrcV2_RegisterHuman"".avatar,
                       ""CrcV2_RegisterHuman"".avatar AS ""tokenId"",
                       NULL::text AS name
                FROM ""CrcV2_RegisterHuman""
                UNION ALL
                SELECT ""CrcV2_InviteHuman"".""blockNumber"",
                       ""CrcV2_InviteHuman"".""timestamp"",
                       ""CrcV2_InviteHuman"".""transactionIndex"",
                       ""CrcV2_InviteHuman"".""logIndex"",
                       ""CrcV2_InviteHuman"".""transactionHash"",
                       'human'::text AS type,
                       ""CrcV2_InviteHuman"".inviter AS ""invitedBy"",
                       ""CrcV2_InviteHuman"".invited,
                       ""CrcV2_InviteHuman"".invited AS ""tokenId"",
                       NULL::text AS name
                FROM ""CrcV2_InviteHuman""
            )
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
                     LEFT JOIN (
                SELECT cid_1.avatar,
                       cid_1.""metadataDigest"" AS ""cidV0Digest"",
                       ROW_NUMBER() OVER (PARTITION BY cid_1.avatar ORDER BY cid_1.""blockNumber"" DESC, cid_1.""transactionIndex"" DESC, cid_1.""logIndex"" DESC) as rn
                FROM ""CrcV2_UpdateMetadataDigest"" cid_1
            ) cid ON cid.avatar = a.avatar AND cid.rn = 1;
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
        CREATE OR REPLACE VIEW ""V_CrcV2_Groups"" AS
            WITH LatestMetadata AS (
                SELECT
                    u.avatar,
                    u.""metadataDigest"",
                    u.""blockNumber"",
                    u.""transactionIndex"",
                    u.""logIndex"",
                    ROW_NUMBER() OVER (PARTITION BY u.avatar ORDER BY u.""blockNumber"" DESC, u.""transactionIndex"" DESC, u.""logIndex"" DESC) as rn
                FROM ""CrcV2_UpdateMetadataDigest"" u
            )
            SELECT
                g.""blockNumber"",
                g.""timestamp"",
                g.""transactionIndex"",
                g.""logIndex"",
                g.""transactionHash"",
                g.""group"",
                g.mint,
                g.treasury,
                g.name,
                g.symbol,
                lm.""metadataDigest"" as ""cidV0Digest""
            FROM ""CrcV2_RegisterGroup"" g
            JOIN LatestMetadata lm ON g.""group"" = lm.avatar
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
            }
        };
}