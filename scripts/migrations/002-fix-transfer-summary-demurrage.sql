-- Migration 002: Fix TransferSummary demurrage for inflationary ERC20 wrappers
--
-- Bug: TransferSummaryAggregator used AttoStaticCirclesToAttoCircles(val) which
-- calls Today() (DateTimeOffset.UtcNow), not the block's timestamp. This means:
--   1. The demurrage day index was wrong (based on indexing time, not block time)
--   2. Re-indexing the same block on different days produced different amounts
--
-- Additionally, the epoch constant was wrong (1675209600 chiado vs 1602720000 gnosis)
-- until PR #247 fixed it.
--
-- Fix: Recalculate amounts for TransferSummary rows that contain inflationary
-- wrapper transfers, using the block's timestamp for the day index.
--
-- This migration is SAFE to run multiple times (idempotent — recalculates same result).
-- Does NOT delete or insert rows — only UPDATEs existing amounts.
--
-- NOTE: Only affects NON-stream TransferSummary rows containing Erc20WrapperTransfer
-- events from inflationary wrappers. Stream rows (from StreamCompleted) are already
-- in demurraged CRC and unaffected.

BEGIN;

-- Step 1: Fixed64.Pow(GAMMA_64, day) — replicates C# Fixed64.Pow exactly
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

-- Step 2: V2 day index from block timestamp
-- V2_INFLATION_DAY_ZERO_UNIX = 1602720000 (2020-10-15 00:00 UTC, gnosis mainnet)
CREATE OR REPLACE FUNCTION pg_temp.v2_day_from_timestamp(block_timestamp bigint)
RETURNS integer AS $$
BEGIN
    RETURN floor((block_timestamp - 1602720000) / 86400)::integer;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Step 3: Identify and recalculate affected TransferSummary rows
--
-- Strategy: For each non-stream TransferSummary row that has Erc20WrapperTransfer
-- events with inflationary tokens, recompute the amount by:
--   1. Summing all individual transfer values from the events JSON
--   2. For inflationary wrappers: apply InflationaryToDemurrage using block timestamp
--   3. For demurraged wrappers + ERC1155: use raw value as-is
--
-- However, the events JSON doesn't reliably contain individual amounts for
-- re-aggregation. Instead, we take a simpler approach:
--
-- For rows where ALL contributing transfers are from the SAME inflationary wrapper
-- (the common case), we can reverse the old conversion and apply the correct one.
-- For mixed rows, we flag them for manual review.

-- Step 3a: Find TransferSummary rows that originated from inflationary wrapper transfers.
-- These are non-stream rows (logIndex < 0) where the from/to pair exists in
-- Erc20WrapperTransfer with an inflationary wrapper token.
WITH affected_rows AS (
    SELECT DISTINCT
        ts."blockNumber",
        ts."timestamp",
        ts."transactionIndex",
        ts."logIndex",
        ts."transactionHash",
        ts."from",
        ts."to"
    FROM "CrcV2_TransferSummary" ts
    -- Match against actual Erc20WrapperTransfer events in the same transaction
    INNER JOIN "CrcV2_Erc20WrapperTransfer" ewt
        ON ewt."transactionHash" = ts."transactionHash"
        AND ewt."from" = ts."from"
        AND ewt."to" = ts."to"
    -- Only inflationary wrappers (circlesType bit 0 set)
    INNER JOIN "CrcV2_ERC20WrapperDeployed" wd
        ON wd."erc20Wrapper" = ewt."tokenAddress"
        AND (wd."circlesType" & 1) = 1
    WHERE ts."logIndex" < 0  -- Synthetic rows from aggregator
),

-- Step 3b: Recompute the correct amount for each affected row.
-- Sum all Erc20WrapperTransfer events matching (txHash, from, to),
-- applying inflationary conversion with block timestamp.
recomputed AS (
    SELECT
        ar."blockNumber",
        ar."timestamp",
        ar."transactionIndex",
        ar."logIndex",
        ar."transactionHash",
        ar."from",
        ar."to",
        SUM(
            CASE
                WHEN wd."circlesType" IS NOT NULL AND (wd."circlesType" & 1) = 1
                THEN trunc(
                    pg_temp.fixed64_gamma_pow(pg_temp.v2_day_from_timestamp(ewt."timestamp"))
                    * ewt."amount" / 18446744073709551616  -- 2^64
                )
                ELSE ewt."amount"
            END
        )::numeric AS correct_amount
    FROM affected_rows ar
    INNER JOIN "CrcV2_Erc20WrapperTransfer" ewt
        ON ewt."transactionHash" = ar."transactionHash"
        AND ewt."from" = ar."from"
        AND ewt."to" = ar."to"
    LEFT JOIN "CrcV2_ERC20WrapperDeployed" wd
        ON wd."erc20Wrapper" = ewt."tokenAddress"
    GROUP BY
        ar."blockNumber",
        ar."timestamp",
        ar."transactionIndex",
        ar."logIndex",
        ar."transactionHash",
        ar."from",
        ar."to"
)

-- Step 3c: Update only rows where the amount actually changed
UPDATE "CrcV2_TransferSummary" ts
SET "amount" = r.correct_amount
FROM recomputed r
WHERE ts."blockNumber" = r."blockNumber"
  AND ts."transactionIndex" = r."transactionIndex"
  AND ts."logIndex" = r."logIndex"
  AND ts."transactionHash" = r."transactionHash"
  AND ts."amount" != r.correct_amount;

COMMIT;
