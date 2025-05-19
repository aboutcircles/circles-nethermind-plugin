-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- type:ValueTypes.String:false
-- invitedBy:ValueTypes.String:false
-- avatar:ValueTypes.String:false
-- tokenId:ValueTypes.String:false
-- name:ValueTypes.String:false
-- cidV0Digest:ValueTypes.Bytes:false

create or replace view public."V_CrcV2_Avatars"
        ("blockNumber", timestamp, "transactionIndex", "logIndex",
            "transactionHash", type, "invitedBy", avatar, "tokenId", name, "cidV0Digest") as
WITH 
avatars AS (
    SELECT "CrcV2_RegisterOrganization"."blockNumber",
        "CrcV2_RegisterOrganization"."timestamp",
        "CrcV2_RegisterOrganization"."transactionIndex",
        "CrcV2_RegisterOrganization"."logIndex",
        "CrcV2_RegisterOrganization"."transactionHash",
        NULL::text                                AS "invitedBy",
        "CrcV2_RegisterOrganization".organization AS avatar,
        NULL::text                                AS "tokenId",
        "CrcV2_RegisterOrganization".name,
        'CrcV2_RegisterOrganization'              as type
    FROM "CrcV2_RegisterOrganization"
    UNION ALL
    SELECT "CrcV2_RegisterGroup"."blockNumber",
        "CrcV2_RegisterGroup"."timestamp",
        "CrcV2_RegisterGroup"."transactionIndex",
        "CrcV2_RegisterGroup"."logIndex",
        "CrcV2_RegisterGroup"."transactionHash",
        NULL::text                    AS "invitedBy",
        "CrcV2_RegisterGroup"."group" AS avatar,
        "CrcV2_RegisterGroup"."group" AS "tokenId",
        "CrcV2_RegisterGroup".name,
        'CrcV2_RegisterGroup'         as type
    FROM "CrcV2_RegisterGroup"
    UNION ALL
    SELECT "CrcV2_RegisterHuman"."blockNumber",
        "CrcV2_RegisterHuman"."timestamp",
        "CrcV2_RegisterHuman"."transactionIndex",
        "CrcV2_RegisterHuman"."logIndex",
        "CrcV2_RegisterHuman"."transactionHash",
        NULL::text                   AS "invitedBy",
        "CrcV2_RegisterHuman".avatar,
        "CrcV2_RegisterHuman".avatar AS "tokenId",
        NULL::text                   AS name,
        'CrcV2_RegisterHuman'        as type
    FROM "CrcV2_RegisterHuman"
)
SELECT a."blockNumber",
        a."timestamp",
        a."transactionIndex",
        a."logIndex",
        a."transactionHash",
        a.type,
        a."invitedBy",
        a.avatar,
        a."tokenId",
        a.name,
        cid."cidV0Digest"
FROM avatars a
         left join lateral (
    select "metadataDigest" as "cidV0Digest"
    from   "CrcV2_UpdateMetadataDigest" m
    where  m.avatar = a.avatar
    order  by m."blockNumber" desc,
              m."transactionIndex" desc,
              m."logIndex" desc
    limit  1
    ) cid on true;


create or replace function public.base58_encode(data bytea) returns text
    immutable
    language plpgsql
as
$$
DECLARE
    alphabet CONSTANT text := '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz';
    leading_zeroes int := 0;
    bytes    int[];   -- the working big-endian byte array (0-255)
    next     int[];
    carry    int;
    q        int;
    piece    int;
    result   text := '';
BEGIN
    -- count leading 0x00
    WHILE leading_zeroes < length(data)
        AND get_byte(data, leading_zeroes) = 0 LOOP
            leading_zeroes := leading_zeroes + 1;
        END LOOP;

    -- copy non-zero payload into an int[]
    bytes := ARRAY(
            SELECT get_byte(data, i)
            FROM generate_series(leading_zeroes, length(data) - 1) AS i
             );

    -- main div-mod loop: base-256 → base-58
    WHILE array_length(bytes, 1) IS NOT NULL LOOP
            carry := 0;
            next  := '{}';
            FOREACH piece IN ARRAY bytes LOOP
                    carry := carry * 256 + piece;   -- 0 ≤ carry < 58*256
                    q     := carry / 58;            -- small; fits in int
                    carry := carry % 58;
                    IF array_length(next, 1) IS NOT NULL OR q <> 0 THEN
                        next := next || q;
                    END IF;
                END LOOP;
            result := substr(alphabet, carry + 1, 1) || result;
            bytes  := next;
        END LOOP;

    RETURN repeat('1', leading_zeroes) || result;
END;
$$;