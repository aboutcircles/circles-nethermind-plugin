WITH registered_avatars AS MATERIALIZED (
{{registered_avatars_cte_body}})
SELECT d."erc20Wrapper", d.avatar, d."circlesType"
FROM "CrcV2_ERC20WrapperDeployed" d
JOIN registered_avatars ra ON ra.avatar = d.avatar
