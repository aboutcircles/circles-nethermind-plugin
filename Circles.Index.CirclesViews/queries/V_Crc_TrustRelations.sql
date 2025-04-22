create or replace view public."V_Crc_TrustRelations"
    ("blockNumber", timestamp, "transactionIndex", "logIndex", "transactionHash", 
    version, trustee, truster, "expiryTime", "limit") as

SELECT 
    "V_CrcV2_TrustRelations"."blockNumber",
    "V_CrcV2_TrustRelations"."timestamp",
    "V_CrcV2_TrustRelations"."transactionIndex",
    "V_CrcV2_TrustRelations"."logIndex",
    "V_CrcV2_TrustRelations"."transactionHash",
    2             AS version,
    "V_CrcV2_TrustRelations".trustee,
    "V_CrcV2_TrustRelations".truster,
    "V_CrcV2_TrustRelations"."expiryTime",
    NULL::numeric AS "limit"
FROM "V_CrcV2_TrustRelations"
UNION ALL
SELECT 
    "V_CrcV1_TrustRelations"."blockNumber",
    "V_CrcV1_TrustRelations"."timestamp",
    "V_CrcV1_TrustRelations"."transactionIndex",
    "V_CrcV1_TrustRelations"."logIndex",
    "V_CrcV1_TrustRelations"."transactionHash",
    1                                    AS version,
    "V_CrcV1_TrustRelations"."user"      AS trustee,
    "V_CrcV1_TrustRelations"."canSendTo" AS truster,
    NULL::numeric                        AS "expiryTime",
    "V_CrcV1_TrustRelations"."limit"
FROM "V_CrcV1_TrustRelations";