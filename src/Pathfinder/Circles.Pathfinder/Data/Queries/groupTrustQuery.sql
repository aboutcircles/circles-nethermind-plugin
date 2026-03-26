SELECT
    t.truster as group_address,
    t.trustee as trusted_token
FROM "V_CrcV2_TrustRelations" t
INNER JOIN "CrcV2_RegisterGroup" g ON g."group" = t.truster
WHERE g."mint" = LOWER(@router);