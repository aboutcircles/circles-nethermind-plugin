-- COLUMNS:
-- vault:ValueTypes.Address:true
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.Timestamp:true
-- balance:ValueTypes.BigInt:true

-- Hourly aggregated group vault balances for time-series analysis and drain detection.
-- Similar pattern to V_CrcV2_Erc20BalancerVaultBalance_1h but for group treasury vaults.
-- Tracks collateral locked/redeemed from group treasuries over time.

CREATE OR REPLACE VIEW "V_CrcV2_GroupVaultBalancesByToken_1h" AS
WITH events AS (
    -- CollateralLockedSingle inflows
    SELECT
        cv.vault,
        cls."group",
        date_trunc('hour', TO_TIMESTAMP(cls."timestamp")) as "timestamp",
        cls.value AS amount
    FROM "CrcV2_CollateralLockedSingle" cls
    JOIN "CrcV2_CreateVault" cv ON cv."group" = cls."group"

    UNION ALL

    -- CollateralLockedBatch inflows
    SELECT
        cv.vault,
        clb."group",
        date_trunc('hour', TO_TIMESTAMP(clb."timestamp")) as "timestamp",
        clb.value AS amount
    FROM "CrcV2_CollateralLockedBatch" clb
    JOIN "CrcV2_CreateVault" cv ON cv."group" = clb."group"

    UNION ALL

    -- GroupRedeemCollateralReturn outflows (negative)
    SELECT
        cv.vault,
        grcr."group",
        date_trunc('hour', TO_TIMESTAMP(grcr."timestamp")) as "timestamp",
        -grcr.value AS amount
    FROM "CrcV2_GroupRedeemCollateralReturn" grcr
    JOIN "CrcV2_CreateVault" cv ON cv."group" = grcr."group"

    UNION ALL

    -- GroupRedeemCollateralBurn outflows (negative)
    SELECT
        cv.vault,
        grcb."group",
        date_trunc('hour', TO_TIMESTAMP(grcb."timestamp")) as "timestamp",
        -grcb.value AS amount
    FROM "CrcV2_GroupRedeemCollateralBurn" grcb
    JOIN "CrcV2_CreateVault" cv ON cv."group" = grcb."group"
),

hourly_grouped AS (
    -- Sum net changes per group per hour
    SELECT
        vault,
        "group",
        "timestamp",
        SUM(amount) as net_change
    FROM events
    GROUP BY vault, "group", "timestamp"
),

-- Generate calendar for dense time series (fill gaps with 0)
min_timestamps AS (
    SELECT
        vault,
        "group",
        MIN("timestamp") as min_ts
    FROM hourly_grouped
    GROUP BY vault, "group"
),

calendar AS (
    SELECT
        m.vault,
        m."group",
        generate_series(
            m.min_ts,
            date_trunc('hour', CURRENT_TIMESTAMP),
            interval '1 hour'
        ) AS "timestamp"
    FROM min_timestamps m
),

dense AS (
    SELECT
        c.vault,
        c."group",
        c."timestamp",
        COALESCE(h.net_change, 0) as net_change
    FROM calendar c
    LEFT JOIN hourly_grouped h
        ON c.vault = h.vault
        AND c."group" = h."group"
        AND c."timestamp" = h."timestamp"
)

SELECT
    vault,
    "group",
    EXTRACT(EPOCH FROM "timestamp")::bigint as "timestamp",
    SUM(net_change) OVER (PARTITION BY vault ORDER BY "timestamp") as balance
FROM dense
ORDER BY vault, "timestamp";
