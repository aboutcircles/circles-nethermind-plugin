SELECT 
    t.truster as group_address,
    t.trustee as trusted_token
FROM "V_CrcV2_TrustRelations" t
INNER JOIN "CrcV2_RegisterGroup" g ON g."group" = t.truster
WHERE g."mint" = LOWER('0xCDFc5135AEC0aFbf102C108e7f5C8A88C6112842');