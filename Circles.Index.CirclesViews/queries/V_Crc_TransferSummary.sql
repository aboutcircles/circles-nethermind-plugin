create or replace view "V_Crc_TransferSummary" as
with 
a as (
    select 1 as version, *
    from "CrcV1_TransferSummary"
    union all 
    select 2 as version, *
    from "CrcV2_TransferSummary"
)
select 
    "blockNumber",
    timestamp,
    "transactionIndex",
    "logIndex",
    "transactionHash",
    version,
    "from",
    "to",
    amount as value,
    events
from a
order by "blockNumber" desc, "transactionIndex" desc, "logIndex" desc;