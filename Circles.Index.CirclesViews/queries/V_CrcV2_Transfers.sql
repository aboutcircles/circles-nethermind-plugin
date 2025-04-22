create or replace view public."V_CrcV2_Transfers"
        ("blockNumber", timestamp, "transactionIndex", "logIndex", 
        "batchIndex", "transactionHash", operator,"from", "to", id, value, type) AS
WITH 
"allTransfers" AS (
    SELECT "CrcV2_TransferSingle"."blockNumber",
            "CrcV2_TransferSingle"."timestamp",
            "CrcV2_TransferSingle"."transactionIndex",
            "CrcV2_TransferSingle"."logIndex",
            0                               AS "batchIndex",
            "CrcV2_TransferSingle"."transactionHash",
            "CrcV2_TransferSingle".operator,
            "CrcV2_TransferSingle"."from",
            "CrcV2_TransferSingle"."to",
            "CrcV2_TransferSingle".id::text AS id,
            "CrcV2_TransferSingle".value,
            'CrcV2_TransferSingle'::text    AS type,
            "tokenAddress"
    FROM "CrcV2_TransferSingle"
    UNION ALL
    SELECT "CrcV2_TransferBatch"."blockNumber",
            "CrcV2_TransferBatch"."timestamp",
            "CrcV2_TransferBatch"."transactionIndex",
            "CrcV2_TransferBatch"."logIndex",
            "CrcV2_TransferBatch"."batchIndex",
            "CrcV2_TransferBatch"."transactionHash",
            "CrcV2_TransferBatch".operator,
            "CrcV2_TransferBatch"."from",
            "CrcV2_TransferBatch"."to",
            "CrcV2_TransferBatch".id::text AS id,
            "CrcV2_TransferBatch".value,
            'CrcV2_TransferBatch'::text    AS type,
            "tokenAddress"
    FROM "CrcV2_TransferBatch"
    UNION ALL
    SELECT "CrcV2_Erc20WrapperTransfer"."blockNumber",
            "CrcV2_Erc20WrapperTransfer"."timestamp",
            "CrcV2_Erc20WrapperTransfer"."transactionIndex",
            "CrcV2_Erc20WrapperTransfer"."logIndex",
            0                                           AS "batchIndex",
            "CrcV2_Erc20WrapperTransfer"."transactionHash",
            NULL::text                                  AS operator,
            "CrcV2_Erc20WrapperTransfer"."from",
            "CrcV2_Erc20WrapperTransfer"."to",
            "CrcV2_Erc20WrapperTransfer"."tokenAddress" AS id,
            "CrcV2_Erc20WrapperTransfer".amount         AS value,
            'CrcV2_Erc20WrapperTransfer'::text          AS type,
            "tokenAddress"
    FROM "CrcV2_Erc20WrapperTransfer"
)
SELECT 
    t."blockNumber",
    t."timestamp",
    t."transactionIndex",
    t."logIndex",
    t."batchIndex",
    t."transactionHash",
    t.operator,
    t."from",
    t."to",
    t.id,
    t.value,
    t.type,
    t."tokenAddress",
    tt.type as "tokenType"
FROM "allTransfers" t
JOIN "V_Crc_Tokens" tt on tt.token = t."tokenAddress"
ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC, "batchIndex" DESC;