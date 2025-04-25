-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- minted:ValueTypes.BigInt:true
-- redeemed:ValueTypes.BigInt:true
-- supply:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupMintRedeem_1d" ("group", "timestamp", "minted", "redeemed", "supply") as

WITH

group_mints_redeems AS (
  SELECT 
    "group",
    date_trunc('day', "timestamp") AS "timestamp",
    SUM("minted") As "minted",
    SUM("redeemed") As "redeemed",
    ARRAY_AGG("supply" ORDER BY "timestamp" DESC) AS supplies
  FROM "V_CrcV2_GroupMintRedeem_1h"
  GROUP BY 1, 2
)

SELECT 
  "group",
  "timestamp",
  "minted",
  "redeemed",
   supplies[1] AS  "supply" 
FROM group_mints_redeems;