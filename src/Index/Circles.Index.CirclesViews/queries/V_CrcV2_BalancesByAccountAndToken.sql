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

create or replace view public."V_CrcV2_BalancesByAccountAndToken"
            (account, "tokenId", "tokenAddress", "lastActivity", "totalBalance", "demurragedTotalBalance") as
with
/* Unpivot debits/credits once, from both single and batch transfers. */
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
    balance                          as "totalBalance",
    floor(crc_demurrage(1675209600::bigint, last_ts, balance)) as "demurragedTotalBalance"
from agg
where account <> '0x0000000000000000000000000000000000000000'
  and balance > 0::numeric;