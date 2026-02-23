SELECT "truster", "trustee", "expiryTime"::bigint, "blockNumber", "transactionIndex", "logIndex"
FROM (
    SELECT *, ROW_NUMBER() OVER (
        PARTITION BY "truster", "trustee"
        ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
    ) AS rn
    FROM "CrcV2_Trust"
) t WHERE rn = 1
