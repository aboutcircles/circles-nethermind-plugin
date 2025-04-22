create or replace view "V_CrcV2_TotalSupply" as
with 
combined_transfers as (
    select
        "tokenAddress",
        id,
        "from",
        "to",
        value
    from "CrcV2_TransferSingle"
    union all
    select
        "tokenAddress",
        id,
        "from",
        "to",
        value
    from "CrcV2_TransferBatch"
)
select
    "tokenAddress",
    "id" as "tokenId",
    sum(
            case
                when "from" = '0x0000000000000000000000000000000000000000' then value
                when "to"   = '0x0000000000000000000000000000000000000000' then -value
                else 0
                end
    ) as "totalSupply"
from combined_transfers
group by
    "tokenAddress",
    "tokenId";