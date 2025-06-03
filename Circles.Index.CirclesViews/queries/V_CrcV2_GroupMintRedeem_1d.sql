-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- minted:ValueTypes.BigInt:true
-- burned:ValueTypes.BigInt:true
-- supply:ValueTypes.BigInt:true
-- demurragedMinted:ValueTypes.BigInt:true
-- demurragedBurned:ValueTypes.BigInt:true
-- demurragedSupply:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupMintRedeem_1d" ("group", "timestamp", "minted", "burned", "supply", "demurragedMinted", "demurragedBurned", "demurragedSupply") as


WITH

group_mints_redeems AS (
  SELECT 
    "group",
    date_trunc('day', "timestamp") AS "timestamp",
    SUM("minted") As "minted",
    SUM("burned") As "burned",
    ARRAY_AGG("supply" ORDER BY "timestamp" DESC) AS supplies
  FROM "V_CrcV2_GroupMintRedeem_1h"
  GROUP BY 1, 2
)

SELECT 
  "group"
  ,"timestamp"
  ,"minted"
  ,"burned"
  ,supplies[1] AS  "supply" 
  ,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), "minted")) AS "demurragedMinted"
	,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), "burned")) AS "demurragedBurned"
	,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), supplies[1])) AS "demurragedSupply"
FROM group_mints_redeems;