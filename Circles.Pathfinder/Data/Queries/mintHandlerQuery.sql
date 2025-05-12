select "mintHandler",
       "group",
       t.token as "tokenAddress",
       t.type as "tokenType"
from "CrcV2_BaseGroupCreated" bg
join "V_Crc_Tokens" t on t."tokenOwner" = bg.group;