create or replace view public."V_CrcV1_BalancesByAccountAndToken"(account, "tokenAddress", "lastActivity", "totalBalance", "tokenOwner") as
WITH 
transfers AS (
    SELECT 
        "CrcV1_Transfer"."timestamp",
        "CrcV1_Transfer"."from",
        "CrcV1_Transfer"."to",
        "CrcV1_Transfer".amount AS value,
        "CrcV1_Transfer"."tokenAddress"
    FROM "CrcV1_Transfer"
),
"accountBalances" AS (
    SELECT 
        all_transfers.account,
        sum(all_transfers.amount)      AS balance,
        max(all_transfers."timestamp") AS "timestamp",
        all_transfers."tokenAddress"
    FROM (
        SELECT 
            transfers."from"  AS account,
            - transfers.value AS amount,
            transfers."timestamp",
            transfers."tokenAddress"
        FROM transfers
        UNION ALL
        SELECT 
            transfers."to"  AS account,
            transfers.value AS amount,
            transfers."timestamp",
            transfers."tokenAddress"
        FROM transfers
    ) all_transfers
    GROUP BY all_transfers.account, all_transfers."tokenAddress"
)
SELECT "accountBalances".account,
        "accountBalances"."tokenAddress",
        "accountBalances"."timestamp" AS "lastActivity",
        "accountBalances".balance     AS "totalBalance",
        "CrcV1_Signup"."user"         AS "tokenOwner"
FROM "accountBalances"
JOIN "CrcV1_Signup" ON "accountBalances"."tokenAddress" = "CrcV1_Signup".token
WHERE "accountBalances".account <> '0x0000000000000000000000000000000000000000'::text
    AND "accountBalances".balance > 0::numeric;