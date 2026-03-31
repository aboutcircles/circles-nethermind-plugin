    SELECT organization AS avatar FROM "CrcV2_RegisterOrganization"
    UNION ALL
    SELECT "group" AS avatar FROM "CrcV2_RegisterGroup"
    UNION ALL
    SELECT avatar FROM "CrcV2_RegisterHuman"
