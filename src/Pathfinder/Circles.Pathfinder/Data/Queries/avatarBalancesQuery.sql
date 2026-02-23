SELECT b."totalBalance"::text AS balance, b."account", b."tokenAddress", b."lastActivity"
FROM "V_CrcV2_BalancesByAccountAndToken" b
WHERE b."account" = ANY(@avatars) AND b."totalBalance" > 0
