-- COLUMNS:
-- avatar:ValueTypes.String:false
-- receive_count:ValueTypes.Int:false

CREATE OR REPLACE VIEW "V_CrcV2_ReceiveCount" AS
WITH watermark AS (
    SELECT COALESCE(MAX("_maxBlock"), 0) AS wm FROM "M_CrcV2_ReceiveCount"
),
delta_counts AS (
    SELECT "to"::text AS avatar, COUNT(*) AS receive_count
    FROM "CrcV2_TransferSummary"
    WHERE "blockNumber" > (SELECT wm FROM watermark)
    GROUP BY "to"
)
SELECT COALESCE(m.avatar, d.avatar) AS avatar,
       COALESCE(m.receive_count, 0) + COALESCE(d.receive_count, 0) AS receive_count
FROM "M_CrcV2_ReceiveCount" m
FULL OUTER JOIN delta_counts d ON m.avatar = d.avatar;
