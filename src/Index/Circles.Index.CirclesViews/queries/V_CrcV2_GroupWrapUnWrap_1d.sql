-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- tokenAddress:ValueTypes.Address:true
-- tokenType:ValueTypes.Address:true
-- wrapAmount:ValueTypes.BigInt:true
-- unwrapAmount:ValueTypes.BigInt:true
-- wrapSupply:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupWrapUnWrap_1d" ("group", "timestamp", "tokenAddress", "tokenType", "wrapAmount", "unwrapAmount", "wrapSupply") as

WITH

group_wrap_tokens AS (
	SELECT
		date_trunc('day',TO_TIMESTAMP(t1."timestamp"))  AS "timestamp"
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
	INNER JOIN
		"V_CrcV2_Avatars" t3
		ON t3."avatar" = t2."avatar"
		AND t3."type" = 'CrcV2_RegisterGroup'
	GROUP BY 1, 2, 3, 4
)

SELECT
	"group"
	,"timestamp"
	,"tokenAddress"
	,"tokenType"
	,"wrapAmount"
	,"unwrapAmount"
	,SUM("wrapAmount" + "unwrapAmount") OVER (PARTITION BY "group", "tokenAddress" ORDER BY "timestamp") AS "wrapSupply"
FROM
	group_wrap_tokens
