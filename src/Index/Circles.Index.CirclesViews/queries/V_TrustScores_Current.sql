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

CREATE MATERIALIZED VIEW IF NOT EXISTS "V_TrustScores_Current" AS
WITH unique_avatars AS (
    -- Deduplicate avatars (V_Crc_Avatars may have duplicates from v1+v2)
    SELECT DISTINCT ON ("avatar") "avatar", "timestamp"
    FROM "V_Crc_Avatars"
    ORDER BY "avatar", "timestamp" DESC
),
avatar_stats AS (
    SELECT
        a."avatar",
        COALESCE(in_deg.cnt, 0) as in_degree,
        COALESCE(out_deg.cnt, 0) as out_degree,
        COALESCE(mutual.cnt, 0) as mutual_count,
        GREATEST(0, (EXTRACT(EPOCH FROM NOW()) - a."timestamp") / 86400)::int as age_days
    FROM unique_avatars a
    LEFT JOIN (
        SELECT "trustee" as avatar, COUNT(*) as cnt
        FROM "V_CrcV2_TrustRelations"
        GROUP BY "trustee"
    ) in_deg ON a."avatar" = in_deg.avatar
    LEFT JOIN (
        SELECT "truster" as avatar, COUNT(*) as cnt
        FROM "V_CrcV2_TrustRelations"
        GROUP BY "truster"
    ) out_deg ON a."avatar" = out_deg.avatar
    LEFT JOIN (
        SELECT t1."truster" as avatar, COUNT(*) as cnt
        FROM "V_CrcV2_TrustRelations" t1
        JOIN "V_CrcV2_TrustRelations" t2
            ON t1."truster" = t2."trustee" AND t1."trustee" = t2."truster"
        GROUP BY t1."truster"
    ) mutual ON a."avatar" = mutual.avatar
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
FROM scores
WITH NO DATA;

-- Create indexes for efficient querying (only if they don't exist)
CREATE UNIQUE INDEX IF NOT EXISTS idx_trust_current_avatar ON "V_TrustScores_Current"(avatar);
CREATE INDEX IF NOT EXISTS idx_trust_current_score ON "V_TrustScores_Current"(trust_score);
CREATE INDEX IF NOT EXISTS idx_trust_current_level ON "V_TrustScores_Current"(trust_level);
CREATE INDEX IF NOT EXISTS idx_trust_current_computed_at ON "V_TrustScores_Current"(computed_at DESC)
