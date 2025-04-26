-- COLUMNS:
-- account:ValueTypes.Address:true
-- tokenId:ValueTypes.String:true
-- tokenAddress:ValueTypes.String:true
-- lastActivity:ValueTypes.Int:true
-- totalBalance:ValueTypes.BigInt:true
-- demurragedTotalBalance:ValueTypes.BigInt:true

CREATE OR REPLACE FUNCTION crc_day("inflationDayZero" bigint, "timestamp" bigint)
RETURNS bigint AS $$
DECLARE
    DEMURRAGE_WINDOW bigint := 86400;
BEGIN
    RETURN ("timestamp" - "inflationDayZero") / DEMURRAGE_WINDOW;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION crc_demurrage("inflationDayZero" bigint, "timestamp" bigint, "value" numeric)
    RETURNS numeric AS $$
DECLARE
    _day_last_interaction bigint;
    _now bigint := EXTRACT(EPOCH FROM NOW())::bigint;
    _day_now bigint;
    _gamma numeric := 0.9998013320085989574306481700129226782902039065082930593676448873;
BEGIN
    _day_last_interaction := crc_day("inflationDayZero", "timestamp");
    _day_now := crc_day("inflationDayZero", _now);
    return (value * POWER(_gamma, _day_now - _day_last_interaction));
END;
$$ LANGUAGE plpgsql;

create or replace view public."V_CrcV2_BalancesByAccountAndToken"
    (account, "tokenId", "tokenAddress", "lastActivity", "totalBalance", "demurragedTotalBalance") as
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
    balance                                                        AS "totalBalance",
    floor(crc_demurrage(1675209600::bigint, "timestamp", balance)) AS "demurragedTotalBalance"
FROM "accountBalances"
WHERE account <> '0x0000000000000000000000000000000000000000'::text
    AND balance > 0::numeric;