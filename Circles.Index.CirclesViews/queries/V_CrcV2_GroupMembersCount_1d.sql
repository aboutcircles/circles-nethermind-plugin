-- COLUMNS:
-- group:ValueTypes.Address:true
-- timestamp:ValueTypes.BigInt:true
-- value:ValueTypes.Int:true

create or replace view public."V_CrcV2_GroupMembersCount_1d"("group","timestamp",value) as
with groups_trusts as (
    select
        t1."timestamp"::bigint                                   as start_epoch,
        t1."logIndex"::bigint                                    as log_index,
        t1.truster                                               as "group",
        t1.trustee,
        t1."expiryTime"::numeric                                 as expiry_numeric,
        lead(t1."timestamp"::bigint) over (
            partition by t1.truster, t1.trustee
            order by t1."timestamp"::bigint, t1."logIndex"::bigint
        )                                                        as next_epoch
    from "CrcV2_Trust" t1
    join "V_CrcV2_Groups" t2 on t2."group" = t1.truster
),
trust_intervals as (
    select
        gt."group",
        gt.start_epoch,
        case
            when gt.expiry_numeric < 10000000000 then
                case when gt.next_epoch is not null then least(gt.expiry_numeric::bigint, gt.next_epoch)
                     else gt.expiry_numeric::bigint end
            else gt.next_epoch
        end                                                      as end_epoch_exclusive
    from groups_trusts gt
    where gt.next_epoch is null or gt.next_epoch > gt.start_epoch
),
membership_changes as (
    select ti."group", ti.start_epoch as change_epoch,  1  as delta from trust_intervals ti
    union all
    select ti."group", ti.end_epoch_exclusive,        -1  as delta from trust_intervals ti
    where ti.end_epoch_exclusive is not null
),
changes_daily as (
    select
        (date_trunc('day', (to_timestamp(mc.change_epoch) at time zone 'UTC')) at time zone 'UTC')::timestamptz as "timestamp",
        mc."group",
        sum(mc.delta) as delta
    from membership_changes mc
    group by 1, mc."group"
),
range_per_group as (
    select "group", min("timestamp") as min_ts_utc
    from changes_daily
    group by "group"
),
calendar as (
    select
        r."group",
        gs::timestamptz as "timestamp"
    from range_per_group r
    cross join generate_series(
        r.min_ts_utc,
        (date_trunc('day', (current_timestamp at time zone 'UTC')) at time zone 'UTC')::timestamptz,
        interval '1 day'
    ) as gs
),
dense as (
    select c."group", c."timestamp", coalesce(d.delta,0) as delta
    from calendar c
    left join changes_daily d
      on d."group" = c."group" and d."timestamp" = c."timestamp"
)
select "group","timestamp",
       sum(delta) over (partition by "group" order by "timestamp") as value
from dense
order by "group","timestamp";