-- COLUMNS:
-- vault:ValueTypes.Address:true
-- id:ValueTypes.BigInt:true
-- balance:ValueTypes.BigInt:true

CREATE OR REPLACE VIEW "V_CrcV2_GroupVaultBalancesByToken" AS
WITH events AS (
    -- 1) CollateralLockedSingle inflows
    SELECT
        cv.vault,
        cls.id,
        cls."timestamp",
        cls.value AS amount
    FROM "CrcV2_CollateralLockedSingle" cls
             JOIN "CrcV2_CreateVault" cv ON cv."group" = cls."group"

    UNION ALL

    -- 2) CollateralLockedBatch inflows
    SELECT
        cv.vault,
        clb.id,
        clb."timestamp",
        clb.value AS amount
    FROM "CrcV2_CollateralLockedBatch" clb
             JOIN "CrcV2_CreateVault" cv ON cv."group" = clb."group"

    UNION ALL

    -- 3) GroupRedeemCollateralReturn outflows (negative)
    SELECT
        cv.vault,
        grcr.id,
        grcr."timestamp",
        -grcr.value AS amount
    FROM "CrcV2_GroupRedeemCollateralReturn" grcr
             JOIN "CrcV2_CreateVault" cv ON cv."group" = grcr."group"

    UNION ALL

    -- 4) GroupRedeemCollateralBurn outflows (negative)
    SELECT
        cv.vault,
        grcb.id,
        grcb."timestamp",
        -grcb.value AS amount
    FROM "CrcV2_GroupRedeemCollateralBurn" grcb
             JOIN "CrcV2_CreateVault" cv ON cv."group" = grcb."group"
),
     grouped AS (
         SELECT
             vault,
             id,
             SUM(amount) AS balance,
             MAX("timestamp") AS "lastActivity"
         FROM events
         GROUP BY vault, id
     )
SELECT
    vault,
    id,
    FLOOR(
        balance * POWER(
            0.9998013320085989574306481700129226782902039065082930593676448873,
            (EXTRACT(EPOCH FROM NOW())::bigint - 1602720000) / 86400
            - ("lastActivity" - 1602720000) / 86400
        )
    ) AS "balance"
FROM grouped
ORDER BY vault, id;
