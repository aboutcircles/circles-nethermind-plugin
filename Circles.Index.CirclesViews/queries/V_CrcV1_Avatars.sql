-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- user:ValueTypes.Address:true
-- token:ValueTypes.Address:true

create or replace view "V_CrcV1_Avatars" 
    ("blockNumber", timestamp, "transactionIndex", "logIndex", 
    "transactionHash", type, "user", token, "cidV0Digest") as
WITH a AS (
    SELECT "CrcV1_Signup"."blockNumber",
            "CrcV1_Signup"."timestamp",
            "CrcV1_Signup"."transactionIndex",
            "CrcV1_Signup"."logIndex",
            "CrcV1_Signup"."transactionHash",
            'CrcV1_Signup'::text AS type,
            "CrcV1_Signup"."user",
            "CrcV1_Signup".token
    FROM "CrcV1_Signup"
    UNION ALL
    SELECT "CrcV1_OrganizationSignup"."blockNumber",
            "CrcV1_OrganizationSignup"."timestamp",
            "CrcV1_OrganizationSignup"."transactionIndex",
            "CrcV1_OrganizationSignup"."logIndex",
            "CrcV1_OrganizationSignup"."transactionHash",
            'CrcV1_OrganizationSignup'::text        AS type,
            "CrcV1_OrganizationSignup".organization AS "user",
            NULL::text                              AS token
    FROM "CrcV1_OrganizationSignup"
)
select 
    "blockNumber"
    , timestamp
    , "transactionIndex"
    , "logIndex"
    , "transactionHash"
    , type
    , "user"
    , token
    , "cidV0Digest"
from a
LEFT JOIN (
    SELECT 
        cid_1.avatar,
        cid_1."metadataDigest"     AS "cidV0Digest",
        row_number() OVER (PARTITION BY cid_1.avatar ORDER BY cid_1."blockNumber" DESC, cid_1."transactionIndex" DESC, cid_1."logIndex" DESC) AS rn
    FROM 
        "CrcV1_UpdateMetadataDigest" cid_1
) cid 
ON cid.avatar = a."user" AND cid.rn = 1;