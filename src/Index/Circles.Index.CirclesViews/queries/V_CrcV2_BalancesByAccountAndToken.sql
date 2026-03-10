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

-- Materialized view: pre-aggregated balances without demurrage (refreshed periodically)
CREATE MATERIALIZED VIEW IF NOT EXISTS "M_CrcV2_BalancesByAccountAndToken"
    (account, "tokenId", "tokenAddress", "lastActivity", "totalBalance") AS
with
    tx as (
        select "timestamp", "from" as account, "tokenAddress", id, -value as delta
        from "CrcV2_TransferSingle"
        union all
        select "timestamp", "to"   as account, "tokenAddress", id,  value as delta
        from "CrcV2_TransferSingle"
        union all
        select "timestamp", "from" as account, "tokenAddress", id, -value as delta
        from "CrcV2_TransferBatch"
        union all
        select "timestamp", "to"   as account, "tokenAddress", id,  value as delta
        from "CrcV2_TransferBatch"
    ),
    agg as (
        select
            account,
            id,
            "tokenAddress",
            sum(delta)                    as balance,
            max("timestamp")              as last_ts
        from tx
        group by account, id, "tokenAddress"
    )
select
    account,
    id::text                         as "tokenId",
    "tokenAddress",
    last_ts                          as "lastActivity",
    balance                          as "totalBalance"
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

-- Regular view: reads from materialized view and computes demurrage at query time
create or replace view public."V_CrcV2_BalancesByAccountAndToken"
            (account, "tokenId", "tokenAddress", "lastActivity", "totalBalance", "demurragedTotalBalance") as
select
    account,
    "tokenId",
    "tokenAddress",
    "lastActivity",
    "totalBalance",
    floor("totalBalance" * POWER(
        0.9998013320085989574306481700129226782902039065082930593676448873,
        (EXTRACT(EPOCH FROM NOW())::bigint - 1675209600) / 86400
        - ("lastActivity" - 1675209600) / 86400
    )) as "demurragedTotalBalance"
from "M_CrcV2_BalancesByAccountAndToken";
