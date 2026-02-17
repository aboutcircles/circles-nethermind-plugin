-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- group:ValueTypes.Address:true
-- member:ValueTypes.Address:true
-- expiryTime:ValueTypes.BigInt:true
-- memberType:ValueTypes.String:true

create or replace view public."V_CrcV2_GroupMemberships"
("blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash", "group", member, "expiryTime", "memberType")
as
with group_trusts as MATERIALIZED (
    -- Pre-filter CrcV2_Trust to group trusters BEFORE the window function.
    -- This shrinks window input from ~551K rows to ~10K (only group trust events).
    select
        sub."blockNumber",
        sub."timestamp",
        sub."transactionIndex",
        sub."logIndex",
        sub."transactionHash",
        sub.truster,
        sub.trustee,
        sub."expiryTime"
    from (
        select
            ct."blockNumber",
            ct."timestamp",
            ct."transactionIndex",
            ct."logIndex",
            ct."transactionHash",
            ct.truster,
            ct.trustee,
            ct."expiryTime",
            row_number() over (
                partition by ct.truster, ct.trustee
                order by ct."blockNumber" desc, ct."transactionIndex" desc, ct."logIndex" desc
            ) as rn
        from "CrcV2_Trust" ct
        inner join "CrcV2_RegisterGroup" g on g."group" = ct.truster
    ) sub
    where rn = 1
      and "expiryTime" > coalesce((select max("timestamp") from "System_Block"), 0)::numeric
),
cte as (
    select
        t."blockNumber",
        t."timestamp",
        t."transactionIndex",
        t."logIndex",
        t."transactionHash",
        t.truster as "group",
        t.trustee as member,
        t."expiryTime",
        case
            when h.avatar is not null then 'CrcV2_RegisterHuman'
            when o.organization is not null then 'CrcV2_RegisterOrganization'
            when gg."group" is not null then 'CrcV2_RegisterGroup'
            else null
        end as "memberType"
    from group_trusts t
    left join "CrcV2_RegisterHuman" h on h.avatar = t.trustee      -- is member a human?
    left join "CrcV2_RegisterOrganization" o on o.organization = t.trustee -- org?
    left join "CrcV2_RegisterGroup" gg on gg."group" = t.truster   -- is member a group?
)
select "blockNumber","timestamp","transactionIndex","logIndex","transactionHash","group",member,"expiryTime","memberType"
from cte;
