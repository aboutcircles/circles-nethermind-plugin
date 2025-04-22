create or replace view public."V_CrcV2_Groups"
    ("blockNumber", timestamp, "transactionIndex", "logIndex", "transactionHash", 
    "group", mint, treasury, name, symbol, "cidV0Digest", "memberCount", "trustedCount") as
WITH 
latestmetadata AS (
    SELECT 
        u.avatar,
        u."metadataDigest",
        u."blockNumber",
        u."transactionIndex",
        u."logIndex",
        row_number()
        OVER (PARTITION BY u.avatar ORDER BY u."blockNumber" DESC, u."transactionIndex" DESC, u."logIndex" DESC) AS rn
    FROM "CrcV2_UpdateMetadataDigest" u
)
SELECT 
    g."blockNumber",
    g."timestamp",
    g."transactionIndex",
    g."logIndex",
    g."transactionHash",
    g."group",
    g.mint,
    g.treasury,
    g.name,
    g.symbol,
    lm."metadataDigest"       AS "cidV0Digest",
    count("outTrust".trustee) AS "memberCount",
    count("inTrust".truster)  AS "trustedCount"
FROM "CrcV2_RegisterGroup" g
JOIN latestmetadata lm ON g."group" = lm.avatar
LEFT JOIN "V_CrcV2_TrustRelations" "outTrust" on "outTrust".truster = g."group"
LEFT JOIN "V_CrcV2_TrustRelations" "inTrust" on "inTrust".trustee = g."group"
WHERE lm.rn = 1
GROUP BY 
    g."blockNumber",
    g."timestamp",
    g."transactionIndex",
    g."logIndex",
    g."transactionHash",
    g."group",
    g.mint,
    g.treasury,
    g.name,
    g.symbol,
    lm."metadataDigest";