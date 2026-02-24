-- Migration: Fix FlowEdgesScopeSingleStarted/LastEnded misclassification
-- in CrcV2_TransferSummary events JSON column.
--
-- Bug: FlowEdgesScopeSingleStarted was added to nonStreamEvents instead of
-- streamEvents, and FlowEdgesScopeLastEnded had no handler (fell through to
-- non-stream bucket). This means:
--   - Non-stream rows contain scope events that don't belong there
--   - Stream rows are missing their scope boundary events
--
-- This migration:
--   1. Extracts scope events from non-stream rows
--   2. Removes them from non-stream rows' JSON arrays
--   3. Prepends them to stream rows' JSON arrays (matched by transactionHash)
--
-- Safe to run multiple times (idempotent) — skips rows already fixed.
-- Estimated: ~504k non-stream rows + ~314k stream rows affected.

BEGIN;

-- Step 1: Build a temp table of scope events per transaction
CREATE TEMP TABLE scope_events_by_tx AS
SELECT
    "transactionHash",
    "blockNumber",
    -- Collect scope events (FlowEdgesScopeSingleStarted + FlowEdgesScopeLastEnded)
    json_agg(elem ORDER BY (elem->>'LogIndex')::int) AS scope_events,
    -- Rebuild non-stream array without scope events
    json_agg(elem ORDER BY (elem->>'LogIndex')::int)
        FILTER (WHERE elem->>'$type' NOT IN ('CrcV2_FlowEdgesScopeSingleStarted', 'CrcV2_FlowEdgesScopeLastEnded'))
        AS cleaned_events
FROM "CrcV2_TransferSummary",
     json_array_elements(events::json) AS elem
WHERE events::text LIKE '%FlowEdgesScopeSingleStarted%'
  AND events::text NOT LIKE '%StreamCompleted%'
GROUP BY "transactionHash", "blockNumber";

-- Index for join performance
CREATE INDEX ON scope_events_by_tx ("transactionHash", "blockNumber");

-- Step 2: Update non-stream rows — remove scope events from their JSON
UPDATE "CrcV2_TransferSummary" ts
SET events = COALESCE(se.cleaned_events::text, '[]')
FROM scope_events_by_tx se
WHERE ts."transactionHash" = se."transactionHash"
  AND ts."blockNumber" = se."blockNumber"
  AND ts.events::text LIKE '%FlowEdgesScopeSingleStarted%'
  AND ts.events::text NOT LIKE '%StreamCompleted%';

-- Step 3: Update stream rows — prepend scope events to their JSON
-- Uses json concatenation: scope_events || existing_events
UPDATE "CrcV2_TransferSummary" ts
SET events = (
    SELECT json_agg(elem ORDER BY (elem->>'LogIndex')::int)::text
    FROM (
        SELECT elem FROM json_array_elements(se.scope_events) AS elem
        UNION ALL
        SELECT elem FROM json_array_elements(ts.events::json) AS elem
    ) combined(elem)
)
FROM scope_events_by_tx se
WHERE ts."transactionHash" = se."transactionHash"
  AND ts."blockNumber" = se."blockNumber"
  AND ts.events::text LIKE '%StreamCompleted%';

-- Cleanup
DROP TABLE scope_events_by_tx;

COMMIT;
