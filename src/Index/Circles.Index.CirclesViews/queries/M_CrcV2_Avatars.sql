-- Materialized version of V_CrcV2_Avatars.
-- V_CrcV2_Avatars is a 3-way UNION with a LATERAL subquery for metadata CID lookup.
-- It is the most heavily-used view, called by: searchProfiles, getTokenInfo,
-- getAvatarInfo, circles_query, and many view joins.
--
-- Materializing it avoids recomputing the UNION + LATERAL on every query.
-- The regular view V_CrcV2_Avatars is retained for compatibility but consumers
-- that don't need real-time data (like searchProfiles) should prefer this matview.
--
-- Refreshed periodically by NetworkStateUpdaterService.

CREATE MATERIALIZED VIEW IF NOT EXISTS "M_CrcV2_Avatars"
    ("blockNumber", "timestamp", "transactionIndex", "logIndex",
     "transactionHash", type, "invitedBy", avatar, "tokenId", name, "cidV0Digest") AS
WITH
avatars AS (
    SELECT "blockNumber", "timestamp", "transactionIndex", "logIndex",
           "transactionHash",
           NULL::text AS "invitedBy",
           organization AS avatar,
           NULL::text AS "tokenId",
           name,
           'CrcV2_RegisterOrganization' AS type
    FROM "CrcV2_RegisterOrganization"
    UNION ALL
    SELECT "blockNumber", "timestamp", "transactionIndex", "logIndex",
           "transactionHash",
           NULL::text AS "invitedBy",
           "group" AS avatar,
           "group" AS "tokenId",
           name,
           'CrcV2_RegisterGroup' AS type
    FROM "CrcV2_RegisterGroup"
    UNION ALL
    SELECT "blockNumber", "timestamp", "transactionIndex", "logIndex",
           "transactionHash",
           NULL::text AS "invitedBy",
           avatar,
           avatar AS "tokenId",
           NULL::text AS name,
           'CrcV2_RegisterHuman' AS type
    FROM "CrcV2_RegisterHuman"
)
SELECT a."blockNumber", a."timestamp", a."transactionIndex", a."logIndex",
       a."transactionHash", a.type, a."invitedBy", a.avatar, a."tokenId",
       a.name, cid."cidV0Digest"
FROM avatars a
LEFT JOIN LATERAL (
    SELECT "metadataDigest" AS "cidV0Digest"
    FROM "CrcV2_UpdateMetadataDigest" m
    WHERE m.avatar = a.avatar
    ORDER BY m."blockNumber" DESC, m."transactionIndex" DESC, m."logIndex" DESC
    LIMIT 1
) cid ON true;

-- Unique index required for REFRESH MATERIALIZED VIEW CONCURRENTLY
CREATE UNIQUE INDEX IF NOT EXISTS "idx_M_CrcV2_Avatars_pk"
    ON "M_CrcV2_Avatars" (avatar);

-- Lookup indexes for common query patterns
CREATE INDEX IF NOT EXISTS "idx_M_CrcV2_Avatars_type"
    ON "M_CrcV2_Avatars" (type);

CREATE INDEX IF NOT EXISTS "idx_M_CrcV2_Avatars_cidV0Digest"
    ON "M_CrcV2_Avatars" ("cidV0Digest")
    WHERE "cidV0Digest" IS NOT NULL;
