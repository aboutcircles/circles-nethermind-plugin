CREATE OR REPLACE FUNCTION "F_CrcV2_GroupMintRedeem_1h"(_group text)
RETURNS TABLE (
    "group" text,
    "timestamp" timestamp,
    "minted" numeric,
    "burned" numeric,
    "supply" numeric,
    "demurragedMinted" numeric,
    "demurragedBurned" numeric,
    "demurragedSupply" numeric
) AS $$

WITH
	mint AS (
         SELECT
		 	date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
			,t1."tokenAddress" AS "group"
            ,SUM(t1."value") AS "minted"
			,0 			 AS "burned"
           FROM "CrcV2_TransferSingle" t1
		   WHERE t1."from" = '0x0000000000000000000000000000000000000000'::text
		     AND t1."tokenAddress" = _group
		   GROUP BY 1, 2, 4
        UNION ALL
         SELECT
		 	date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
			,t1."tokenAddress" AS "group"
            ,SUM(t1."value") AS "minted"
			,0 			 AS "burned"
         FROM "CrcV2_TransferBatch" t1
		   WHERE t1."from" = '0x0000000000000000000000000000000000000000'::text
		     AND t1."tokenAddress" = _group
		   GROUP BY 1, 2, 4
        ),

	burn AS (
         SELECT
		 	date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
			,t1."tokenAddress" AS "group"
            ,0			 AS "minted"
			,-SUM(t1."value") AS "burned"
           FROM "CrcV2_TransferSingle" t1
		   WHERE t1."to" = '0x0000000000000000000000000000000000000000'::text
		     AND t1."tokenAddress" = _group
		   GROUP BY 1, 2, 3
        UNION ALL
         SELECT
		 	date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
			,t1."tokenAddress" AS "group"
            ,0			 AS "minted"
			,-SUM(t1."value") AS "burned"
         FROM "CrcV2_TransferBatch" t1
		   WHERE t1."to" = '0x0000000000000000000000000000000000000000'::text
		     AND t1."tokenAddress" = _group
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
	ga."group"
	,ga."timestamp"
	,ga."minted"
	,ga."burned"
	,ga."supply"
	,floor(ga."minted" * POWER(0.9998013320085989574306481700129226782902039065082930593676448873,
		(EXTRACT(EPOCH FROM NOW())::bigint - 1602720000) / 86400
		- (EXTRACT(EPOCH FROM ga."timestamp")::bigint - 1602720000) / 86400
	)) AS "demurragedMinted"
	,floor(ga."burned" * POWER(0.9998013320085989574306481700129226782902039065082930593676448873,
		(EXTRACT(EPOCH FROM NOW())::bigint - 1602720000) / 86400
		- (EXTRACT(EPOCH FROM ga."timestamp")::bigint - 1602720000) / 86400
	)) AS "demurragedBurned"
	,floor(ga."supply" * POWER(0.9998013320085989574306481700129226782902039065082930593676448873,
		(EXTRACT(EPOCH FROM NOW())::bigint - 1602720000) / 86400
		- (EXTRACT(EPOCH FROM ga."timestamp")::bigint - 1602720000) / 86400
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
) ga;

$$ LANGUAGE sql STABLE;
