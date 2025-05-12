-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- tokenAddress:ValueTypes.Address:true
-- currencyUnit:ValueTypes.Address:true
-- wrapAmount:ValueTypes.BigInt:true
-- unwrapAmount:ValueTypes.BigInt:true
-- wrapSupply:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupWrapUnWrap_1h" ("group", "timestamp", "tokenAddress", "currencyUnit", "wrapAmount", "unwrapAmount", "wrapSupply") as

WITH

group_wrap_tokens AS (
	SELECT
		date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
		,t2."avatar" AS "group"
		,t1."tokenAddress"
		,CASE
			WHEN t2."circlesType"=0 THEN 'Demurrage'
			ELSE 'Inflation'
		END AS "currencyUnit"
		,SUM(
			CASE
				WHEN t1."from"='0x0000000000000000000000000000000000000000' THEN t1.amount
				ELSE 0 
			END 
		) AS "wrapAmount"
		,SUM(
			CASE
				WHEN t1."to"='0x0000000000000000000000000000000000000000' THEN -t1.amount
				ELSE 0
			END
		) AS "unwrapAmount"
	FROM public."CrcV2_Erc20WrapperTransfer" t1
	INNER JOIN
		"CrcV2_ERC20WrapperDeployed" t2
		ON t2."erc20Wrapper" = t1."tokenAddress"
	INNER JOIN
		"V_CrcV2_Avatars" t3
		ON t3."avatar" = t2."avatar"
		AND t3."type" = 'CrcV2_RegisterGroup'
	GROUP BY 1, 2, 3, 4
),

min_per_group AS (
    SELECT
        "group",
		"tokenAddress",
		"currencyUnit",
        MIN("timestamp") AS min_timestamp
    FROM 
        group_wrap_tokens
    GROUP BY 1, 2, 3
),


calendar AS (
    SELECT
        g."group",
		g."tokenAddress",
		g."currencyUnit",
        generate_series(
            g.min_timestamp,
            date_trunc('hour', CURRENT_TIMESTAMP),
            interval '1 hour'
        ) AS "timestamp"
    FROM min_per_group g
)

SELECT 
	t1."group"
	,t1."timestamp"
	,t1."tokenAddress"
	,t1."currencyUnit"
	,COALESCE(t2."wrapAmount", 0) AS "wrapAmount"
	,COALESCE(t2."unwrapAmount", 0) AS "unwrapAmount"
	,SUM(COALESCE(t2."wrapAmount", 0) + COALESCE(t2."unwrapAmount", 0)) OVER (PARTITION BY t1."group", t1."tokenAddress" ORDER BY t1."timestamp") AS "wrapSupply"
FROM 
	calendar t1
LEFT JOIN
	group_wrap_tokens t2
    ON t2."timestamp" = t1."timestamp"
	AND t2."group" = t1."group"
	AND t2."tokenAddress" = t1."tokenAddress"