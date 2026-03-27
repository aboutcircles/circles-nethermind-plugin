-- Optimized balance query for the pathfinder graph.
-- Changes from the original:
--   1. MATERIALIZED registered_avatars CTE — avoids expanding V_CrcV2_Avatars 7× (each expansion joins
--      3 registration tables + metadata). The pathfinder only needs the avatar address for filtering.
--   2. Removed ORDER BY from wrapper CTEs — downstream only aggregates with sum(); ordering is wasted I/O
--      (was causing ~350MB of external-merge disk sorts).
--   3. Inlined V_CrcV2_BalancesByAccountAndToken for the native balance path — skips the crc_demurrage()
--      SQL function call (pathfinder applies its own C# demurrage via DemurrageCalculator.Apply).
--      Also drops the redundant `id` group key (tokenAddress uniquely determines id in CrcV2).
--   4. Removed final ORDER BY — C# reads into an unordered List<>.
--   5. Trimmed wrapper CTEs to only SELECT the 4 columns needed downstream (was selecting 9 including
--      blockNumber, transactionIndex, logIndex, transactionHash that were never read).

with registered_avatars as materialized (
    select organization as avatar from "CrcV2_RegisterOrganization"
    union all
    select "group" as avatar from "CrcV2_RegisterGroup"
    union all
    select avatar from "CrcV2_RegisterHuman"
),

-- Static ERC20 wrapper transfers (circlesType = 1)
static_wrapper_transfers as (
    select t."timestamp"
         , t."tokenAddress"
         , t."from"
         , t."to"
         , t."amount"
    from "CrcV2_Erc20WrapperTransfer" t
    join "CrcV2_ERC20WrapperDeployed" d
        on d."circlesType" = 1 and d."erc20Wrapper" = t."tokenAddress"
    join registered_avatars a on a.avatar = d.avatar
),
static_sum as (
    select sum(diff) as balance
         , account
         , "tokenAddress"
         , max("timestamp") as "lastActivity"
         , true as "isWrapped"
         , 'static' as "circlesType"
    from (
        select t."timestamp", t."tokenAddress", t."from" as account, -t."amount" as diff
        from static_wrapper_transfers t
        join registered_avatars ra on ra.avatar = t."from"
        union all
        select t."timestamp", t."tokenAddress", t."to" as account, t."amount" as diff
        from static_wrapper_transfers t
        join registered_avatars ra on ra.avatar = t."to"
    ) as t
    group by account, "tokenAddress"
),

-- Demurraged ERC20 wrapper transfers (circlesType = 0)
demurraged_wrapper_transfers as (
    select t."timestamp"
         , t."tokenAddress"
         , t."from"
         , t."to"
         , t."amount"
    from "CrcV2_Erc20WrapperTransfer" t
    join "CrcV2_ERC20WrapperDeployed" d
        on d."circlesType" = 0 and d."erc20Wrapper" = t."tokenAddress"
    join registered_avatars a on a.avatar = d.avatar
),
demurraged_sum as (
    select sum(diff) as balance
         , account
         , "tokenAddress"
         , max("timestamp") as "lastActivity"
         , true as "isWrapped"
         , 'demurraged' as "circlesType"
    from (
        select t."timestamp", t."tokenAddress", t."from" as account, -t."amount" as diff
        from demurraged_wrapper_transfers t
        join registered_avatars ra on ra.avatar = t."from"
        union all
        select t."timestamp", t."tokenAddress", t."to" as account, t."amount" as diff
        from demurraged_wrapper_transfers t
        join registered_avatars ra on ra.avatar = t."to"
    ) as t
    group by account, "tokenAddress"
),

-- Native (unwrapped) ERC1155 balances — inlined from V_CrcV2_BalancesByAccountAndToken.
-- Skips crc_demurrage() (C# applies its own) and the redundant `id` group key.
native_tx as (
    select "timestamp", "from" as account, "tokenAddress", -value as delta
    from "CrcV2_TransferSingle"
    where "from" <> '0x0000000000000000000000000000000000000000'
    union all
    select "timestamp", "to" as account, "tokenAddress", value as delta
    from "CrcV2_TransferSingle"
    where "to" <> '0x0000000000000000000000000000000000000000'
    union all
    select "timestamp", "from" as account, "tokenAddress", -value as delta
    from "CrcV2_TransferBatch"
    where "from" <> '0x0000000000000000000000000000000000000000'
    union all
    select "timestamp", "to" as account, "tokenAddress", value as delta
    from "CrcV2_TransferBatch"
    where "to" <> '0x0000000000000000000000000000000000000000'
),
-- Aggregate ALL accounts first (cheaper sort without join overhead), then filter to registered.
-- The GroupAggregate sorts 10.8M rows regardless, but avoiding a Merge Join on 10.8M rows
-- before the sort lets the planner use HashAggregate or parallel strategies more freely.
native_agg as (
    select account
         , "tokenAddress"
         , sum(delta) as balance
         , max("timestamp") as "lastActivity"
    from native_tx
    group by account, "tokenAddress"
    having sum(delta) > 0
),
native_sum as (
    select balance
         , n.account
         , n."tokenAddress"
         , n."lastActivity"
         , false as "isWrapped"
         , 'demurraged' as "circlesType"
    from native_agg n
    join registered_avatars ra on ra.avatar = n.account
    join registered_avatars ra_token on ra_token.avatar = n."tokenAddress"
)

select balance::text
     , account
     , "tokenAddress"
     , "lastActivity"
     , "isWrapped"
     , "circlesType"
from (
    select * from static_sum
    union all
    select * from demurraged_sum
    union all
    select * from native_sum
) all_balances
where balance > 0;
