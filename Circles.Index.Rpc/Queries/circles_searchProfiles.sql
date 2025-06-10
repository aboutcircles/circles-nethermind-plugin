WITH
    input(txt) AS (VALUES (@search)),
    q AS (
        SELECT to_tsquery(
                       'simple',
                       (
                           SELECT string_agg(quote_literal(tok) || ':*', ' & ')
                           FROM   unnest(string_to_array(txt, ' ')) AS tok
                       )
               ) AS query
        FROM input
    ),
    recv AS (
        SELECT "to"::text AS avatar, COUNT(*) AS receive_count
        FROM   "CrcV2_TransferSummary"
        GROUP  BY "to"
    ),
    /* ── avatars WITH profile ─────────────────────────────────────── */
    w_profile AS (
        SELECT  a.avatar,
                a."timestamp",
                a.name              AS avatar_name,
                rs."shortName"      AS short_name,
                f.metadata_digest,
                f.payload,
                ts_rank_cd(
                        ARRAY[1.0, 0.4, 0.2, 0.05],            -- A,B,C,D weights
                        (
                            setweight(to_tsvector('simple', coalesce(f.payload ->> 'name',        '')), 'A') ||
                            setweight(to_tsvector('simple', coalesce(f.payload ->> 'description', '')), 'B') ||
                            setweight(to_tsvector('simple', a.avatar),                                'C')
                            ),
                        q.query
                ) AS rank
        FROM   "V_CrcV2_Avatars"        a
                   LEFT   JOIN "CrcV2_RegisterShortName" rs ON rs.avatar = a.avatar
                   JOIN   ipfs_files                 f  ON f.metadata_digest = a."cidV0Digest"
                   CROSS  JOIN q
        WHERE (
                  setweight(to_tsvector('simple', coalesce(f.payload ->> 'name',        '')), 'A') ||
                  setweight(to_tsvector('simple', coalesce(f.payload ->> 'description', '')), 'B') ||
                  setweight(to_tsvector('simple', a.avatar),                                'C')
                  ) @@ q.query
    ),
    /* ── avatars WITHOUT profile ──────────────────────────────────── */
    wo_profile AS (
        SELECT  a.avatar,
                a."timestamp",
                a.name              AS avatar_name,
                rs."shortName"      AS short_name,
                NULL::bytea         AS metadata_digest,
                NULL::jsonb         AS payload,
                ts_rank_cd(
                        ARRAY[1.0, 0.4, 0.2, 0.05],
                        (
                            setweight(to_tsvector('simple', a.name),   'A') ||
                            setweight(to_tsvector('simple', a.avatar), 'C')
                            ),
                        q.query
                ) AS rank
        FROM   "V_CrcV2_Avatars"        a
                   LEFT   JOIN "CrcV2_RegisterShortName" rs ON rs.avatar = a.avatar
                   LEFT   JOIN ipfs_files             f  ON f.metadata_digest = a."cidV0Digest"
                   CROSS  JOIN q
        WHERE  f.metadata_digest IS NULL
          AND (
                  setweight(to_tsvector('simple', a.name),   'A') ||
                  setweight(to_tsvector('simple', a.avatar), 'C')
                  ) @@ q.query
    )
SELECT  p."timestamp",
        COALESCE(r.receive_count, 0) AS receive_count,
        p.avatar,
        p.avatar_name,
        p.short_name::text,
        p.metadata_digest,
        p.payload
FROM   (SELECT * FROM w_profile
        UNION ALL
        SELECT * FROM wo_profile) p
           LEFT   JOIN recv r USING (avatar)
ORDER  BY receive_count DESC, p.rank DESC
LIMIT  @limit
    OFFSET @offset;
