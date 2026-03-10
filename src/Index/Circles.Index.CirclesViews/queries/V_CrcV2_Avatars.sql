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

CREATE OR REPLACE VIEW public."V_CrcV2_Avatars"
        ("blockNumber", timestamp, "transactionIndex", "logIndex",
            "transactionHash", type, "invitedBy", avatar, "tokenId", name, "cidV0Digest") AS
WITH watermark AS (
    SELECT COALESCE(MAX("blockNumber"), 0) AS wm FROM "M_CrcV2_Avatars"
),
-- Metadata updates since last refresh for EXISTING matview avatars
delta_cids AS (
    SELECT DISTINCT ON (avatar) avatar, "metadataDigest" AS "cidV0Digest"
    FROM "CrcV2_UpdateMetadataDigest"
    WHERE "blockNumber" > (SELECT wm FROM watermark)
    ORDER BY avatar, "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
),
-- New registrations since last refresh
new_avatars AS (
    SELECT "blockNumber", "timestamp", "transactionIndex", "logIndex",
           "transactionHash",
           NULL::text AS "invitedBy",
           organization AS avatar,
           NULL::text AS "tokenId",
           name,
           'CrcV2_RegisterOrganization' AS type
    FROM "CrcV2_RegisterOrganization"
    WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION ALL
    SELECT "blockNumber", "timestamp", "transactionIndex", "logIndex",
           "transactionHash",
           NULL::text AS "invitedBy",
           "group" AS avatar,
           "group" AS "tokenId",
           name,
           'CrcV2_RegisterGroup' AS type
    FROM "CrcV2_RegisterGroup"
    WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION ALL
    SELECT "blockNumber", "timestamp", "transactionIndex", "logIndex",
           "transactionHash",
           NULL::text AS "invitedBy",
           avatar,
           avatar AS "tokenId",
           NULL::text AS name,
           'CrcV2_RegisterHuman' AS type
    FROM "CrcV2_RegisterHuman"
    WHERE "blockNumber" > (SELECT wm FROM watermark)
)
-- Existing matview rows with CID overrides where applicable
SELECT m."blockNumber", m."timestamp", m."transactionIndex", m."logIndex",
       m."transactionHash", m.type, m."invitedBy", m.avatar, m."tokenId", m.name,
       COALESCE(dc."cidV0Digest", m."cidV0Digest") AS "cidV0Digest"
FROM "M_CrcV2_Avatars" m
LEFT JOIN delta_cids dc ON dc.avatar = m.avatar
UNION ALL
-- New registrations since last refresh (with full CID lookup)
SELECT a."blockNumber", a."timestamp", a."transactionIndex", a."logIndex",
       a."transactionHash", a.type, a."invitedBy", a.avatar, a."tokenId", a.name,
       cid."cidV0Digest"
FROM new_avatars a
LEFT JOIN LATERAL (
    SELECT "metadataDigest" AS "cidV0Digest"
    FROM "CrcV2_UpdateMetadataDigest" m
    WHERE m.avatar = a.avatar
    ORDER BY m."blockNumber" DESC, m."transactionIndex" DESC, m."logIndex" DESC
    LIMIT 1
) cid ON true;
