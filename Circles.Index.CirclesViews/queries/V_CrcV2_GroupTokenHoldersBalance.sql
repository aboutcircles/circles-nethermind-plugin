-- COLUMNS:
-- group:ValueTypes.Address:true
-- holder:ValueTypes.Address:true
-- totalBalance:ValueTypes.BigInt:true
-- demurragedTotalBalance:ValueTypes.BigInt:true
-- fractionOwnership:ValueTypes.BigInt:true

create or replace view "V_CrcV2_GroupTokenHoldersBalance"
    ("group", "holder", "totalBalance", "demurragedTotalBalance", "fractionOwnership") as
SELECT 
    t1."tokenAddress" AS "group",
    t1."account" AS "holder",
    t1."totalBalance",
    t1."demurragedTotalBalance",
    t1."demurragedTotalBalance" / (SUM(t1."demurragedTotalBalance") OVER (PARTITION BY t1."tokenAddress")) AS "fractionOwnership"
FROM 
    "V_CrcV2_BalancesByAccountAndToken" t1
INNER JOIN
    "V_CrcV2_Groups" t2
    ON t2."group" = t1."tokenAddress";