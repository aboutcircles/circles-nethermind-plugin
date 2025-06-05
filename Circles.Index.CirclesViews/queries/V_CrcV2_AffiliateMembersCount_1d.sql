-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- value:ValueTypes.Int:true

create or replace view "V_CrcV2_AffiliateMembersCount_1d" ("group", "timestamp", "value") as

WITH

affiliate_groups_sparse AS (
    SELECT
        "timestamp"
        ,affiliate_group AS "group"
        ,SUM(cnt) AS members_change
    FROM (
        SELECT
            "timestamp"
            ,affiliate_group
            ,SUM(cnt) AS cnt
        FROM (
            SELECT 
                "timestamp"
                ,"newGroup" AS affiliate_group
                ,COUNT(*) AS cnt
            FROM "CrcV2_AffiliateGroupChanged"
            GROUP BY 1, 2
        
            UNION ALL
        
            SELECT 
                "timestamp"
                ,"oldGroup" AS affiliate_group
                ,-COUNT(*) AS cnt
            FROM "CrcV2_AffiliateGroupChanged"
            WHERE "oldGroup" != '0x0000000000000000000000000000000000000000'
            GROUP BY 1, 2
        )
        GROUP BY 1, 2
    ) 
	GROUP BY 1, 2
),

affiliate_groups_hourly_sparse AS (
    SELECT 
        date_trunc('day',TO_TIMESTAMP("timestamp")) AS "timestamp"
        ,"group"
        ,SUM(members_change) AS members_change
    FROM 
        affiliate_groups_sparse
	GROUP BY 1, 2
),

min_max_per_group AS (
    SELECT
        "group",
        MIN("timestamp") AS min_timestamp
    FROM 
        affiliate_groups_hourly_sparse
    GROUP BY 1
),

calendar AS (
    SELECT
        g."group",
        generate_series(
            g.min_timestamp,
            date_trunc('day', CURRENT_TIMESTAMP),
            interval '1 day'
        ) AS "timestamp"
    FROM min_max_per_group g
)



SELECT 
	t1."group",
	t1."timestamp",
	SUM(COALESCE(t2.members_change, 0))  OVER (PARTITION BY t1."group" ORDER BY t1."timestamp") AS value
FROM 
	calendar t1
LEFT JOIN 
	affiliate_groups_hourly_sparse t2
	ON t1."group" = t2."group" AND t1."timestamp" = t2."timestamp"