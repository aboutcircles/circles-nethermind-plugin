SELECT "timestamp", "from", "to", "tokenAddress", "value"::text
FROM "CrcV2_TransferSingle" WHERE "blockNumber" > @lastBlock
UNION ALL
SELECT "timestamp", "from", "to", "tokenAddress", "value"::text
FROM "CrcV2_TransferBatch" WHERE "blockNumber" > @lastBlock
