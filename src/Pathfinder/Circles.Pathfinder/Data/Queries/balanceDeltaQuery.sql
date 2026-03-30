WITH registered_avatars AS MATERIALIZED (
{{registered_avatars_cte_body}})
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
        ts."tokenAddress",
        "value"::text AS "value",
        false AS "isWrapped",
        false AS "isStatic"
    FROM "CrcV2_TransferSingle" ts
    JOIN registered_avatars ra_token ON ra_token.avatar = ts."tokenAddress"
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
        tb."tokenAddress",
        "value"::text AS "value",
        false AS "isWrapped",
        false AS "isStatic"
    FROM "CrcV2_TransferBatch" tb
    JOIN registered_avatars ra_token ON ra_token.avatar = tb."tokenAddress"
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
