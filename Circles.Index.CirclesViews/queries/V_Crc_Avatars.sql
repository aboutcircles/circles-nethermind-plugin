create or replace view "V_Crc_Avatars"
    ("blockNumber", timestamp, "transactionIndex", "logIndex", "transactionHash", 
    version, type, "invitedBy", avatar, "tokenId", name, "cidV0Digest") as
SELECT 
    "V_CrcV2_Avatars"."blockNumber",
    "V_CrcV2_Avatars"."timestamp",
    "V_CrcV2_Avatars"."transactionIndex",
    "V_CrcV2_Avatars"."logIndex",
    "V_CrcV2_Avatars"."transactionHash",
    2 AS version,
    "V_CrcV2_Avatars".type,
    "V_CrcV2_Avatars"."invitedBy",
    "V_CrcV2_Avatars".avatar,
    "V_CrcV2_Avatars"."tokenId",
    "V_CrcV2_Avatars".name,
    "V_CrcV2_Avatars"."cidV0Digest"
FROM "V_CrcV2_Avatars"
UNION ALL
SELECT 
    "V_CrcV1_Avatars"."blockNumber",
    "V_CrcV1_Avatars"."timestamp",
    "V_CrcV1_Avatars"."transactionIndex",
    "V_CrcV1_Avatars"."logIndex",
    "V_CrcV1_Avatars"."transactionHash",
    1                        AS version,
    "V_CrcV1_Avatars".type,
    NULL::text               AS "invitedBy",
    "V_CrcV1_Avatars"."user" AS avatar,
    "V_CrcV1_Avatars".token  AS "tokenId",
    NULL::text               AS name,
    "cidV0Digest"            AS "cidV0Digest"
FROM "V_CrcV1_Avatars";