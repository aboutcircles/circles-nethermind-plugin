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
            ,-t1.value AS diff
        FROM public."V_CrcV2_Transfers" t1
        INNER JOIN 
            public."V_CrcV1_Avatars" t2
            ON t2.user = t1."from"
        WHERE
            t1.operator IS NULL
        
        UNION ALL
        
        SELECT 
            t1."timestamp"
            ,t1."to" AS account
            ,t1."tokenAddress"
            ,t1.value AS diff
        FROM public."V_CrcV2_Transfers" t1
        INNER JOIN 
            public."V_CrcV1_Avatars" t2
            ON t2.user = t1."to"
        WHERE
            t1.operator IS NULL
    )
    GROUP BY 2, 3
),

final AS (
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
)
WHERE "demurragedTotalBalance" > 0
)

select "demurragedTotalBalance"::text, "account", "tokenAddress"
from "V_CrcV2_BalancesByAccountAndToken" b
left join "CrcV2_RegisterGroup" g on g."group" = b."tokenAddress"
where g."group" is null
and "demurragedTotalBalance" > 0

UNION ALL 

SELECT * FROM final