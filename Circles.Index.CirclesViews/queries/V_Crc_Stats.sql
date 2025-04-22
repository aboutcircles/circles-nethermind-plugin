create or replace view "V_Crc_Stats"("measure", "value") as
select 'avatar_count_v1' as measure, count("user") as value
from "V_CrcV1_Avatars"
union all
select 'organization_count_v1' as measure, count("user") as value
from "V_CrcV1_Avatars"
where token is null
union all
select 'human_count_v1' as measure, count("user") as value
from "V_CrcV1_Avatars"
where token is not null
union all
select 'avatar_count_v2', count("avatar")
from "V_CrcV2_Avatars"
union all
select 'organization_count_v2', count(organization)
from "CrcV2_RegisterOrganization"
union all
select 'human_count_v2', count(avatar)
from "CrcV2_RegisterHuman"
union all
select 'group_count_v2', count("group")
from "CrcV2_RegisterGroup"
union all
select 'trust_count_v1',
        (SELECT COUNT(*)
        FROM (SELECT DISTINCT ON ("user", "canSendTo") "user", "canSendTo", "limit"
                FROM "CrcV1_Trust"
                ORDER BY "user", "canSendTo", "blockNumber" DESC, "transactionIndex" DESC, "logIndex" DESC) t
        WHERE "limit" > 0)
union all
select 'trust_count_v2', count(*)
from "V_CrcV2_TrustRelations"
union all
select 'token_count_v1', count(*)
from "V_Crc_Tokens"
where version = 1
union all
select 'token_count_v2', count(*)
from "V_Crc_Tokens"
where version = 2
union all
select 'transitive_transfer_count_v1', count(*)
from "CrcV1_HubTransfer"
union all
select 'transitive_transfer_count_v2', count(*)
from "CrcV2_StreamCompleted"
union all 
select 'circles_transfer_count_v1', count(*)
from "CrcV1_Transfer"
union all 
select 'circles_transfer_count_v2', (
    select sum(t.value) from (
        select count(*) as value
        from "CrcV2_TransferSingle"
        union all
        select count(*)
        from "CrcV2_TransferBatch"
    ) as t
)
union all 
select 'erc20_wrapper_token_count_v2', count(*)
from "CrcV2_ERC20WrapperDeployed";