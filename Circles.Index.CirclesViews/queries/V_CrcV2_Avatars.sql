-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- type:ValueTypes.String:false
-- invitedBy:ValueTypes.String:false
-- avatar:ValueTypes.String:false
-- tokenId:ValueTypes.String:false
-- name:ValueTypes.String:false
-- cidV0Digest:ValueTypes.Bytes:false

create or replace view public."V_CrcV2_Avatars"
        ("blockNumber", timestamp, "transactionIndex", "logIndex",
            "transactionHash", type, "invitedBy", avatar, "tokenId", name, "cidV0Digest") as
WITH 
avatars AS (
    SELECT "CrcV2_RegisterOrganization"."blockNumber",
        "CrcV2_RegisterOrganization"."timestamp",
        "CrcV2_RegisterOrganization"."transactionIndex",
        "CrcV2_RegisterOrganization"."logIndex",
        "CrcV2_RegisterOrganization"."transactionHash",
        NULL::text                                AS "invitedBy",
        "CrcV2_RegisterOrganization".organization AS avatar,
        NULL::text                                AS "tokenId",
        "CrcV2_RegisterOrganization".name,
        'CrcV2_RegisterOrganization'              as type
    FROM "CrcV2_RegisterOrganization"
    UNION ALL
    SELECT "CrcV2_RegisterGroup"."blockNumber",
        "CrcV2_RegisterGroup"."timestamp",
        "CrcV2_RegisterGroup"."transactionIndex",
        "CrcV2_RegisterGroup"."logIndex",
        "CrcV2_RegisterGroup"."transactionHash",
        NULL::text                    AS "invitedBy",
        "CrcV2_RegisterGroup"."group" AS avatar,
        "CrcV2_RegisterGroup"."group" AS "tokenId",
        "CrcV2_RegisterGroup".name,
        'CrcV2_RegisterGroup'         as type
    FROM "CrcV2_RegisterGroup"
    UNION ALL
    SELECT "CrcV2_RegisterHuman"."blockNumber",
        "CrcV2_RegisterHuman"."timestamp",
        "CrcV2_RegisterHuman"."transactionIndex",
        "CrcV2_RegisterHuman"."logIndex",
        "CrcV2_RegisterHuman"."transactionHash",
        NULL::text                   AS "invitedBy",
        "CrcV2_RegisterHuman".avatar,
        "CrcV2_RegisterHuman".avatar AS "tokenId",
        NULL::text                   AS name,
        'CrcV2_RegisterHuman'        as type
    FROM "CrcV2_RegisterHuman"
)
SELECT a."blockNumber",
        a."timestamp",
        a."transactionIndex",
        a."logIndex",
        a."transactionHash",
        a.type,
        a."invitedBy",
        a.avatar,
        a."tokenId",
        a.name,
        cid."cidV0Digest"
FROM avatars a
LEFT JOIN (
    SELECT 
        cid_1.avatar,
        cid_1."metadataDigest"  AS "cidV0Digest",
        row_number() OVER (PARTITION BY cid_1.avatar ORDER BY cid_1."blockNumber" DESC, cid_1."transactionIndex" DESC, cid_1."logIndex" DESC) AS rn
    FROM "CrcV2_UpdateMetadataDigest" cid_1
) cid 
ON cid.avatar = a.avatar AND cid.rn = 1;