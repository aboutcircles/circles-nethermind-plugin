-- COLUMNS:
-- group:ValueTypes.Address:true
-- totalBalance:ValueTypes.BigInt:true
-- demurragedTotalSupply:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupTokenSupply"
    ("group", "totalSupply", "demurragedTotalSupply") as

SELECT 
	"group" 
	,SUM("totalBalance") AS "totalSupply"
	,SUM("demurragedTotalBalance") AS "demurragedTotalSupply"
FROM "V_CrcV2_GroupTokenHoldersBalance"
GROUP BY "group";
