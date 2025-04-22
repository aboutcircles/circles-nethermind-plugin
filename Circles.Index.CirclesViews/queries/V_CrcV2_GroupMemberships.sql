create or replace view public."V_CrcV2_GroupMemberships"
    ("blockNumber", timestamp, "transactionIndex", "logIndex", "transactionHash", "group", member,
        "expiryTime", "memberType") as
SELECT 
    t."blockNumber",
    t."timestamp",
    t."transactionIndex",
    t."logIndex",
    t."transactionHash",
    t.truster AS "group",
    t.trustee AS member,
    t."expiryTime",
    a.type as "memberType"
FROM "V_CrcV2_TrustRelations" t
JOIN "CrcV2_RegisterGroup" g ON t.truster = g."group"
JOIN "V_CrcV2_Avatars" a ON a.avatar = t.trustee;