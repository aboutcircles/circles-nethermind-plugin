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
       "V_CrcV1_Avatars"."user" AS avatar,
       "V_CrcV1_Avatars".token  AS "tokenId",
       NULL::text               AS name,
       "cidV0Digest"            AS "cidV0Digest"
FROM "V_CrcV1_Avatars";


create table if not exists ipfs_files
(
    id            serial
        primary key,
    cid           text                                   not null
        unique,
    payload       jsonb                                  not null,
    downloaded_at timestamp with time zone default now() not null
);

create table if not exists public.ipfs_queue
(
    cid           text                     not null
        primary key,
    status        text                     not null,
    attempt_count integer                  not null,
    next_retry    timestamp with time zone not null,
    last_error    text,
    updated_at    timestamp with time zone not null
);

create index if not exists ipfs_queue_next_retry_status_idx
    on public.ipfs_queue (next_retry, status);

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
-- ipfs_enqueue_triggers.sql
-- Automatically adds CID rows to ipfs_queue whenever metadata-
-- digest rows land in the V1/V2 tables.
--
-- Assumes a `base58_encode(bytea)` function is available.
-- ================================================================
-- ----------------------------------------------------------------
-- 1. Trigger function (statement-level, shared by both tables)
-- ----------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.enqueue_ipfs_from_update()
    RETURNS TRIGGER
    LANGUAGE plpgsql
AS
$$
BEGIN
    /*  Collect all distinct CIDs created by this INSERT and push only the
        ones we haven’t already downloaded or queued.  */

    WITH candidate_cids AS (SELECT DISTINCT base58_encode(decode('1220', 'hex') || nr."metadataDigest") AS cid
                            FROM new_rows nr
                            WHERE nr."metadataDigest" IS NOT NULL),
         missing AS (SELECT cid
                     FROM candidate_cids c
                     WHERE NOT EXISTS (SELECT 1 FROM ipfs_files f WHERE f.cid = c.cid)
                       AND NOT EXISTS (SELECT 1 FROM ipfs_queue q WHERE q.cid = c.cid))
    INSERT
    INTO ipfs_queue (cid, status, attempt_count, next_retry, updated_at)
    SELECT cid, 'PENDING', 0, NOW(), NOW()
    FROM missing
    ON CONFLICT DO NOTHING; -- double-insert safety

    RETURN NULL;
END;
$$;

-- ----------------------------------------------------------------
-- 2. Attach the trigger to both metadata-digest tables
-- ----------------------------------------------------------------
-- V1
DROP TRIGGER IF EXISTS trg_enq_ipfs_v1
    ON public."CrcV1_UpdateMetadataDigest";

CREATE TRIGGER trg_enq_ipfs_v1
    AFTER INSERT
    ON public."CrcV1_UpdateMetadataDigest"
    REFERENCING NEW TABLE AS new_rows
    FOR EACH STATEMENT
EXECUTE FUNCTION public.enqueue_ipfs_from_update();

-- V2
DROP TRIGGER IF EXISTS trg_enq_ipfs_v2
    ON public."CrcV2_UpdateMetadataDigest";

CREATE TRIGGER trg_enq_ipfs_v2
    AFTER INSERT
    ON public."CrcV2_UpdateMetadataDigest"
    REFERENCING NEW TABLE AS new_rows
    FOR EACH STATEMENT
EXECUTE FUNCTION public.enqueue_ipfs_from_update();