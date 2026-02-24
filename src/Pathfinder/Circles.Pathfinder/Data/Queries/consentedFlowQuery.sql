-- Get latest advanced usage flag per avatar
-- Returns all avatars that have ever set their flag (we filter for consented flow in code)
SELECT DISTINCT ON (avatar) avatar, flag
FROM "CrcV2_SetAdvancedUsageFlag"
ORDER BY avatar, "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC;
