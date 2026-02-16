with static_token_transfers as (
    select t."blockNumber"
         , t."timestamp"
         , t."transactionIndex"
         , t."logIndex"
         , t."transactionHash"
         , t."tokenAddress"
         , t."from"
         , t."to"
         , t."amount"
    from "CrcV2_Erc20WrapperTransfer" t
             join "CrcV2_ERC20WrapperDeployed" d on d."circlesType" = 1 and d."erc20Wrapper" = t."tokenAddress"
             inner join "V_CrcV2_Avatars" a on a.avatar = d.avatar
    order by t."blockNumber", t."transactionIndex", t."logIndex"
), static_from_transfers as (
    select t1."timestamp"
         , t1."tokenAddress"
         , t1."from" as "account"
         , -t1."amount" as diff
    from static_token_transfers t1
             inner join "V_CrcV2_Avatars" t2 on t2.avatar = t1."from"
), static_to_transfers as (
    select t1."timestamp"
         , t1."tokenAddress"
         , t1."to" as "account"
         , t1."amount" as diff
    from static_token_transfers t1
             inner join "V_CrcV2_Avatars" t2 on t2.avatar = t1."to"
), static_sum as (
    select sum(diff) AS static_balance
         , account
         , "tokenAddress"
         , max("timestamp") AS "timestamp"
         , true as "isWrapped"
         , 'static' as "circlesType"
    from (
             select *
             from static_from_transfers
             union all
             select *
             from static_to_transfers
         ) as t
    group by account
           , "tokenAddress"
),

demurraged_wrapped_token_transfers as (
    select t."blockNumber"
         , t."timestamp"
         , t."transactionIndex"
         , t."logIndex"
         , t."transactionHash"
         , t."tokenAddress"
         , t."from"
         , t."to"
         , t."amount"
    from "CrcV2_Erc20WrapperTransfer" t
             join "CrcV2_ERC20WrapperDeployed" d on d."circlesType" = 0 and d."erc20Wrapper" = t."tokenAddress"
             inner join "V_CrcV2_Avatars" a on a.avatar = d.avatar
    order by t."blockNumber", t."transactionIndex", t."logIndex"
), demurraged_wrapped_from_transfers as (
    select t1."timestamp"
         , t1."tokenAddress"
         , t1."from" as "account"
         , -t1."amount" as diff
    from demurraged_wrapped_token_transfers t1
             inner join "V_CrcV2_Avatars" t2 on t2.avatar = t1."from"
), demurraged_wrapped_to_transfers as (
    select t1."timestamp"
         , t1."tokenAddress"
         , t1."to" as "account"
         , t1."amount" as diff
    from demurraged_wrapped_token_transfers t1
             inner join "V_CrcV2_Avatars" t2 on t2.avatar = t1."to"
), demurraged_wrapped_sum as (
    select sum(diff) AS inflationary_balance
         , account
         , "tokenAddress"
         , max("timestamp") AS "lastActivity"
         , true as "isWrapped"
         , 'demurraged' as "circlesType"
    from (
             select *
             from demurraged_wrapped_from_transfers
             union all
             select *
             from demurraged_wrapped_to_transfers
         ) as t
    group by account
           , "tokenAddress"
),

all_transfers as (
    select "static_balance" as balance
         , "account"
         , "tokenAddress"
         , "timestamp" as "lastActivity"
         , "isWrapped"
         , "circlesType"
    from static_sum
    union all
    select "inflationary_balance" as balance
         , "account"
         , "tokenAddress"
         , "lastActivity"
         , "isWrapped"
         , "circlesType"
    from demurraged_wrapped_sum
    union all
    select
        b."totalBalance" as balance
         ,b."account"
         ,b."tokenAddress"
         ,b."lastActivity"
         ,false AS "isWrapped"
         ,'demurraged' AS "circlesType"
    from "V_CrcV2_BalancesByAccountAndToken" b
    inner join "V_CrcV2_Avatars" a on a.avatar = b."account"
)

select balance::text
     , account
     , "tokenAddress"
     , "lastActivity"
     , "isWrapped"
     , "circlesType"
from all_transfers
where balance > 0
order by balance, account, "tokenAddress";