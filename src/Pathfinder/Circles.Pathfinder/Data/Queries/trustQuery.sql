SELECT t1.truster, t1.trustee FROM "V_CrcV2_TrustRelations" t1
INNER JOIN "V_CrcV2_Avatars" a1 ON a1.avatar = t1.truster
INNER JOIN "V_CrcV2_Avatars" a2 ON a2.avatar = t1.trustee
LEFT JOIN "CrcV2_RegisterGroup" t2 on t2."group" = t1.truster
WHERE t2."group"  IS NULL
