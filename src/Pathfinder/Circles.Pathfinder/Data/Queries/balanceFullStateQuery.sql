with registered_avatars as materialized (
{{registered_avatars_cte_body}}),
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
native_sum as (
    select b."totalBalance" as balance
         , b."account"
         , b."tokenAddress"
         , b."lastActivity"
         , false as "isWrapped"
         , false as "isStatic"
    from "V_CrcV2_BalancesByAccountAndToken" b
    join registered_avatars ra on ra.avatar = b."account"
    join registered_avatars ra_token on ra_token.avatar = b."tokenAddress"
    where b."totalBalance" > 0
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
