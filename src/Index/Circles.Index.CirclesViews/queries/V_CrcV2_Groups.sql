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
CREATE OR REPLACE VIEW "V_CrcV2_Groups"
AS
    WITH latest_owner AS (
        SELECT DISTINCT ON (emitter) *
        FROM "CrcV2_BaseGroupOwnerUpdated"
        ORDER BY emitter,
            "blockNumber"      DESC,
            "transactionIndex" DESC,
            "logIndex"         DESC
    ),
    latest_service AS (
        SELECT DISTINCT ON (emitter) *
        FROM "CrcV2_BaseGroupServiceUpdated"
        ORDER BY emitter,
            "blockNumber"      DESC,
            "transactionIndex" DESC,
            "logIndex"         DESC
    ),
    latest_cid AS (
        SELECT DISTINCT ON (avatar) *
        FROM "CrcV2_UpdateMetadataDigest"
        ORDER BY avatar,
            "blockNumber"      DESC,
            "transactionIndex" DESC,
            "logIndex"         DESC
    ),
    latest_fee AS (
        SELECT DISTINCT ON (emitter) *
        FROM "CrcV2_BaseGroupFeeCollectionUpdated"
        ORDER BY emitter,
            "blockNumber"      DESC,
            "transactionIndex" DESC,
            "logIndex"         DESC
    ),
    member_counts AS (
        SELECT sub.truster as "group", count(*) as "memberCount"
        FROM (
            SELECT ct.truster, ct.trustee,
                   row_number() OVER (
                       PARTITION BY ct.truster, ct.trustee
                       ORDER BY ct."blockNumber" DESC, ct."transactionIndex" DESC, ct."logIndex" DESC
                   ) as rn,
                   ct."expiryTime"
            FROM "CrcV2_Trust" ct
            INNER JOIN "CrcV2_RegisterGroup" g ON g."group" = ct.truster
        ) sub
        WHERE sub.rn = 1
          AND sub."expiryTime" > COALESCE((SELECT max("timestamp") FROM "System_Block"), 0)::numeric
        GROUP BY sub.truster
    ),
    erc20_demurraged AS (
        SELECT ed.avatar, ed."erc20Wrapper" as "erc20WrapperDemurraged"
        FROM "CrcV2_RegisterGroup" bgc
                 JOIN "CrcV2_ERC20WrapperDeployed" ed on ed."circlesType" = 0 and ed.avatar = bgc."group"
    ),
    erc20_static AS (
        SELECT ed.avatar, ed."erc20Wrapper" as "erc20WrapperStatic"
        FROM "CrcV2_RegisterGroup" bgc
                 JOIN "CrcV2_ERC20WrapperDeployed" ed on ed."circlesType" = 1 and ed.avatar = bgc."group"
    )
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
        COALESCE(cm.owner, o.owner) as owner,
        c."mint" as "mintPolicy",
        COALESCE(cm."mintHandler", bg."mintHandler") as "mintHandler",
        c.treasury,
        s."newService"                      AS service,
        f."feeCollection",
        COALESCE(m."memberCount", 0)        AS "memberCount",
        c.name,
        c.symbol,
        ci."metadataDigest" as "cidV0Digest",
        ed."erc20WrapperDemurraged",
        es."erc20WrapperStatic"
    FROM       "CrcV2_RegisterGroup" c
    LEFT JOIN  latest_owner             o ON o.emitter   = c."group"
    LEFT JOIN  latest_service           s ON s.emitter   = c."group"
    LEFT JOIN  latest_fee               f ON f.emitter   = c."group"
    LEFT JOIN  member_counts            m ON m."group"   = c."group"
    LEFT JOIN  latest_cid               ci ON ci.avatar  = c."group"
    LEFT JOIN  erc20_demurraged         ed ON ed.avatar  = c."group"
    LEFT JOIN  erc20_static             es ON es.avatar  = c."group"
    LEFT JOIN  "CrcV2_CMGroupCreated"   cm ON cm.proxy   = c."group"
    LEFT JOIN  "CrcV2_BaseGroupCreated" bg ON bg."group" = c."group";