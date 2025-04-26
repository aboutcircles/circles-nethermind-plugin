-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- minted:ValueTypes.BigInt:true
-- redeemed:ValueTypes.BigInt:true
-- supply:ValueTypes.BigInt:true
-- demurragedMinted:ValueTypes.BigInt:true
-- demurragedRedeemed:ValueTypes.BigInt:true
-- demurragedSupply:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupMintRedeem_1h" ("group", "timestamp", "minted", "redeemed", "supply", "demurragedMinted", "demurragedRedeemed", "demurragedSupply") as

WITH

group_actions AS (
	SELECT
		"timestamp"
		,"group"
		,SUM("minted") AS "minted"
		,SUM("redeemed") AS "redeemed"
	FROM (
		SELECT 
			date_trunc('hour',TO_TIMESTAMP("timestamp"))  AS "timestamp"
			,"group"
			,SUM(amount) AS "minted"
			,0 			 AS "redeemed"
		FROM "CrcV2_GroupMint"
		GROUP BY 1, 2, 4

		UNION ALL

		SELECT 
			date_trunc('hour',TO_TIMESTAMP("timestamp"))  AS "timestamp"
			,"group"
			,0			 AS "minted"
			,-SUM("value") AS "redeemed"
		FROM "CrcV2_GroupRedeem"
		GROUP BY 1, 2, 3
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
	,"redeemed"
	,"supply"
	,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), "minted")) AS "demurragedMinted"
	,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), "redeemed")) AS "demurragedRedeemed"
	,floor(crc_demurrage(1675209600::bigint, CAST(EXTRACT(EPOCH FROM "timestamp") AS BIGINT), "supply")) AS "demurragedSupply"
FROM (
    SELECT 
        t1."group"
        ,t1."timestamp"
        ,COALESCE(t2."minted", 0) AS "minted"
        ,COALESCE(t2."redeemed", 0) AS "redeemed"
        ,SUM(COALESCE(t2."minted", 0) + COALESCE(t2."redeemed", 0)) OVER (PARTITION BY t1."group" ORDER BY t1."timestamp") AS "supply"
    FROM 
        calendar t1
    LEFT JOIN
        group_actions t2
        ON t2."group" = t1."group"
        AND t2."timestamp" = t1."timestamp"
)