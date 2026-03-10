-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- group:ValueTypes.Address:true
-- type:ValueTypes.String:true
-- owner:ValueTypes.Address:true
-- mintPolicy:ValueTypes.Address:true
-- mintHandler:ValueTypes.Address:true
-- treasury:ValueTypes.Address:true
-- service:ValueTypes.Address:true
-- feeCollection:ValueTypes.Address:true
-- memberCount:ValueTypes.Int:true
-- name:ValueTypes.String:true
-- symbol:ValueTypes.String:true
-- cidV0Digest:ValueTypes.Bytes:true
-- erc20WrapperDemurraged:ValueTypes.Address:true
-- erc20WrapperStatic:ValueTypes.Address:true

CREATE OR REPLACE VIEW "V_CrcV2_Groups" AS
WITH watermark AS (
    SELECT COALESCE(MAX("blockNumber"), 0) AS wm FROM "M_CrcV2_Groups"
),
-- All groups affected by ANY change since watermark
affected AS (
    SELECT "group" FROM "CrcV2_RegisterGroup" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION SELECT truster FROM "CrcV2_Trust" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION SELECT emitter FROM "CrcV2_BaseGroupOwnerUpdated" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION SELECT emitter FROM "CrcV2_BaseGroupServiceUpdated" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION SELECT emitter FROM "CrcV2_BaseGroupFeeCollectionUpdated" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION SELECT avatar FROM "CrcV2_UpdateMetadataDigest" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION SELECT avatar FROM "CrcV2_ERC20WrapperDeployed" WHERE "blockNumber" > (SELECT wm FROM watermark)
),
-- Live re-computation for affected groups only
live_groups AS (
    SELECT
        c."blockNumber",
        c."timestamp",
        c."transactionIndex",
        c."logIndex",
        c."transactionHash",
        c."group",
        CASE WHEN cm.proxy IS NOT NULL
                THEN 'CrcV2_CMGroupCreated'
             WHEN bg."group" IS NOT NULL
                THEN 'CrcV2_BaseGroupCreated'
             ELSE 'CrcV2_RegisterGroup'
        END AS type,
        COALESCE(cm.owner, lo.owner) AS owner,
        c."mint" AS "mintPolicy",
        COALESCE(cm."mintHandler", bg."mintHandler") AS "mintHandler",
        c.treasury,
        ls."newService" AS service,
        lf."feeCollection",
        COALESCE(mc."memberCount", 0) AS "memberCount",
        c.name,
        c.symbol,
        lci."metadataDigest" AS "cidV0Digest",
        ed."erc20WrapperDemurraged",
        es."erc20WrapperStatic"
    FROM "CrcV2_RegisterGroup" c
    LEFT JOIN LATERAL (
        SELECT owner FROM "CrcV2_BaseGroupOwnerUpdated"
        WHERE emitter = c."group"
        ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC LIMIT 1
    ) lo ON true
    LEFT JOIN LATERAL (
        SELECT "newService" FROM "CrcV2_BaseGroupServiceUpdated"
        WHERE emitter = c."group"
        ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC LIMIT 1
    ) ls ON true
    LEFT JOIN LATERAL (
        SELECT "feeCollection" FROM "CrcV2_BaseGroupFeeCollectionUpdated"
        WHERE emitter = c."group"
        ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC LIMIT 1
    ) lf ON true
    LEFT JOIN LATERAL (
        SELECT "metadataDigest" FROM "CrcV2_UpdateMetadataDigest"
        WHERE avatar = c."group"
        ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC LIMIT 1
    ) lci ON true
    LEFT JOIN LATERAL (
        SELECT count(*) AS "memberCount"
        FROM (
            SELECT ct.trustee,
                   row_number() OVER (
                       PARTITION BY ct.trustee
                       ORDER BY ct."blockNumber" DESC, ct."transactionIndex" DESC, ct."logIndex" DESC
                   ) AS rn,
                   ct."expiryTime"
            FROM "CrcV2_Trust" ct
            WHERE ct.truster = c."group"
        ) sub
        WHERE sub.rn = 1
          AND sub."expiryTime" > COALESCE((SELECT max("timestamp") FROM "System_Block"), 0)::numeric
    ) mc ON true
    LEFT JOIN LATERAL (
        SELECT ed."erc20Wrapper" AS "erc20WrapperDemurraged"
        FROM "CrcV2_ERC20WrapperDeployed" ed
        WHERE ed."circlesType" = 0 AND ed.avatar = c."group" LIMIT 1
    ) ed ON true
    LEFT JOIN LATERAL (
        SELECT ed."erc20Wrapper" AS "erc20WrapperStatic"
        FROM "CrcV2_ERC20WrapperDeployed" ed
        WHERE ed."circlesType" = 1 AND ed.avatar = c."group" LIMIT 1
    ) es ON true
    LEFT JOIN "CrcV2_CMGroupCreated" cm ON cm.proxy = c."group"
    LEFT JOIN "CrcV2_BaseGroupCreated" bg ON bg."group" = c."group"
    WHERE c."group" IN (SELECT "group" FROM affected)
)
-- Unaffected: straight from matview
SELECT m.* FROM "M_CrcV2_Groups" m WHERE m."group" NOT IN (SELECT "group" FROM affected)
UNION ALL
-- Affected: live re-computation
SELECT * FROM live_groups;
