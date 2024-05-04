namespace Circles.Index.Common;

public enum Tables
{
    [DbIdentifier("block")] Block,
    [DbIdentifier("crc_v1_hub_transfer")] CrcV1HubTransfer,
    [DbIdentifier("crc_v1_signup")] CrcV1Signup,
    [DbIdentifier("crc_v1_trust")] CrcV1Trust,
    [DbIdentifier("crc_v2_convert_inflation")] CrcV2ConvertInflation,
    [DbIdentifier("crc_v2_invite_human")] CrcV2InviteHuman,
    [DbIdentifier("crc_v2_personal_mint")] CrcV2PersonalMint,
    [DbIdentifier("crc_v2_register_group")] CrcV2RegisterGroup,
    [DbIdentifier("crc_v2_register_human")] CrcV2RegisterHuman,
    [DbIdentifier("crc_v2_register_organization")] CrcV2RegisterOrganization,
    [DbIdentifier("crc_v2_stopped")] CrcV2Stopped,
    [DbIdentifier("crc_v2_trust")] CrcV2Trust,
    [DbIdentifier("erc20_transfer")] Erc20Transfer,
    [DbIdentifier("erc1155_approval_for_all")] Erc1155ApprovalForAll,
    [DbIdentifier("erc1155_transfer_batch")] Erc1155TransferBatch,
    [DbIdentifier("erc1155_transfer_single")] Erc1155TransferSingle,
    [DbIdentifier("erc1155_uri")] Erc1155Uri
}