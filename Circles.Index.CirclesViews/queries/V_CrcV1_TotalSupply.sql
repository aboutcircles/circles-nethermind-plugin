create or replace view "V_CrcV1_TotalSupply" as
with 
t as (
    select
        "tokenAddress",
        SUM(
                case
                    when "from" = '0x0000000000000000000000000000000000000000' then amount
                    when "to"   = '0x0000000000000000000000000000000000000000' then -amount
                    else 0
                    end
        ) as "totalSupply"
    from "CrcV1_Transfer"
    group by "tokenAddress"
)
select 
    t."tokenAddress",
    s."user",
    t."totalSupply"
from "t"
join "CrcV1_Signup" s on "s"."token" = t."tokenAddress";