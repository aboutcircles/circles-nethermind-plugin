-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- affiliateGroup:ValueTypes.Address:true
-- avatar:ValueTypes.Address:true

-- Current affiliate-group membership ("willingness") from the MultiAffiliateGroupRegistry.
--
-- An avatar is currently affiliated with a group when the LATEST event for that (avatar,
-- affiliateGroup) pair is an AffiliateGroupAdded (not a Removed). Latest-event-wins (like
-- V_CrcV2_TrustRelations) handles add -> remove -> add re-adds, which a naive added-minus-removed
-- count could not.
--
-- initialize() seeding edge case: the registry's deployer-only initialize() OVERWRITES an avatar's
-- list to a single group but emits only AffiliateGroupAdded (no Removed for displaced groups). The
-- LogParser flags those Addeds with isSeed=true. We treat the latest seed per avatar as a hard RESET
-- point and only consider that avatar's events at/after it — so a seed correctly collapses the
-- avatar's set to the seeded group (+ any later self add/remove), matching the on-chain linked list.
-- Avatars never seeded behave exactly as plain latest-event-wins.
-- Queryable both directions (by avatar and by affiliateGroup); timestamp is that of the winning Added.
create or replace view public."V_CrcV2_AffiliateGroupMembers"
    ("blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash", "affiliateGroup", avatar) as
with events as (
    select "blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash",
           "affiliateGroup", avatar, true as is_add, "isSeed" as is_seed
    from "CrcV2_AffiliateGroupAdded"
    union all
    select "blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash",
           "affiliateGroup", avatar, false as is_add, false as is_seed
    from "CrcV2_AffiliateGroupRemoved"
),
resets as (
    -- latest initialize() seed per avatar = hard reset point
    select distinct on (avatar)
        avatar, "blockNumber" as rb, "transactionIndex" as rt, "logIndex" as rl
    from events
    where is_seed
    order by avatar, "blockNumber" desc, "transactionIndex" desc, "logIndex" desc
),
scoped as (
    select e.*
    from events e
    left join resets r on r.avatar = e.avatar
    where r.avatar is null
       or (e."blockNumber", e."transactionIndex", e."logIndex") >= (r.rb, r.rt, r.rl)
),
ranked as (
    select s.*,
           row_number() over (
               partition by s.avatar, s."affiliateGroup"
               order by s."blockNumber" desc, s."transactionIndex" desc, s."logIndex" desc
           ) as rn
    from scoped s
)
select "blockNumber", "timestamp", "transactionIndex", "logIndex", "transactionHash", "affiliateGroup", avatar
from ranked
where rn = 1
  and is_add = true
