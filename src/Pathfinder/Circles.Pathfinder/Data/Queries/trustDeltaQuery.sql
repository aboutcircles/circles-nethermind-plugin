SELECT "blockNumber", "transactionIndex", "logIndex", "truster", "trustee", LEAST("expiryTime", 9223372036854775807)::bigint
FROM "CrcV2_Trust" WHERE "blockNumber" > @lastBlock
