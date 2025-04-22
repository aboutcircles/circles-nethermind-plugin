-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- value:ValueTypes.Int:true

create or replace view "V_CrcV2_GroupMembersCount_1h" ("group", "timestamp", "value") as

WITH 

---- GroupMembersChange
groups_trusts AS (
	SELECT
		"timestamp",
		"logIndex",
		truster,
		trustee,
		"expiryTime"
		,"TrusterIsGroup"
		,"TrusteeIsGroup"
	FROM (
		SELECT 
			t1.*
			,CASE WHEN t2.group IS NOT NULL THEN TRUE ELSE FALSE END AS "TrusterIsGroup"
			,CASE WHEN t3.group IS NOT NULL THEN TRUE ELSE FALSE END AS "TrusteeIsGroup"
		FROM 
			"CrcV2_Trust" t1
		LEFT JOIN
			"V_CrcV2_Groups" t2
			ON t2.group = t1.truster 
		LEFT JOIN
			"V_CrcV2_Groups" t3
			ON t3.group = t1.trustee
	) a
	WHERE
		"TrusterIsGroup" IS TRUE OR "TrusteeIsGroup" IS TRUE
),

groups_trusts_diff AS (
    SELECT 
        "timestamp"
		,truster AS "group"
        ,COUNT(*) AS cnt
    FROM groups_trusts
	WHERE "TrusterIsGroup" IS TRUE
    GROUP BY 1, 2
    
    UNION ALL 
    
    SELECT 
        "expiryTime" AS "timestamp"
        ,truster AS "group"
        ,-COUNT(*) AS cnt
    FROM groups_trusts
    WHERE "TrusterIsGroup" IS TRUE AND  "expiryTime" < 10000000000 --Sat Nov 20 2286
    GROUP BY 1, 2
),

-- V_CrcV2_GroupMembersCount_1h
members_change_sparse AS (
    SELECT 
        date_trunc('hour',TO_TIMESTAMP("timestamp")) AS "timestamp"
        ,"group"
        ,SUM(cnt) AS cnt
    FROM 
        groups_trusts_diff
	GROUP BY 1, 2
),

min_max_per_group AS (
    SELECT
        "group",
        MIN("timestamp") AS min_timestamp
    FROM 
        members_change_sparse
    GROUP BY 1
),

calendar AS (
    SELECT
        g."group",
        generate_series(
            g.min_timestamp,
            date_trunc('hour', CURRENT_TIMESTAMP),
            interval '1 hour'
        ) AS "timestamp"
    FROM min_max_per_group g
),

members_change AS (
    SELECT 
	    t1."group",
	    t1."timestamp" ,
	    COALESCE(t2.cnt, 0) AS cnt
	FROM 
	    calendar t1
	LEFT JOIN 
		members_change_sparse t2
	    ON t1."group" = t2."group" AND t1."timestamp" = t2."timestamp"
)


SELECT 
    "group",
    "timestamp",
    SUM(cnt) OVER (PARTITION BY "group" ORDER BY "timestamp") AS value
FROM members_change;