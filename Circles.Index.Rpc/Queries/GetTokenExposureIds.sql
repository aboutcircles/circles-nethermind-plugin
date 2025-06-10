WITH tokens AS (
    SELECT "tokenAddress",
           'CrcV1_Signup'                  AS "type",
           s."user"                        AS "tokenOwner"
    FROM   public."CrcV1_Transfer"  t
               JOIN   "CrcV1_Signup"          s ON s.token = t."tokenAddress"
    WHERE  "to" = @address

    UNION ALL
    SELECT "tokenAddress",
           CASE WHEN rh.avatar IS NOT NULL
                    THEN 'CrcV2_RegisterHuman'
                ELSE 'CrcV2_RegisterGroup'
               END                           AS "type",
           "tokenAddress"                AS "tokenOwner"
    FROM   public."CrcV2_TransferSingle" ts
               LEFT   JOIN "CrcV2_RegisterHuman" rh ON rh.avatar = ts."tokenAddress"
    WHERE  "to" = @address

    UNION ALL
    SELECT "tokenAddress",
           CASE WHEN rh.avatar IS NOT NULL
                    THEN 'CrcV2_RegisterHuman'
                ELSE 'CrcV2_RegisterGroup'
               END                           AS "type",
           "tokenAddress"                AS "tokenOwner"
    FROM   public."CrcV2_TransferBatch"  tb
               LEFT   JOIN "CrcV2_RegisterHuman" rh ON rh.avatar = tb."tokenAddress"
    WHERE  "to" = @address

    UNION ALL
    SELECT "tokenAddress",
           CASE WHEN wd."circlesType" = 0
                    THEN 'CrcV2_ERC20WrapperDeployed_Demurraged'
                ELSE 'CrcV2_ERC20WrapperDeployed_Inflationary'
               END                           AS "type",
           wd.avatar                     AS "tokenOwner"
    FROM   public."CrcV2_Erc20WrapperTransfer" wt
               JOIN   "CrcV2_ERC20WrapperDeployed"   wd ON wd."erc20Wrapper" = wt."tokenAddress"
    WHERE  "to" = @address
),
     distinct_tokens AS (
         SELECT DISTINCT "tokenAddress", "type", "tokenOwner"
         FROM   tokens
     )
SELECT "tokenAddress", "type", "tokenOwner"
FROM   distinct_tokens;
