select
    b."totalBalance"::text as balance
     ,b."account"
     ,b."tokenAddress"
     ,b."lastActivity"
     ,false AS "isWrapped"
     ,'demurraged' AS "circlesType"
from "V_CrcV2_BalancesByAccountAndToken" b
inner join "V_CrcV2_Avatars" a on a.avatar = b."account"
where b."totalBalance" > 0
order by b."totalBalance", b."account", b."tokenAddress";
