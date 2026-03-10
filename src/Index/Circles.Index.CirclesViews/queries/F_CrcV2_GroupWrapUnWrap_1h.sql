CREATE OR REPLACE FUNCTION "F_CrcV2_GroupWrapUnWrap_1h"(_group text)
RETURNS TABLE (
    "group" text,
    "timestamp" timestamp,
    "tokenAddress" text,
    "tokenType" text,
    "wrapAmount" numeric,
    "unwrapAmount" numeric,
    "wrapSupply" numeric
) AS $$

WITH

group_wrap_tokens AS (
	SELECT
		date_trunc('hour',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
		,t2."avatar" AS "group"
		,t1."tokenAddress"
		,CASE
			WHEN t2."circlesType"=0 THEN 'Demurrage'
			ELSE 'Inflation'
		END AS "tokenType"
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
	WHERE t2."avatar" = _group
	GROUP BY 1, 2, 3, 4
)

SELECT
	gwt."group"
	,gwt."timestamp"
	,gwt."tokenAddress"
	,gwt."tokenType"
	,gwt."wrapAmount"
	,gwt."unwrapAmount"
	,SUM(gwt."wrapAmount" + gwt."unwrapAmount") OVER (PARTITION BY gwt."group", gwt."tokenAddress" ORDER BY gwt."timestamp") AS "wrapSupply"
FROM
	group_wrap_tokens gwt;

$$ LANGUAGE sql STABLE;
