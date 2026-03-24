WITH registered_avatars AS MATERIALIZED (
    SELECT organization AS avatar FROM "CrcV2_RegisterOrganization"
    UNION ALL
    SELECT "group" AS avatar FROM "CrcV2_RegisterGroup"
    UNION ALL
    SELECT avatar FROM "CrcV2_RegisterHuman"
)
SELECT "timestamp", "from", "to", "tokenAddress", "value", "isWrapped", "isStatic"
FROM (
    SELECT
        "blockNumber",
        "transactionIndex",
        "logIndex",
        -1 AS "batchIndex",
        "timestamp",
        "from",
        "to",
        "tokenAddress",
        "value"::text AS "value",
        false AS "isWrapped",
        false AS "isStatic"
    FROM "CrcV2_TransferSingle"
    WHERE "blockNumber" > @lastBlock

    UNION ALL

    SELECT
        "blockNumber",
        "transactionIndex",
        "logIndex",
        "batchIndex",
        "timestamp",
        "from",
        "to",
        "tokenAddress",
        "value"::text AS "value",
        false AS "isWrapped",
        false AS "isStatic"
    FROM "CrcV2_TransferBatch"
    WHERE "blockNumber" > @lastBlock

    UNION ALL

    SELECT
        t."blockNumber",
        t."transactionIndex",
        t."logIndex",
        -1 AS "batchIndex",
        t."timestamp",
        t."from",
        t."to",
        t."tokenAddress",
        t."amount"::text AS "value",
        true AS "isWrapped",
        (d."circlesType" = 1) AS "isStatic"
    FROM "CrcV2_Erc20WrapperTransfer" t
    JOIN "CrcV2_ERC20WrapperDeployed" d ON d."erc20Wrapper" = t."tokenAddress"
    JOIN registered_avatars a ON a.avatar = d.avatar
    WHERE t."blockNumber" > @lastBlock
) deltas
ORDER BY "blockNumber", "transactionIndex", "logIndex", "batchIndex";
