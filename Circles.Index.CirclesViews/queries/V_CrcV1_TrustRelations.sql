create or replace view public."V_CrcV1_TrustRelations"
                ("blockNumber", timestamp, "transactionIndex", "logIndex", "transactionHash", "user", "canSendTo",
                 "limit") as
SELECT "blockNumber",
       "timestamp",
       "transactionIndex",
       "logIndex",
       "transactionHash",
       "user",
       "canSendTo",
       "limit"
FROM (SELECT "CrcV1_Trust"."blockNumber",
             "CrcV1_Trust"."timestamp",
             "CrcV1_Trust"."transactionIndex",
             "CrcV1_Trust"."logIndex",
             "CrcV1_Trust"."transactionHash",
             "CrcV1_Trust"."user",
             "CrcV1_Trust"."canSendTo",
             "CrcV1_Trust"."limit",
             row_number()
             OVER (PARTITION BY "CrcV1_Trust"."user", "CrcV1_Trust"."canSendTo" ORDER BY "CrcV1_Trust"."blockNumber" DESC, "CrcV1_Trust"."transactionIndex" DESC, "CrcV1_Trust"."logIndex" DESC) AS rn
      FROM "CrcV1_Trust") t
WHERE rn = 1
  AND "limit" > 0::numeric
ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;