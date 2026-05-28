WITH latest_score_group AS (
    SELECT DISTINCT ON ("group")
        "group" AS group_address,
        "pathMintRouter" AS router_address
    FROM "CrcV2_ScoreGroup_GroupInitialized"
    WHERE LOWER("emitter") = ANY(@scoreMintPolicies)
    ORDER BY "group", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC
),
supported_groups AS (
    SELECT
        g."group" AS group_address
    FROM "CrcV2_RegisterGroup" g
    LEFT JOIN latest_score_group sg ON sg.group_address = g."group"
    WHERE g."mint" = LOWER(@mintPolicy)
       OR (
           LOWER(g."mint") = ANY(@scoreMintPolicies)
           AND sg.router_address IS NOT NULL
       )
)
SELECT
    group_address
FROM supported_groups;
