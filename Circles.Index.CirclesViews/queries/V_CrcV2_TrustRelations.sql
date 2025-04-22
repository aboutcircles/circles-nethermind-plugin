create or replace view public."V_CrcV2_TrustRelations"
    ("blockNumber", timestamp, "transactionIndex", "logIndex", "transactionHash", trustee, truster,
        "expiryTime") as
SELECT 
    t."blockNumber",
    t."timestamp",
    t."transactionIndex",
    t."logIndex",
    t."transactionHash",
    trustee,
    truster,
    "expiryTime"
FROM (
    SELECT 
        "CrcV2_Trust"."blockNumber",
        "CrcV2_Trust"."timestamp",
        "CrcV2_Trust"."transactionIndex",
        "CrcV2_Trust"."logIndex",
        "CrcV2_Trust"."transactionHash",
        "CrcV2_Trust".truster,
        "CrcV2_Trust".trustee,
        "CrcV2_Trust"."expiryTime",
        row_number()
        OVER (PARTITION BY "CrcV2_Trust".truster, "CrcV2_Trust".trustee ORDER BY "CrcV2_Trust"."blockNumber" DESC, "CrcV2_Trust"."transactionIndex" DESC, "CrcV2_Trust"."logIndex" DESC) AS rn
    FROM "CrcV2_Trust"
) t
WHERE rn = 1
    AND "expiryTime" > ((SELECT max("System_Block"."timestamp") AS max
                        FROM "System_Block"))::numeric
ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;