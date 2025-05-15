-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- value:ValueTypes.Int:true

create or replace view "V_CrcV2_GroupMembersCount_1h" ("group", "timestamp", "value") as

WITH 

---- GroupMembersChange
groups_trusts AS (
	SELECT
		t1."timestamp",
		t1."logIndex",
		t1.truster,
		t1.trustee,
		t1."expiryTime",
		LEAD(t1."timestamp") OVER (PARTITION BY t1.truster, t1.trustee ORDER BY t1."timestamp", t1."logIndex") AS next_ts
	FROM 
		"CrcV2_Trust" t1
	INNER JOIN 
		"V_CrcV2_Groups" t2 
		ON t2.group = t1.truster 
),

trust_intervals AS (
	SELECT 
		truster AS "group",
		"timestamp" AS start_ts,
		CASE 
			WHEN "expiryTime" < 10000000000 THEN "expiryTime"
			WHEN next_ts IS NOT NULL THEN next_ts
			ELSE NULL -- Still valid
		END AS end_ts
	FROM groups_trusts
),

group_membership_changes AS (
	SELECT 
		start_ts AS "timestamp",
		"group",
		1 AS cnt
	FROM trust_intervals

	UNION ALL

	SELECT 
		end_ts AS "timestamp",
		"group",
		-1 AS cnt
	FROM trust_intervals
	WHERE end_ts IS NOT NULL
),

-- V_CrcV2_GroupMembersCount_1h
members_hourly_sparse AS (
    SELECT 
        date_trunc('hour',TO_TIMESTAMP("timestamp")) AS "timestamp"
        ,"group"
        ,SUM(cnt) AS cnt
    FROM 
        group_membership_changes
	GROUP BY 1, 2
),

min_max_per_group AS (
    SELECT
        "group",
        MIN("timestamp") AS min_timestamp
    FROM 
        members_hourly_sparse
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
		members_hourly_sparse t2
	    ON t1."group" = t2."group" AND t1."timestamp" = t2."timestamp"
)


SELECT 
    "group",
    "timestamp",
    SUM(cnt) OVER (PARTITION BY "group" ORDER BY "timestamp") AS value
FROM members_change;