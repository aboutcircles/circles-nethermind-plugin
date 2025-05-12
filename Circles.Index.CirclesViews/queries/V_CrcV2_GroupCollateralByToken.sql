-- COLUMNS:
-- group:ValueTypes.Address:true
-- id:ValueTypes.BigInt:true
-- amountLocked:ValueTypes.BigInt:true
-- demurragedAmountLocked:ValueTypes.BigInt:true
-- fractionLocked:ValueTypes.DoublePrecision:true


create or replace view public."V_CrcV2_GroupCollateralByToken"
    ( "group", "id", "amountLocked", "demurragedAmountLocked", "fractionLocked") as

SELECT
    "group"
    ,"id"
    ,"amountLocked"
    ,"demurragedAmountLocked"
    ,"amountLocked" / (SUM("amountLocked") OVER (PARTITION BY "group")) AS "fractionLocked"
FROM (
    SELECT
        "group"
        ,"id"
        ,SUM("amountLocked") AS "amountLocked"
        ,floor(SUM(crc_demurrage(1675209600::bigint, "timestamp", "amountLocked"))) AS "demurragedAmountLocked"
    FROM "V_CrcV2_GroupCollateralDiffByToken"
    GROUP BY 1, 2
)
WHERE "amountLocked" > 0::numeric
