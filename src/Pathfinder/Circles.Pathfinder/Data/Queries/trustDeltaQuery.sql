SELECT "blockNumber", "transactionIndex", "logIndex", "truster", "trustee", "expiryTime"::bigint
FROM "CrcV2_Trust" WHERE "blockNumber" > @lastBlock
