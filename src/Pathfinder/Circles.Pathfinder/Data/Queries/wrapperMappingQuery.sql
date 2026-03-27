WITH registered_avatars AS MATERIALIZED (
    SELECT organization AS avatar FROM "CrcV2_RegisterOrganization"
    UNION ALL
    SELECT "group" AS avatar FROM "CrcV2_RegisterGroup"
    UNION ALL
    SELECT avatar FROM "CrcV2_RegisterHuman"
)
SELECT d."erc20Wrapper", d.avatar
FROM "CrcV2_ERC20WrapperDeployed" d
JOIN registered_avatars ra ON ra.avatar = d.avatar
