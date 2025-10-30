-- COLUMNS:
-- timestamp:ValueTypes.BigInt:true
-- tokenAddress:ValueTypes.Address:true
-- vaultBalance:ValueTypes.BigInt:true


create or replace view public."V_CrcV2_Erc20BalancerVaultBalance_1d"
    ("timestamp", "tokenAddress", "value") as
WITH

balancer_vault_diffs AS (
	SELECT
		date_trunc('day',TO_TIMESTAMP("timestamp"))  AS "timestamp"
		,"tokenAddress"
		,SUM("vaultBalanceDiff") AS "vaultBalanceDiff"
	FROM (
		SELECT 
			"timestamp"
			,"tokenAddress"
			,amount AS "vaultBalanceDiff"
		FROM "CrcV2_Erc20WrapperTransfer"
		WHERE 
			"to" = '0xba12222222228d8ba445958a75a0704d566bf2c8'
		UNION ALL
		SELECT 
			"timestamp"
			,"tokenAddress"
			,-amount AS "vaultBalanceDiff"
		FROM "CrcV2_Erc20WrapperTransfer"
		WHERE 
			"from" = '0xba12222222228d8ba445958a75a0704d566bf2c8'
	)
	GROUP BY 1, 2
),

min_timestamp_tokenAddress AS (
    SELECT
        "tokenAddress",
        MIN("timestamp") AS min_timestamp
    FROM 
        balancer_vault_diffs
    GROUP BY 1
),

calendar AS (
    SELECT
        g."tokenAddress",
        generate_series(
            g.min_timestamp,
            date_trunc('day', CURRENT_TIMESTAMP),
            interval '1 day'
        ) AS "timestamp"
    FROM min_timestamp_tokenAddress g
),

balancer_vault_diffs_dense AS (
    SELECT 
	    t1."tokenAddress",
	    t1."timestamp" ,
	    COALESCE(t2."vaultBalanceDiff", 0) AS "vaultBalanceDiff"
	FROM 
	    calendar t1
	LEFT JOIN 
		balancer_vault_diffs t2
	    ON t1."tokenAddress" = t2."tokenAddress" AND t1."timestamp" = t2."timestamp"
)

SELECT
	"timestamp"
	,"tokenAddress"
	,SUM("vaultBalanceDiff") OVER (PARTITION BY "tokenAddress" ORDER BY "timestamp") AS value
FROM
	balancer_vault_diffs_dense