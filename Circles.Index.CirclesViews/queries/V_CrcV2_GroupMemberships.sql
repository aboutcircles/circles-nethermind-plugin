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
            ("blockNumber", timestamp, "transactionIndex", "logIndex", "transactionHash", "group", member, "expiryTime",
             "memberType")
as
with cte as (SELECT t."blockNumber",
                    t."timestamp",
                    t."transactionIndex",
                    t."logIndex",
                    t."transactionHash",
                    t.truster                                                   AS "group",
                    t.trustee                                                   AS member,
                    t."expiryTime",
                    case
                        when h.avatar is not null then 'CrcV2_RegisterHuman'
                        when o.organization is not null then 'CrcV2_RegisterOrganization'
                        when g.group is not null then 'CrcV2_RegisterGroup' end AS "memberType"
             FROM "V_CrcV2_TrustRelations" t
                      JOIN "CrcV2_RegisterGroup" g ON t.truster = g."group"
                      LEFT JOIN "CrcV2_RegisterHuman" h on h.avatar = t.trustee
                      LEFT JOIN "CrcV2_RegisterOrganization" o on o.organization = t.trustee
                      LEFT JOIN "CrcV2_RegisterGroup" gg on gg.group = t.trustee
)
select "blockNumber",
       timestamp,
       "transactionIndex",
       "logIndex",
       "transactionHash",
       "group",
       member,
       "expiryTime",
       "memberType"
from cte;