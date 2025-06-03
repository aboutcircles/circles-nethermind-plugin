-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- minted:ValueTypes.BigInt:true
-- burned:ValueTypes.BigInt:true
-- supply:ValueTypes.BigInt:true
-- demurragedMinted:ValueTypes.BigInt:true
-- demurragedBurned:ValueTypes.BigInt:true
-- demurragedSupply:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupMintRedeem_1h" ("group", "timestamp", "minted", "burned", "supply", "demurragedMinted", "demurragedBurned", "demurragedSupply") as

WITH 
	mint AS (
         SELECT 
		 	date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
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
		 	date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
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
		 	date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
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
		 	date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
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
	
),

min_max_per_group AS (
    SELECT
        "group",
        MIN("timestamp") AS min_timestamp
    FROM 
        group_actions
    GROUP BY 1
),


calendar AS (
    SELECT
        g."group",
        generate_series(
            g.min_timestamp,
            date_trunc('hour', CURRENT_TIMESTAMP),
            interval '1 hour'
        ) AS "timestamp"
    FROM min_max_per_group g
)


SELECT
	"group"
	,"timestamp"
	,"minted"
	,"burned"
	,"supply"
	,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), "minted")) AS "demurragedMinted"
	,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), "burned")) AS "demurragedBurned"
	,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), "supply")) AS "demurragedSupply"
FROM (
    SELECT 
        t1."group"
        ,t1."timestamp"
        ,COALESCE(t2."minted", 0) AS "minted"
        ,COALESCE(t2."burned", 0) AS "burned"
        ,SUM(COALESCE(t2."minted", 0) + COALESCE(t2."burned", 0)) OVER (PARTITION BY t1."group" ORDER BY t1."timestamp") AS "supply"
    FROM 
        calendar t1
    LEFT JOIN
        group_actions t2
        ON t2."group" = t1."group"
        AND t2."timestamp" = t1."timestamp"
)