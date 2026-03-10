-- Pre-materialized receive counts for profile search ranking.
-- Replaces the expensive full-table GROUP BY in circles_searchProfiles:
--   SELECT "to", COUNT(*) FROM "CrcV2_TransferSummary" GROUP BY "to"
-- which scans millions of rows on every search query.
--
-- Refreshed periodically by NetworkStateUpdaterService.

CREATE MATERIALIZED VIEW IF NOT EXISTS "M_CrcV2_ReceiveCount"
    (avatar, receive_count) AS
SELECT "to"::text AS avatar, COUNT(*) AS receive_count
FROM "CrcV2_TransferSummary"
GROUP BY "to";

-- Unique index required for REFRESH MATERIALIZED VIEW CONCURRENTLY
CREATE UNIQUE INDEX IF NOT EXISTS "idx_M_CrcV2_ReceiveCount_pk"
    ON "M_CrcV2_ReceiveCount" (avatar);
