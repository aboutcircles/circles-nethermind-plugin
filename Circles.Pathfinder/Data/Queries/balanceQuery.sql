WITH

current_crc20_balances AS (
    SELECT
        MAX("timestamp") AS "timestamp"
        ,account
        ,"tokenAddress"
        ,SUM(diff) AS balance
    FROM (
        SELECT 	
            t1."timestamp"
            ,t1."from" AS account
            ,t1."tokenAddress"
            ,-t1.amount AS diff
        FROM public."CrcV2_Erc20WrapperTransfer" t1
        INNER JOIN 
            public."V_CrcV2_Avatars" t2
            ON t2.avatar = t1."from"
        
        UNION ALL
        
        SELECT 
            t1."timestamp"
            ,t1."to" AS account
            ,t1."tokenAddress"
            ,t1.amount AS diff
        FROM public."CrcV2_Erc20WrapperTransfer" t1
        INNER JOIN 
            public."V_CrcV2_Avatars" t2
            ON t2.avatar = t1."to"
    ) AS t
    GROUP BY 2, 3
),

current_crc20_demurraged_balances AS (
	SELECT
	    "demurragedTotalBalance"::text
	    ,account AS "account"
	    ,"tokenAddress"
	    ,"circlesType"
	FROM (
	    SELECT
            current_crc20_balances."timestamp" AS "lastActivity"
	        ,account
	        ,"tokenAddress"
	        ,balance
	        ,floor(crc_demurrage(1675209600::bigint, current_crc20_balances."timestamp", balance)) AS "demurragedTotalBalance"
	        ,t1."circlesType"
	    FROM
	        current_crc20_balances
	    JOIN 
            public."CrcV2_ERC20WrapperDeployed" t1 
	        ON t1."erc20Wrapper" = current_crc20_balances."tokenAddress"
	) AS t
	WHERE "demurragedTotalBalance" > 0
)

SELECT 
	"demurragedTotalBalance"::text
	,"account"
	,"tokenAddress"
	,FALSE AS "isWrapped"
    ,'demurraged' AS "circlesType"
FROM 
	"V_CrcV2_BalancesByAccountAndToken" 
WHERE
	"demurragedTotalBalance" > 0

UNION ALL 

SELECT 
	"demurragedTotalBalance"
	,"account"
	,"tokenAddress"
	,TRUE AS "isWrapped"
    ,CASE "circlesType" WHEN 0 THEN 'demurraged' ELSE 'static' END
    FROM current_crc20_demurraged_balances