-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- tokenAddress:ValueTypes.Address:true
-- currencyUnit:ValueTypes.Address:true
-- wrapAmount:ValueTypes.BigInt:true
-- unwrapAmount:ValueTypes.BigInt:true
-- wrapSupply:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupWrapUnWrap_1d" ("group", "timestamp", "tokenAddress", "currencyUnit", "wrapAmount", "unwrapAmount", "wrapSupply") as

WITH

group_wrap_unwrap AS (
  SELECT 
    "group",
    date_trunc('day', "timestamp") AS "timestamp",
    "tokenAddress",
    "currencyUnit",
    SUM("wrapAmount") As "wrapAmount",
    SUM("unwrapAmount") As "unwrapAmount",
    ARRAY_AGG("wrapSupply" ORDER BY "timestamp" DESC) AS supplies
  FROM "V_CrcV2_GroupWrapUnWrap_1h"
  GROUP BY 1, 2, 3, 4
)

SELECT 
  "group"
  ,"timestamp"
  ,"tokenAddress"
  ,"currencyUnit"
  ,"wrapAmount"
  ,"unwrapAmount"
  ,supplies[1] AS  "supply" 
FROM group_wrap_unwrap;