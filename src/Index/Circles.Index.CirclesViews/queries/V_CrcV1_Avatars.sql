-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- type:ValueTypes.String:true
-- user:ValueTypes.Address:true
-- token:ValueTypes.Address:true
-- cidV0Digest:ValueTypes.Bytes:false

create or replace view "V_CrcV1_Avatars" 
    ("blockNumber", timestamp, "transactionIndex", "logIndex", 
    "transactionHash", type, "user", token, "cidV0Digest") as
with signup as (               -- your signup / organisation append
    select  s."blockNumber",
            s."timestamp",
            s."transactionIndex",
            s."logIndex",
            s."transactionHash",
            s.type,
            s."user",
            s.token
    from   (
               select 'CrcV1_Signup'               as type,
                      "blockNumber",
                      timestamp,
                      "transactionIndex",
                      "logIndex",
                      "transactionHash",
                      "user",
                      token
               from   "CrcV1_Signup"
               union all
               select 'CrcV1_OrganizationSignup'   as type,
                      "blockNumber",
                      timestamp,
                      "transactionIndex",
                      "logIndex",
                      "transactionHash",
                      organization,
                      null
               from   "CrcV1_OrganizationSignup"
           ) s
)
select  s.*,
        m."cidV0Digest"
from    signup           as s
            left join lateral (
    select  "metadataDigest" as "cidV0Digest"
    from    "CrcV1_UpdateMetadataDigest" m
    where   m.avatar = s."user"
    order   by m."blockNumber"        desc,
               m."transactionIndex"    desc,
               m."logIndex"            desc
    limit   1
    ) m on true;