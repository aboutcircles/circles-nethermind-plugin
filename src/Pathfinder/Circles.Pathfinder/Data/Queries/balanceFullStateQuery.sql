SELECT b."totalBalance"::text AS balance, b."account", b."tokenAddress", b."lastActivity"
FROM "V_CrcV2_BalancesByAccountAndToken" b
INNER JOIN "V_CrcV2_Avatars" a ON a.avatar = b."account"
WHERE b."totalBalance" > 0
