-- Migration: Fix missing Erc20WrapperTransfer events in TransferSummary
--
-- Bug: Erc20WrapperTransfer events occurring inside Hub stream scopes
-- (between FlowEdgesScopeSingleStarted and StreamCompleted) were captured
-- in streamEvents but never aggregated into TransferSummary rows.
-- StreamCompleted only reflects Hub ERC1155 flows, not wrapper contract transfers.
--
-- This migration INSERTs the missing TransferSummary rows with zero downtime.
-- Safe to run multiple times (idempotent via NOT EXISTS guard).
--
-- Run AFTER deploying the code fix to TransferSummaryAggregator.cs.
-- New blocks indexed after the code fix will be correct automatically.
-- This migration only backfills historical data.

BEGIN;

-- Step 1: Create a temporary function to compute Fixed64.Pow(GAMMA_64, day)
-- Replicates the C# Fixed64.Pow exponentiation-by-squaring on Q64.64 fixed-point.
-- Needed for inflationary wrapper value conversion (circlesType = 1).
CREATE OR REPLACE FUNCTION pg_temp.fixed64_gamma_pow(day integer)
RETURNS numeric AS $$
DECLARE
    base numeric := 18443079296116538654;         -- GAMMA_64
    result numeric := 18446744073709551616;        -- 2^64 (Q64.64 identity = 1.0)
    two_64 numeric := 18446744073709551616;        -- 2^64
    n integer := day;
BEGIN
    WHILE n > 0 LOOP
        IF n % 2 = 1 THEN
            result := trunc(result * base / two_64);
        END IF;
        base := trunc(base * base / two_64);
        n := n / 2;
    END LOOP;
    RETURN result;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Step 2: Compute the V2 day from a block timestamp
-- V2_INFLATION_DAY_ZERO_UNIX = 1602720000 (2020-10-15 00:00 UTC)
-- Uses the block timestamp (not NOW()) so the conversion matches what the
-- indexer would have computed at the time the block was processed.
CREATE OR REPLACE FUNCTION pg_temp.v2_day_from_timestamp(block_timestamp bigint)
RETURNS integer AS $$
BEGIN
    RETURN floor((block_timestamp - 1602720000) / 86400)::integer;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Step 3: Insert missing TransferSummary rows
WITH
-- 3a: Identify stream windows: (txHash, stream_start_logIndex, stream_end_logIndex)
-- A stream starts at FlowEdgesScopeSingleStarted and ends at StreamCompleted.
-- We pair each StreamCompleted with the MAX FlowEdgesScopeSingleStarted logIndex
-- that is less than the StreamCompleted logIndex in the same transaction.
stream_windows AS (
    SELECT
        sc."transactionHash",
        sc."blockNumber",
        sc."timestamp",
        sc."transactionIndex",
        (
            SELECT MAX(fss."logIndex")
            FROM "CrcV2_FlowEdgesScopeSingleStarted" fss
            WHERE fss."transactionHash" = sc."transactionHash"
              AND fss."logIndex" < sc."logIndex"
        ) AS stream_start_log,
        sc."logIndex" AS stream_end_log
    FROM "CrcV2_StreamCompleted" sc
),

-- 3b: Find Erc20WrapperTransfer events inside stream windows
wrapper_in_stream AS (
    SELECT
        ewt."blockNumber",
        ewt."timestamp",
        ewt."transactionIndex",
        ewt."transactionHash",
        ewt."from",
        ewt."to",
        ewt."amount",
        ewt."tokenAddress"
    FROM "CrcV2_Erc20WrapperTransfer" ewt
    INNER JOIN stream_windows sw
        ON ewt."transactionHash" = sw."transactionHash"
        AND ewt."logIndex" > sw.stream_start_log
        AND ewt."logIndex" < sw.stream_end_log
    WHERE sw.stream_start_log IS NOT NULL
),

-- 3c: Convert inflationary wrapper amounts (circlesType = 1)
-- Demurraged wrappers (circlesType = 0 or 2): use raw amount
-- Inflationary wrappers (circlesType = 1 or 3): apply InflationaryToDemurrage
wrapper_converted AS (
    SELECT
        wis."blockNumber",
        wis."timestamp",
        wis."transactionIndex",
        wis."transactionHash",
        wis."from",
        wis."to",
        CASE
            WHEN wd."circlesType" IS NOT NULL AND (wd."circlesType" & 1) = 1
            THEN trunc(
                pg_temp.fixed64_gamma_pow(pg_temp.v2_day_from_timestamp(wis."timestamp"))
                * wis."amount" / 18446744073709551616  -- 2^64
            )
            ELSE wis."amount"
        END AS converted_amount
    FROM wrapper_in_stream wis
    LEFT JOIN "CrcV2_ERC20WrapperDeployed" wd
        ON wd."erc20Wrapper" = wis."tokenAddress"
),

-- 3d: Aggregate by (tx, from, to)
aggregated AS (
    SELECT
        wc."blockNumber",
        wc."timestamp",
        wc."transactionIndex",
        wc."transactionHash",
        wc."from",
        wc."to",
        SUM(wc.converted_amount)::numeric AS total_amount
    FROM wrapper_converted wc
    GROUP BY
        wc."blockNumber",
        wc."timestamp",
        wc."transactionIndex",
        wc."transactionHash",
        wc."from",
        wc."to"
),

-- 3e: Compute synthetic logIndex values that don't collide with existing rows.
-- Existing TransferSummary rows use negative logIndex values.
-- We need to find the minimum existing logIndex per transaction and go below it.
with_log_index AS (
    SELECT
        a.*,
        COALESCE(
            (SELECT MIN(ts."logIndex") FROM "CrcV2_TransferSummary" ts
             WHERE ts."transactionHash" = a."transactionHash"),
            0
        ) - ROW_NUMBER() OVER (
            PARTITION BY a."transactionHash"
            ORDER BY a."from", a."to"
        ) AS synthetic_log_index
    FROM aggregated a
)

-- 3f: Insert only rows that don't already exist (idempotent)
INSERT INTO "CrcV2_TransferSummary"
    ("blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash", "from", "to", "amount", "events")
SELECT
    wli."blockNumber",
    wli."timestamp",
    wli."transactionIndex",
    wli.synthetic_log_index::integer,
    wli."transactionHash",
    wli."from",
    wli."to",
    wli.total_amount,
    '[]'::json  -- events JSON not used by RPC queries; supplementary only
FROM with_log_index wli
WHERE NOT EXISTS (
    SELECT 1
    FROM "CrcV2_TransferSummary" ts
    WHERE ts."transactionHash" = wli."transactionHash"
      AND ts."from" = wli."from"
      AND ts."to" = wli."to"
);

COMMIT;
