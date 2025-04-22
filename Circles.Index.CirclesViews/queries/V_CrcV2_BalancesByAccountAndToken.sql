create or replace view public."V_CrcV2_BalancesByAccountAndToken"
    (account, "tokenId", "tokenAddress", "lastActivity", "demurragedTotalBalance") as
WITH 
transfers AS (
    SELECT 
        "CrcV2_TransferSingle"."timestamp",
        "CrcV2_TransferSingle"."from",
        "CrcV2_TransferSingle"."to",
        "CrcV2_TransferSingle".id,
        "CrcV2_TransferSingle".value,
        "CrcV2_TransferSingle"."tokenAddress"
    FROM "CrcV2_TransferSingle"
    UNION ALL
    SELECT 
        "CrcV2_TransferBatch"."timestamp",
        "CrcV2_TransferBatch"."from",
        "CrcV2_TransferBatch"."to",
        "CrcV2_TransferBatch".id,
        "CrcV2_TransferBatch".value,
        "CrcV2_TransferBatch"."tokenAddress"
    FROM "CrcV2_TransferBatch"
),
"accountBalances" AS (
    SELECT 
        all_transfers.account,
        all_transfers.id,
        sum(all_transfers.amount)      AS balance,
        max(all_transfers."timestamp") AS "timestamp",
        all_transfers."tokenAddress"
    FROM (
        SELECT 
            transfers."from"  AS account,
            transfers.id,
            - transfers.value AS amount,
            transfers."timestamp",
            transfers."tokenAddress"
        FROM transfers
        UNION ALL
        SELECT transfers."to"  AS account,
            transfers.id,
            transfers.value AS amount,
            transfers."timestamp",
            transfers."tokenAddress"
        FROM transfers
    ) all_transfers
    GROUP BY all_transfers.account, all_transfers.id, all_transfers."tokenAddress"
)

SELECT 
    account,
    id::text                                                       AS "tokenId",
    "tokenAddress",
    "timestamp"                                                    AS "lastActivity",
    floor(crc_demurrage(1675209600::bigint, "timestamp", balance)) AS "demurragedTotalBalance"
FROM "accountBalances"
WHERE account <> '0x0000000000000000000000000000000000000000'::text
    AND balance > 0::numeric;