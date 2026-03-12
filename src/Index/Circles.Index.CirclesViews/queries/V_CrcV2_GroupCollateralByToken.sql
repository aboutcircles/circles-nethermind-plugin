-- COLUMNS:
-- group:ValueTypes.Address:true
-- id:ValueTypes.BigInt:true
-- amountLocked:ValueTypes.BigInt:true
-- demurragedAmountLocked:ValueTypes.BigInt:true
-- fractionLocked:ValueTypes.Double:true


create or replace view public."V_CrcV2_GroupCollateralByToken"
    ( "group", "id", "amountLocked", "demurragedAmountLocked", "fractionLocked") as

SELECT
    "group"
    ,"id"
    ,"amountLocked"
    ,"demurragedAmountLocked"
    ,COALESCE("amountLocked" / NULLIF(SUM("amountLocked") OVER (PARTITION BY "group"),0),0) AS "fractionLocked"
FROM (
    SELECT
        "group"
        ,"id"
        ,SUM("amountLocked") AS "amountLocked"
        ,floor(SUM("amountLocked" * POWER(
            0.9998013320085989574306481700129226782902039065082930593676448873,
            (EXTRACT(EPOCH FROM NOW())::bigint - 1602720000) / 86400
            - ("timestamp" - 1602720000) / 86400
        ))) AS "demurragedAmountLocked"
    FROM "V_CrcV2_GroupCollateralDiffByToken"
    GROUP BY 1, 2
)
WHERE "amountLocked" > 0::numeric
