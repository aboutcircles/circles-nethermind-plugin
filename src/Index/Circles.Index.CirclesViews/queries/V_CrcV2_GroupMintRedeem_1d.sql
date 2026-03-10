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
	mint AS (
    SELECT
		 	date_trunc('day',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
			,t1."tokenAddress" AS "group"
            ,SUM(t1."value") AS "minted"
			,0 			 AS "burned"
           FROM "CrcV2_TransferSingle" t1
		   INNER JOIN
		   		"V_Crc_Avatars" t2
				 ON t2.avatar = t1."tokenAddress"
		   WHERE t1."from" = '0x0000000000000000000000000000000000000000'::text
		   GROUP BY 1, 2, 4
        UNION ALL
         SELECT
		 	date_trunc('day',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
			,t1."tokenAddress" AS "group"
            ,SUM(t1."value") AS "minted"
			,0 			 AS "burned"
         FROM "CrcV2_TransferBatch" t1
		   INNER JOIN
		   		"V_Crc_Avatars" t2
				 ON t2.avatar = t1."tokenAddress"
		   WHERE t1."from" = '0x0000000000000000000000000000000000000000'::text
		   GROUP BY 1, 2, 4
        ),

	burn AS (
         SELECT
		 	date_trunc('day',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
			,t1."tokenAddress" AS "group"
            ,0			 AS "minted"
			,-SUM(t1."value") AS "burned"
           FROM "CrcV2_TransferSingle" t1
		   INNER JOIN
		   		"V_Crc_Avatars" t2
				 ON t2.avatar = t1."tokenAddress"
		   WHERE t1."to" = '0x0000000000000000000000000000000000000000'::text
		   GROUP BY 1, 2, 3
        UNION ALL
         SELECT
		 	date_trunc('day',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
			,t1."tokenAddress" AS "group"
            ,0			 AS "minted"
			,-SUM(t1."value") AS "burned"
         FROM "CrcV2_TransferBatch" t1
		   INNER JOIN
		   		"V_Crc_Avatars" t2
				 ON t2.avatar = t1."tokenAddress"
		   WHERE t1."to" = '0x0000000000000000000000000000000000000000'::text
		   GROUP BY 1, 2, 3
        ),

group_actions AS (
	SELECT
		"timestamp"
		,"group"
		,SUM("minted") AS "minted"
		,SUM("burned") AS "burned"
	FROM (
		SELECT * FROM mint
		UNION ALL
		SELECT * FROM burn
	)
	GROUP BY 1, 2

)

SELECT
	"group"
	,"timestamp"
	,"minted"
	,"burned"
	,"supply"
	,floor("minted" * POWER(0.9998013320085989574306481700129226782902039065082930593676448873,
		(EXTRACT(EPOCH FROM NOW())::bigint - 1675209600) / 86400
		- (EXTRACT(EPOCH FROM "timestamp")::bigint - 1675209600) / 86400
	)) AS "demurragedMinted"
	,floor("burned" * POWER(0.9998013320085989574306481700129226782902039065082930593676448873,
		(EXTRACT(EPOCH FROM NOW())::bigint - 1675209600) / 86400
		- (EXTRACT(EPOCH FROM "timestamp")::bigint - 1675209600) / 86400
	)) AS "demurragedBurned"
	,floor("supply" * POWER(0.9998013320085989574306481700129226782902039065082930593676448873,
		(EXTRACT(EPOCH FROM NOW())::bigint - 1675209600) / 86400
		- (EXTRACT(EPOCH FROM "timestamp")::bigint - 1675209600) / 86400
	)) AS "demurragedSupply"
FROM (
    SELECT
        "group"
        ,"timestamp"
        ,"minted"
        ,"burned"
        ,SUM("minted" + "burned") OVER (PARTITION BY "group" ORDER BY "timestamp") AS "supply"
    FROM
        group_actions
)
