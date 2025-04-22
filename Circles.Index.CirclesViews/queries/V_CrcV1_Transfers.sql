create or replace view public."V_CrcV1_Transfers" (
    "blockNumber", timestamp, "transactionIndex", 
    "logIndex", "transactionHash", "from", "to", 
    "tokenAddress", amount, type) AS
WITH "allTransfers" AS (
    SELECT "CrcV1_HubTransfer"."blockNumber",
        "CrcV1_HubTransfer"."timestamp",
        "CrcV1_HubTransfer"."transactionIndex",
        "CrcV1_HubTransfer"."logIndex",
        "CrcV1_HubTransfer"."transactionHash",
        "CrcV1_HubTransfer"."from",
        "CrcV1_HubTransfer"."to",
        NULL::text                AS "tokenAddress",
        "CrcV1_HubTransfer".amount,
        'CrcV1_HubTransfer'::text AS type
    FROM "CrcV1_HubTransfer"
    UNION ALL
    SELECT t."blockNumber",
        t."timestamp",
        t."transactionIndex",
        t."logIndex",
        t."transactionHash",
        t."from",
        t."to",
        t."tokenAddress",
        t.amount,
        'CrcV1_Transfer'::text AS type
    FROM "CrcV1_Transfer" t
)

SELECT t."blockNumber",
    t."timestamp",
    t."transactionIndex",
    t."logIndex",
    t."transactionHash",
    t."from",
    t."to",
    t."tokenAddress",
    t.amount,
    t.type,
    tt.type as "tokenType"
FROM "allTransfers" t
LEFT JOIN "V_Crc_Tokens" tt on tt.token = t."tokenAddress"
ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;