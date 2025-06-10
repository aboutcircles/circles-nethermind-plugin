select "user",
       "canSendTo",
       "limit"
from (
         select "blockNumber",
                "transactionIndex",
                "logIndex",
                "user",
                "canSendTo",
                "limit",
                row_number() over (
                    partition by "user", "canSendTo"
                    order by "blockNumber" desc,
                        "transactionIndex" desc,
                        "logIndex" desc
                    ) as rn
         from "CrcV1_Trust"
     ) t
where rn = 1
  and ("user"     = @address
    or "canSendTo" = @address);
