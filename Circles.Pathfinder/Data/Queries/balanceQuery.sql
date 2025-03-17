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
	FROM (
	    SELECT
	        "timestamp" AS "lastActivity"
	        ,account
	        ,"tokenAddress"
	        ,balance
	        ,floor(crc_demurrage(1675209600::bigint, "timestamp", balance)) AS "demurragedTotalBalance"
	    FROM
	        current_crc20_balances
	) AS t
	WHERE "demurragedTotalBalance" > 0
)

SELECT 
	"demurragedTotalBalance"::text
	,"account"
	,"tokenAddress"
	,FALSE AS "isWrapped"
FROM 
	"V_CrcV2_BalancesByAccountAndToken" b
LEFT JOIN 
	"CrcV2_RegisterGroup" g 
	ON g."group" = b."tokenAddress"
WHERE 
	g."group" is null AND "demurragedTotalBalance" > 0

UNION ALL 

SELECT 
	"demurragedTotalBalance"
	,"account"
	,"tokenAddress"
	,TRUE AS "isWrapped"
FROM current_crc20_demurraged_balances