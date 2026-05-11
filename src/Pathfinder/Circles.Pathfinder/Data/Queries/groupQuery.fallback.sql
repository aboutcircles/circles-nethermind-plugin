SELECT
    "group" AS group_address
FROM "CrcV2_RegisterGroup"
WHERE "mint" = LOWER(@mintPolicy);
