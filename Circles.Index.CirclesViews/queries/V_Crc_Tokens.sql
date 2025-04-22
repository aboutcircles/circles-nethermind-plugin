-- COLUMNS:
-- blockNumber:ValueTypes.Int:true
-- timestamp:ValueTypes.Int:true
-- transactionIndex:ValueTypes.Int:true
-- logIndex:ValueTypes.Int:true
-- transactionHash:ValueTypes.String:true
-- version:ValueTypes.Int:false
-- type:ValueTypes.String:false
-- token:ValueTypes.String:true
-- tokenOwner:ValueTypes.String:true

create or replace view "V_Crc_Tokens" as
select 
    "blockNumber",
    "timestamp",
    "transactionIndex",
    "logIndex",
    "transactionHash",
    version,
    type,
    "tokenId" as token,
    "avatar" as "tokenOwner"
from "V_Crc_Avatars"
where "tokenId" is not null
union all 
select 
    "blockNumber",
    "timestamp",
    "transactionIndex",
    "logIndex",
    "transactionHash",
    2,
    'CrcV2_ERC20WrapperDeployed_Inflationary' as type,
    "erc20Wrapper" as token,
    "avatar" as "tokenOwner"
from "CrcV2_ERC20WrapperDeployed"
where "circlesType" = 1
union all
select 
    "blockNumber",
    "timestamp",
    "transactionIndex",
    "logIndex",
    "transactionHash",
    2,
    'CrcV2_ERC20WrapperDeployed_Demurraged' as type,
    "erc20Wrapper" as token,
    "avatar" as "tokenOwner"
from "CrcV2_ERC20WrapperDeployed"
where "circlesType" = 0;