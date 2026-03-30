-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- version:ValueTypes.Int:false
-- type:ValueTypes.String:false
-- invitedBy:ValueTypes.String:false
-- avatar:ValueTypes.String:false
-- tokenId:ValueTypes.String:false
-- name:ValueTypes.String:false
-- cidV0Digest:ValueTypes.Bytes:false

create or replace view "V_Crc_Avatars"
            ("blockNumber", timestamp, "transactionIndex", "logIndex", "transactionHash",
             version, type, "invitedBy", avatar, "tokenId", name, "cidV0Digest")
as
SELECT "V_CrcV2_Avatars"."blockNumber",
       "V_CrcV2_Avatars"."timestamp",
       "V_CrcV2_Avatars"."transactionIndex",
       "V_CrcV2_Avatars"."logIndex",
       "V_CrcV2_Avatars"."transactionHash",
       2 AS version,
       "V_CrcV2_Avatars".type,
       "V_CrcV2_Avatars"."invitedBy",
       "V_CrcV2_Avatars".avatar,
       "V_CrcV2_Avatars"."tokenId",
       "V_CrcV2_Avatars".name,
       "V_CrcV2_Avatars"."cidV0Digest"
FROM "V_CrcV2_Avatars"
UNION ALL
SELECT "V_CrcV1_Avatars"."blockNumber",
       "V_CrcV1_Avatars"."timestamp",
       "V_CrcV1_Avatars"."transactionIndex",
       "V_CrcV1_Avatars"."logIndex",
       "V_CrcV1_Avatars"."transactionHash",
       1                        AS version,
       "V_CrcV1_Avatars".type,
       NULL::text               AS "invitedBy",
       "V_CrcV1_Avatars".avatar,
       "V_CrcV1_Avatars".token  AS "tokenId",
       NULL::text               AS name,
       "cidV0Digest"            AS "cidV0Digest"
FROM "V_CrcV1_Avatars";


create table if not exists public.ipfs_files
(
    id              serial                                 primary key,
    cid             text                                   not null unique,
    metadata_digest bytea                                  not null unique,
    payload         jsonb                                  not null,
    downloaded_at   timestamp with time zone default now() not null
);

create unique index if not exists idx_ipfs_files_metadata_digest
    on public.ipfs_files (metadata_digest);

create table if not exists public.ipfs_queue
(
    cid             text                     not null primary key,
    metadata_digest bytea                    not null unique,
    status          text                     not null,
    attempt_count   integer                  not null,
    next_retry      timestamp with time zone not null,
    last_error      text,
    updated_at      timestamp with time zone not null
);

create index if not exists ipfs_queue_next_retry_status_idx
    on public.ipfs_queue (next_retry, status);

create unique index if not exists id_ipfs_queue_metadata_digest
    on public.ipfs_queue (metadata_digest);

create or replace function public.base58_encode(data bytea) returns text
    immutable
    language plpgsql
as
$$
DECLARE
    alphabet CONSTANT text := '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
    leading_zeroes    int  := 0;
    bytes             int[]; -- the working big-endian byte array (0-255)
    next              int[];
    carry             int;
    q                 int;
    piece             int;
    result            text := '';
BEGIN
    -- count leading 0x00
    WHILE leading_zeroes < length(data)
        AND get_byte(data, leading_zeroes) = 0
        LOOP
            leading_zeroes := leading_zeroes + 1;
        END LOOP;

    -- copy non-zero payload into an int[]
    bytes := ARRAY(
            SELECT get_byte(data, i)
            FROM generate_series(leading_zeroes, length(data) - 1) AS i
             );

    -- main div-mod loop: base-256 → base-58
    WHILE array_length(bytes, 1) IS NOT NULL
        LOOP
            carry := 0;
            next := '{}';
            FOREACH piece IN ARRAY bytes
                LOOP
                    carry := carry * 256 + piece; -- 0 ≤ carry < 58*256
                    q := carry / 58; -- small; fits in int
                    carry := carry % 58;
                    IF array_length(next, 1) IS NOT NULL OR q <> 0 THEN
                        next := next || q;
                    END IF;
                END LOOP;
            result := substr(alphabet, carry + 1, 1) || result;
            bytes := next;
        END LOOP;

    RETURN repeat('1', leading_zeroes) || result;
END;
$$;

-- ================================================================
-- IPFS queue triggers — REMOVED
-- Profile content is now served by the profile pinning service
-- (PROFILE_CONTENT_SERVICE_URL). The ipfs_queue and ipfs_files tables
-- are kept for now but no longer written to.
-- Drop the triggers that previously inserted into ipfs_queue:
-- ================================================================
DROP TRIGGER IF EXISTS trg_enq_ipfs_v1 ON public."CrcV1_UpdateMetadataDigest";
DROP TRIGGER IF EXISTS trg_enq_ipfs_v2 ON public."CrcV2_UpdateMetadataDigest";
DROP FUNCTION IF EXISTS public.enqueue_ipfs_from_update();

CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS idx_ipfs_files_payload_profile_fts
    ON ipfs_files
        USING gin (
                   (
                       /* weight A → most important */
                       setweight(
                               to_tsvector('simple', coalesce(payload ->> 'name', '')),
                               'A'
                       )
                           ||
                           /* weight B → still counts, but less */
                       setweight(
                               to_tsvector('simple', coalesce(payload ->> 'description', '')),
                               'B'
                       )
                   )
                );


