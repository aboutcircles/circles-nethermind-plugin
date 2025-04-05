SELECT t1.truster, t1.trustee FROM "V_CrcV2_TrustRelations" t1
LEFT JOIN "CrcV2_RegisterGroup" t2 on t2."group" = t1.truster
WHERE t2."group"  IS NULL

UNION ALL

SELECT t1.truster, t2."erc20Wrapper" AS trustee FROM "V_CrcV2_TrustRelations" t1
INNER JOIN "CrcV2_ERC20WrapperDeployed" t2
ON t2.avatar = t1.trustee
LEFT JOIN "CrcV2_RegisterGroup" t3 on t3."group" = t1.truster
WHERE t3."group"  IS NULL