WITH registered_avatars AS MATERIALIZED (
{{registered_avatars_cte_body}})
SELECT
    t.truster as group_address,
    t.trustee as trusted_token
FROM "V_CrcV2_TrustRelations" t
INNER JOIN "CrcV2_RegisterGroup" g ON g."group" = t.truster
INNER JOIN registered_avatars ra ON ra.avatar = t.trustee
WHERE g."mint" = LOWER(@router);