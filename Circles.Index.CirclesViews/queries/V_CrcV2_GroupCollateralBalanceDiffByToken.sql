-- COLUMNS:
-- timestamp:ValueTypes.BigInt:true
-- group:ValueTypes.Address:true
-- id:ValueTypes.BigInt:true
-- amountLocked:ValueTypes.BigInt:true


create or replace view public."V_CrcV2_GroupCollateralBalanceDiffByToken"
    ("timestamp", "group", "id", "amountLocked") as
WITH

batch_collateral_locked AS (
    SELECT
        "timestamp"
        ,"group"
        ,"id"
        ,value AS "amountLocked"
    FROM "CrcV2_CollateralLockedBatch"
),

single_collateral_locked AS (
    SELECT
        "timestamp"
        ,"group"
        ,"id"
        ,value AS "amountLocked"
    FROM "CrcV2_CollateralLockedSingle"
),

collateral_locked AS (
    SELECT
        "timestamp"
        ,"group"
        ,"id"
        ,SUM("amountLocked") AS "amountLocked"
    FROM (
        SELECT * FROM batch_collateral_locked
        UNION ALL
        SELECT * FROM single_collateral_locked
    )
    GROUP BY 1, 2, 3
),

collateral_redeemed AS (
    SELECT
        "timestamp"
        ,"group"
        ,"id"
        ,-value AS "amountLocked"
    FROM "CrcV2_GroupRedeemCollateralReturn"
)

SELECT 
   "timestamp"
	,"group"
	,"id"
	,SUM("amountLocked") AS "amountLocked"
FROM (
	SELECT * FROM collateral_locked
	UNION ALL 
	SELECT * FROM collateral_redeemed
)
GROUP BY 1, 2, 3
