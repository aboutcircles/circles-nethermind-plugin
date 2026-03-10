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
$$ LANGUAGE plpgsql STABLE;

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
$$ LANGUAGE plpgsql STABLE;

-- Migration guard: drop matview if it exists without _maxBlock column
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_matviews WHERE matviewname = 'M_CrcV2_BalancesByAccountAndToken')
       AND NOT EXISTS (
           SELECT 1 FROM pg_attribute a
           JOIN pg_class c ON c.oid = a.attrelid
           WHERE c.relname = 'M_CrcV2_BalancesByAccountAndToken'
             AND a.attname = '_maxBlock'
       ) THEN
        DROP MATERIALIZED VIEW "M_CrcV2_BalancesByAccountAndToken" CASCADE;
    END IF;
END $$;

-- Materialized view: pre-aggregated balances without demurrage (refreshed periodically)
CREATE MATERIALIZED VIEW IF NOT EXISTS "M_CrcV2_BalancesByAccountAndToken"
    (account, "tokenId", "tokenAddress", "lastActivity", "totalBalance", "_maxBlock") AS
with
    tx as (
        select "blockNumber", "timestamp", "from" as account, "tokenAddress", id, -value as delta
        from "CrcV2_TransferSingle"
        union all
        select "blockNumber", "timestamp", "to"   as account, "tokenAddress", id,  value as delta
        from "CrcV2_TransferSingle"
        union all
        select "blockNumber", "timestamp", "from" as account, "tokenAddress", id, -value as delta
        from "CrcV2_TransferBatch"
        union all
        select "blockNumber", "timestamp", "to"   as account, "tokenAddress", id,  value as delta
        from "CrcV2_TransferBatch"
    ),
    agg as (
        select
            account,
            id,
            "tokenAddress",
            sum(delta)                    as balance,
            max("timestamp")              as last_ts,
            max("blockNumber")            as max_block
        from tx
        group by account, id, "tokenAddress"
    )
select
    account,
    id::text                         as "tokenId",
    "tokenAddress",
    last_ts                          as "lastActivity",
    balance                          as "totalBalance",
    max_block                        as "_maxBlock"
from agg
where account <> '0x0000000000000000000000000000000000000000'
  and balance > 0::numeric;

-- Unique index required for REFRESH MATERIALIZED VIEW CONCURRENTLY
CREATE UNIQUE INDEX IF NOT EXISTS "idx_M_CrcV2_BalancesByAccountAndToken_pk"
    ON "M_CrcV2_BalancesByAccountAndToken" (account, "tokenId", "tokenAddress");

-- Lookup indexes for common query patterns
CREATE INDEX IF NOT EXISTS "idx_M_CrcV2_BalancesByAccountAndToken_account"
    ON "M_CrcV2_BalancesByAccountAndToken" (account);
CREATE INDEX IF NOT EXISTS "idx_M_CrcV2_BalancesByAccountAndToken_tokenAddress"
    ON "M_CrcV2_BalancesByAccountAndToken" ("tokenAddress");

-- Regular view: merges matview with delta since last refresh, then computes demurrage
CREATE OR REPLACE VIEW public."V_CrcV2_BalancesByAccountAndToken"
            (account, "tokenId", "tokenAddress", "lastActivity", "totalBalance", "demurragedTotalBalance") AS
WITH watermark AS (
    SELECT COALESCE(MAX("_maxBlock"), 0) AS wm FROM "M_CrcV2_BalancesByAccountAndToken"
),
delta_tx AS (
    SELECT "timestamp", "from" AS account, "tokenAddress", id, -value AS delta
    FROM "CrcV2_TransferSingle" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION ALL
    SELECT "timestamp", "to" AS account, "tokenAddress", id, value AS delta
    FROM "CrcV2_TransferSingle" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION ALL
    SELECT "timestamp", "from" AS account, "tokenAddress", id, -value AS delta
    FROM "CrcV2_TransferBatch" WHERE "blockNumber" > (SELECT wm FROM watermark)
    UNION ALL
    SELECT "timestamp", "to" AS account, "tokenAddress", id, value AS delta
    FROM "CrcV2_TransferBatch" WHERE "blockNumber" > (SELECT wm FROM watermark)
),
delta_agg AS (
    SELECT account, id::text AS "tokenId", "tokenAddress",
           MAX("timestamp") AS "lastActivity", SUM(delta) AS "totalBalance"
    FROM delta_tx GROUP BY account, id, "tokenAddress"
),
merged AS (
    SELECT COALESCE(m.account, d.account) AS account,
           COALESCE(m."tokenId", d."tokenId") AS "tokenId",
           COALESCE(m."tokenAddress", d."tokenAddress") AS "tokenAddress",
           GREATEST(COALESCE(m."lastActivity",0), COALESCE(d."lastActivity",0)) AS "lastActivity",
           COALESCE(m."totalBalance",0) + COALESCE(d."totalBalance",0) AS "totalBalance"
    FROM "M_CrcV2_BalancesByAccountAndToken" m
    FULL OUTER JOIN delta_agg d ON m.account=d.account AND m."tokenId"=d."tokenId" AND m."tokenAddress"=d."tokenAddress"
)
SELECT account, "tokenId", "tokenAddress", "lastActivity", "totalBalance",
       floor("totalBalance" * POWER(
           0.9998013320085989574306481700129226782902039065082930593676448873,
           (EXTRACT(EPOCH FROM NOW())::bigint - 1675209600) / 86400
           - ("lastActivity" - 1675209600) / 86400
       )) AS "demurragedTotalBalance"
FROM merged
WHERE account <> '0x0000000000000000000000000000000000000000' AND "totalBalance" > 0;
