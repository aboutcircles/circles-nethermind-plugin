-- COLUMNS:
-- avatar:ValueTypes.Address:true
-- trust_score:ValueTypes.Int:true
-- trust_level:ValueTypes.String:true
-- confidence:ValueTypes.Int:true
-- computed_at:ValueTypes.Int:true
-- in_degree:ValueTypes.Int:true
-- out_degree:ValueTypes.Int:true
-- mutual_count:ValueTypes.Int:true
-- age_days:ValueTypes.Int:true

-- Trust Scores Materialized View
-- Computes trust scores for all avatars based on network position, reciprocity, and account age.
-- Requires periodic REFRESH MATERIALIZED VIEW CONCURRENTLY to update data.
--
-- Performance: Inlines trust deduplication as a single CTE to avoid repeated expansion
-- of V_CrcV2_TrustRelations (which contains an expensive window function over CrcV2_Trust).

CREATE MATERIALIZED VIEW IF NOT EXISTS "V_TrustScores_Current" AS
WITH active_trusts AS MATERIALIZED (
    -- Deduplicate trust relations: latest event per (truster, trustee) pair, still active.
    -- Equivalent to V_CrcV2_TrustRelations but computed once as a temp result.
    SELECT truster, trustee
    FROM (
        SELECT
            truster,
            trustee,
            "expiryTime",
            row_number() OVER (
                PARTITION BY truster, trustee
                ORDER BY "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
            ) AS rn
        FROM "CrcV2_Trust"
    ) t
    WHERE rn = 1
      AND "expiryTime" > COALESCE((SELECT max("timestamp") FROM "System_Block"), 0)::numeric
),
unique_avatars AS (
    -- Deduplicate avatars (V_Crc_Avatars may have duplicates from v1+v2)
    SELECT DISTINCT ON ("avatar") "avatar", "timestamp"
    FROM "V_Crc_Avatars"
    ORDER BY "avatar", "timestamp" DESC
),
trust_degrees AS (
    -- Compute in-degree and out-degree in a single pass over active_trusts
    SELECT
        avatar,
        SUM(CASE WHEN direction = 'in' THEN 1 ELSE 0 END) AS in_degree,
        SUM(CASE WHEN direction = 'out' THEN 1 ELSE 0 END) AS out_degree
    FROM (
        SELECT trustee AS avatar, 'in' AS direction FROM active_trusts
        UNION ALL
        SELECT truster AS avatar, 'out' AS direction FROM active_trusts
    ) edges
    GROUP BY avatar
),
mutual_counts AS (
    -- Count mutual trusts per avatar using EXISTS semi-join (no full self-join)
    SELECT t1.truster AS avatar, COUNT(*) AS mutual_count
    FROM active_trusts t1
    WHERE EXISTS (
        SELECT 1 FROM active_trusts t2
        WHERE t2.truster = t1.trustee AND t2.trustee = t1.truster
    )
    GROUP BY t1.truster
),
avatar_stats AS (
    SELECT
        a."avatar",
        COALESCE(d.in_degree, 0) as in_degree,
        COALESCE(d.out_degree, 0) as out_degree,
        COALESCE(m.mutual_count, 0) as mutual_count,
        GREATEST(0, (EXTRACT(EPOCH FROM NOW()) - a."timestamp") / 86400)::int as age_days
    FROM unique_avatars a
    LEFT JOIN trust_degrees d ON a."avatar" = d.avatar
    LEFT JOIN mutual_counts m ON a."avatar" = m.avatar
),
network_avg AS (
    SELECT GREATEST(1, AVG(in_degree + out_degree)) as avg_degree FROM avatar_stats
),
scores AS (
    SELECT
        s.avatar,
        LEAST(100, GREATEST(0,
            -- Network position (40 pts max): normalized by network average
            LEAST(40, (s.in_degree * 2 + s.out_degree) / n.avg_degree * 20) +
            -- Reciprocity bonus (35 pts max): mutual trusts / total incoming
            CASE WHEN s.in_degree > 0
                 THEN LEAST(35, s.mutual_count::float / s.in_degree * 35)
                 ELSE 0
            END +
            -- Age factor (25 pts max for ~180 days)
            LEAST(25, s.age_days / 7.2)
        ))::int as trust_score,
        s.in_degree, s.out_degree, s.mutual_count, s.age_days
    FROM avatar_stats s
    CROSS JOIN network_avg n
)
SELECT
    avatar,
    trust_score,
    CASE
        WHEN trust_score >= 85 THEN 'VERY_HIGH'
        WHEN trust_score >= 70 THEN 'HIGH'
        WHEN trust_score >= 50 THEN 'MEDIUM'
        WHEN trust_score >= 30 THEN 'LOW'
        ELSE 'VERY_LOW'
    END as trust_level,
    CASE
        WHEN in_degree > 5 AND out_degree > 3 THEN 90
        WHEN in_degree > 2 THEN 60
        ELSE 30
    END as confidence,
    EXTRACT(EPOCH FROM NOW())::bigint as computed_at,
    in_degree,
    out_degree,
    mutual_count,
    age_days
FROM scores;

-- Create indexes for efficient querying (only if they don't exist)
CREATE UNIQUE INDEX IF NOT EXISTS idx_trust_current_avatar ON "V_TrustScores_Current"(avatar);
CREATE INDEX IF NOT EXISTS idx_trust_current_score ON "V_TrustScores_Current"(trust_score);
CREATE INDEX IF NOT EXISTS idx_trust_current_level ON "V_TrustScores_Current"(trust_level);
CREATE INDEX IF NOT EXISTS idx_trust_current_computed_at ON "V_TrustScores_Current"(computed_at DESC);
