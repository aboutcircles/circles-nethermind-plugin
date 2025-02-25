select "demurragedTotalBalance"::text, "account", "tokenAddress"
from "V_CrcV2_BalancesByAccountAndToken" b
left join "CrcV2_RegisterGroup" g on g."group" = b."tokenAddress"
where g."group" is null
and "demurragedTotalBalance" > 0