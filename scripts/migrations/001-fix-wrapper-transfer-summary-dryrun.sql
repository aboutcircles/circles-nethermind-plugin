-- Dry-run: Preview what the migration would insert
-- Run this FIRST on staging to verify correctness before running the real migration.
-- Shows: affected transactions, missing TransferSummary rows, and row counts.

-- Helper functions (same as migration)
CREATE OR REPLACE FUNCTION pg_temp.fixed64_gamma_pow(day integer)
RETURNS numeric AS $$
DECLARE
    base numeric := 18443079296116538654;
    result numeric := 18446744073709551616;
    two_64 numeric := 18446744073709551616;
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

CREATE OR REPLACE FUNCTION pg_temp.v2_day_from_timestamp(block_timestamp bigint)
RETURNS integer AS $$
BEGIN
    RETURN floor((block_timestamp - 1675209600) / 86400)::integer;
END;
$$ LANGUAGE plpgsql IMMUTABLE;

-- Diagnostic 1: Count of affected transactions
WITH stream_windows AS (
    SELECT
        sc."transactionHash",
        sc."blockNumber",
        sc."timestamp",
        sc."transactionIndex",
        (SELECT MAX(fss."logIndex")
         FROM "CrcV2_FlowEdgesScopeSingleStarted" fss
         WHERE fss."transactionHash" = sc."transactionHash"
           AND fss."logIndex" < sc."logIndex"
        ) AS stream_start_log,
        sc."logIndex" AS stream_end_log
    FROM "CrcV2_StreamCompleted" sc
),
wrapper_in_stream AS (
    SELECT DISTINCT ewt."transactionHash"
    FROM "CrcV2_Erc20WrapperTransfer" ewt
    INNER JOIN stream_windows sw
        ON ewt."transactionHash" = sw."transactionHash"
        AND ewt."logIndex" > sw.stream_start_log
        AND ewt."logIndex" < sw.stream_end_log
    WHERE sw.stream_start_log IS NOT NULL
)
SELECT COUNT(*) AS affected_transactions FROM wrapper_in_stream;

-- Diagnostic 2: Preview rows that would be inserted
WITH stream_windows AS (
    SELECT
        sc."transactionHash",
        sc."blockNumber",
        sc."timestamp",
        sc."transactionIndex",
        (SELECT MAX(fss."logIndex")
         FROM "CrcV2_FlowEdgesScopeSingleStarted" fss
         WHERE fss."transactionHash" = sc."transactionHash"
           AND fss."logIndex" < sc."logIndex"
        ) AS stream_start_log,
        sc."logIndex" AS stream_end_log
    FROM "CrcV2_StreamCompleted" sc
),
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
                * wis."amount" / 18446744073709551616
            )
            ELSE wis."amount"
        END AS converted_amount,
        CASE
            WHEN wd."circlesType" IS NOT NULL AND (wd."circlesType" & 1) = 1
            THEN 'inflationary'
            ELSE 'demurraged'
        END AS wrapper_type
    FROM wrapper_in_stream wis
    LEFT JOIN "CrcV2_ERC20WrapperDeployed" wd
        ON wd."erc20Wrapper" = wis."tokenAddress"
),
aggregated AS (
    SELECT
        wc."blockNumber",
        wc."transactionHash",
        wc."from",
        wc."to",
        SUM(wc.converted_amount)::numeric AS total_amount,
        COUNT(*) AS event_count,
        string_agg(DISTINCT wc.wrapper_type, ', ') AS wrapper_types
    FROM wrapper_converted wc
    GROUP BY
        wc."blockNumber",
        wc."transactionHash",
        wc."from",
        wc."to"
)
SELECT
    a.*,
    CASE
        WHEN EXISTS (
            SELECT 1 FROM "CrcV2_TransferSummary" ts
            WHERE ts."transactionHash" = a."transactionHash"
              AND ts."from" = a."from"
              AND ts."to" = a."to"
        ) THEN 'ALREADY EXISTS (skip)'
        ELSE 'WILL INSERT'
    END AS action
FROM aggregated a
ORDER BY a."blockNumber", a."transactionHash", a."from", a."to";

-- Diagnostic 3: Verify the specific transaction from the bug report
SELECT
    ts."blockNumber",
    ts."transactionHash",
    ts."from",
    ts."to",
    ts."amount",
    ts."logIndex"
FROM "CrcV2_TransferSummary" ts
WHERE ts."transactionHash" = '0xe0cfdf0f0970a104aaa79b157b240ff95e02807a40a84b2ef6ff94d7dbc761b1'
ORDER BY ts."logIndex";
