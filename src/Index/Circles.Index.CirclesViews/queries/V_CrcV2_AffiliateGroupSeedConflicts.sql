-- COLUMNS:
-- avatar:ValueTypes.Address:true
-- seedBlock:ValueTypes.Int:true
-- priorEventCount:ValueTypes.Int:true

-- Safety net for the initialize() overwrite edge case.
--
-- The registry's deployer-only initialize() resets an avatar's affiliate list to a single group but
-- emits no AffiliateGroupRemoved for any groups the avatar had added before. V_CrcV2_AffiliateGroupMembers
-- already compensates (it treats a seed as a reset), so served membership stays correct — but a seed
-- landing on an avatar that had ALREADY interacted with the registry is operationally suspicious
-- (initialize is meant for one-time bulk seeding of fresh avatars). This view surfaces exactly those
-- cases: avatars with at least one affiliate event strictly BEFORE their latest seed. Expected to be
-- empty; a non-empty result (or the circles_affiliate_seed_conflicts metric going > 0) means a seed
-- overwrote prior willingness and should be investigated.
create or replace view public."V_CrcV2_AffiliateGroupSeedConflicts"
    (avatar, "seedBlock", "priorEventCount") as
with events as (
    select "blockNumber", "transactionIndex", "logIndex", avatar, "isSeed" as is_seed
    from "CrcV2_AffiliateGroupAdded"
    union all
    select "blockNumber", "transactionIndex", "logIndex", avatar, false as is_seed
    from "CrcV2_AffiliateGroupRemoved"
),
resets as (
    select distinct on (avatar)
        avatar, "blockNumber" as rb, "transactionIndex" as rt, "logIndex" as rl
    from events
    where is_seed
    order by avatar, "blockNumber" desc, "transactionIndex" desc, "logIndex" desc
)
select r.avatar, r.rb as "seedBlock", count(*)::int as "priorEventCount"
from resets r
join events e on e.avatar = r.avatar
where (e."blockNumber", e."transactionIndex", e."logIndex") < (r.rb, r.rt, r.rl)
group by r.avatar, r.rb
