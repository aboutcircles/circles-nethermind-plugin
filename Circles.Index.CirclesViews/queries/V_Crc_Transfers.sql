create or replace view public."V_Crc_Transfers"
    ("blockNumber", timestamp, "transactionIndex", "logIndex", "batchIndex", 
    "transactionHash", version, operator, "from", "to", id, value, type) as
WITH 
"allTransfers" AS (
    SELECT 
        "V_CrcV1_Transfers"."blockNumber",
        "V_CrcV1_Transfers"."timestamp",
        "V_CrcV1_Transfers"."transactionIndex",
        "V_CrcV1_Transfers"."logIndex",
        0                                  AS "batchIndex",
        "V_CrcV1_Transfers"."transactionHash",
        1                                  AS version,
        NULL::text                         AS operator,
        "V_CrcV1_Transfers"."from",
        "V_CrcV1_Transfers"."to",
        "V_CrcV1_Transfers"."tokenAddress" AS id,
        "V_CrcV1_Transfers".amount         AS value,
        "V_CrcV1_Transfers".type,
        "V_CrcV1_Transfers"."tokenType"
    FROM "V_CrcV1_Transfers"
    UNION ALL
    SELECT 
        "V_CrcV2_Transfers"."blockNumber",
        "V_CrcV2_Transfers"."timestamp",
        "V_CrcV2_Transfers"."transactionIndex",
        "V_CrcV2_Transfers"."logIndex",
        "V_CrcV2_Transfers"."batchIndex",
        "V_CrcV2_Transfers"."transactionHash",
        2 AS version,
        "V_CrcV2_Transfers".operator,
        "V_CrcV2_Transfers"."from",
        "V_CrcV2_Transfers"."to",
        "V_CrcV2_Transfers".id,
        "V_CrcV2_Transfers".value,
        "V_CrcV2_Transfers".type,
        "V_CrcV2_Transfers"."tokenType"
    FROM "V_CrcV2_Transfers"
)
SELECT 
    "blockNumber",
    "timestamp",
    "transactionIndex",
    "logIndex",
    "batchIndex",
    "transactionHash",
    version,
    operator,
    "from",
    "to",
    id,
    value,
    type,
    "tokenType"
FROM "allTransfers" t;