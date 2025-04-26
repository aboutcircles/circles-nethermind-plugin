-- COLUMNS:
-- timestamp:ValueTypes.BigInt:true
-- tokenAddress:ValueTypes.Address:true
-- vaultBalance:ValueTypes.BigInt:true


create or replace view public."V_CrcV2_Erc20BalancerVaultBalance_1d"
    ("timestamp", "tokenAddress", "value") as
WITH

vault_balance AS (
  SELECT 
    date_trunc('day', "timestamp") AS "timestamp",
	"tokenAddress",
    ARRAY_AGG(value ORDER BY "timestamp" DESC) AS values
  FROM "V_CrcV2_Erc20BalancerVaultBalance_1h"
  GROUP BY 1, 2
)

SELECT 
  "timestamp",
  "tokenAddress",
  values[1] AS value 
FROM vault_balance;