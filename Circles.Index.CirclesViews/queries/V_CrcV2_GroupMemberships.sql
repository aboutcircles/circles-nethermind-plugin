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
with cte as (
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
    from "V_CrcV2_TrustRelations" t
    join "CrcV2_RegisterGroup" g on g."group" = t.truster         -- ensures truster is a group
    left join "CrcV2_RegisterHuman" h on h.avatar = t.trustee      -- is member a human?
    left join "CrcV2_RegisterOrganization" o on o.organization = t.trustee -- org?
    left join "CrcV2_RegisterGroup" gg on gg."group" = t.trustee   -- is member a group?
)
select "blockNumber","timestamp","transactionIndex","logIndex","transactionHash","group",member,"expiryTime","memberType"
from cte;