-- Pre-materialized receive counts for profile search ranking.
-- Replaces the expensive full-table GROUP BY in circles_searchProfiles:
--   SELECT "to", COUNT(*) FROM "CrcV2_TransferSummary" GROUP BY "to"
-- which scans millions of rows on every search query.
--
-- Refreshed periodically by NetworkStateUpdaterService.

-- Migration guard: drop matview if it exists without _maxBlock column
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_matviews WHERE matviewname = 'M_CrcV2_ReceiveCount')
       AND NOT EXISTS (
           SELECT 1 FROM pg_attribute
           WHERE attrelid = '"M_CrcV2_ReceiveCount"'::regclass
             AND attname = '_maxBlock'
       ) THEN
        DROP MATERIALIZED VIEW "M_CrcV2_ReceiveCount" CASCADE;
    END IF;
END $$;

CREATE MATERIALIZED VIEW IF NOT EXISTS "M_CrcV2_ReceiveCount"
    (avatar, receive_count, "_maxBlock") AS
SELECT "to"::text AS avatar, COUNT(*) AS receive_count, MAX("blockNumber") AS "_maxBlock"
FROM "CrcV2_TransferSummary"
GROUP BY "to";

-- Unique index required for REFRESH MATERIALIZED VIEW CONCURRENTLY
CREATE UNIQUE INDEX IF NOT EXISTS "idx_M_CrcV2_ReceiveCount_pk"
    ON "M_CrcV2_ReceiveCount" (avatar);
