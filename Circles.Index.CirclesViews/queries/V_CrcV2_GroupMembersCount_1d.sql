-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- value:ValueTypes.Int:true

create or replace view "V_CrcV2_GroupMembersCount_1d" ("group", "timestamp", "value") as
WITH

members_1d AS (
  SELECT 
    "group",
    date_trunc('day', "timestamp") AS "timestamp",
    ARRAY_AGG(value ORDER BY "timestamp" DESC) AS values
  FROM "V_CrcV2_GroupMembersCount_1h"
  GROUP BY 1, 2
)

SELECT 
  "group",
  "timestamp",
  values[1] AS value 
FROM members_1d;