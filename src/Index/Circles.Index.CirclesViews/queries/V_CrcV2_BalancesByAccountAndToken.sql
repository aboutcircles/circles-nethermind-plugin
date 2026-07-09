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

-- Migration guard: this object used to be a MATERIALIZED VIEW refreshed with
-- REFRESH ... CONCURRENTLY. It is now a plain TABLE maintained incrementally by the
-- pathfinder (INSERT ... ON CONFLICT / DELETE keyed on the _maxBlock watermark) — a
-- materialized view cannot support that (PostgreSQL forbids DML on matviews). Drop the
-- old object so the CREATE TABLE below can take over:
--   * if it still exists as a materialized view -> drop it (one-time upgrade), or
--   * if it exists as a table missing _maxBlock   -> drop it (schema upgrade).
-- CASCADE transitively drops the dependent views: V_CrcV2_BalancesByAccountAndToken (recreated
-- at the end of this file) -> V_CrcV2_GroupTokenHoldersBalance -> V_CrcV2_GroupTokenSupply. Those
-- last two are recreated by the indexer's Migrate() in the SAME transaction, later in
-- ViewDependencyOrder, so this is safe ONLY as part of the full schema apply. Do not run this
-- file standalone, and keep GroupTokenHoldersBalance/GroupTokenSupply after Balances in
-- ViewDependencyOrder, or those two views are left dropped.
DO $$ BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_class c
        WHERE c.relname = 'M_CrcV2_BalancesByAccountAndToken' AND c.relkind = 'm'
    ) THEN
        DROP MATERIALIZED VIEW "M_CrcV2_BalancesByAccountAndToken" CASCADE;
    ELSIF EXISTS (
        SELECT 1 FROM pg_class c
        WHERE c.relname = 'M_CrcV2_BalancesByAccountAndToken' AND c.relkind = 'r'
    ) AND NOT EXISTS (
        SELECT 1 FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        WHERE c.relname = 'M_CrcV2_BalancesByAccountAndToken' AND a.attname = '_maxBlock'
    ) THEN
        DROP TABLE "M_CrcV2_BalancesByAccountAndToken" CASCADE;
    END IF;
END $$;

-- Pre-aggregated balances without demurrage. Maintained incrementally by the pathfinder
-- (Circles.Pathfinder.Host NetworkStateUpdaterService.IncrementalRefreshBalancesMatView):
-- each cycle folds in only transfers with blockNumber > MAX("_maxBlock"). On first creation
-- the table is fully populated (WITH DATA) so the watermark starts at the current chain tip;
-- if it already exists this statement is a no-op (the SELECT is not re-run).
CREATE TABLE IF NOT EXISTS "M_CrcV2_BalancesByAccountAndToken"
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

-- Unique index required for the incremental ON CONFLICT (account, tokenId, tokenAddress) upsert
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
           (EXTRACT(EPOCH FROM NOW())::bigint - 1602720000) / 86400
           - ("lastActivity" - 1602720000) / 86400
       )) AS "demurragedTotalBalance"
FROM merged
WHERE account <> '0x0000000000000000000000000000000000000000' AND "totalBalance" > 0;
