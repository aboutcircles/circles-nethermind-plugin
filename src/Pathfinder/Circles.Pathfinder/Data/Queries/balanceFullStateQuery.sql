with registered_avatars as materialized (
    select organization as avatar from "CrcV2_RegisterOrganization"
    union all
    select "group" as avatar from "CrcV2_RegisterGroup"
    union all
    select avatar from "CrcV2_RegisterHuman"
),
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
         , true as "isStatic"
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
         , false as "isStatic"
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
         , false as "isStatic"
    from native_agg n
    join registered_avatars ra on ra.avatar = n.account
)
select balance::text
     , account
     , "tokenAddress"
     , "lastActivity"
     , "isWrapped"
     , "isStatic"
from (
    select * from static_sum
    union all
    select * from demurraged_sum
    union all
    select * from native_sum
) all_balances
where balance > 0;
