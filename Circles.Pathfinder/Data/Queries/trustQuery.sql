SELECT truster, trustee FROM "V_CrcV2_TrustRelations"

UNION ALL

SELECT t1.truster, t2."erc20Wrapper" AS trustee FROM "V_CrcV2_TrustRelations" t1
INNER JOIN "CrcV2_ERC20WrapperDeployed" t2
ON t2.avatar = t1.trustee