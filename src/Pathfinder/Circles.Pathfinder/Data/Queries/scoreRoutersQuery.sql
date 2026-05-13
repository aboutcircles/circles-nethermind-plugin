SELECT DISTINCT LOWER("pathMintRouter") AS router_address
FROM "CrcV2_ScoreGroup_GroupInitialized"
WHERE LOWER("emitter") = ANY(@scoreMintPolicies);
