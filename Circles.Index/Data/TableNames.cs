namespace Circles.Index.Data;

public static class TableNames
{
    public const string Block = "block";
    public const string CrcV1Signup = "crc_v1_signup";
    public const string CrcV1Trust = "crc_v1_trust";
    public const string CrcV1HubTransfer = "crc_v1_hub_transfer";
    public const string Erc20Transfer = "erc20_transfer";
    public const string CrcV2RegisterHuman = "crc_v2_register_human";
    public const string CrcV2InviteHuman = "crc_v2_invite_human";
    public const string CrcV2RegisterOrganization = "crc_v2_register_organization";
    public const string CrcV2RegisterGroup = "crc_v2_register_group";
    public const string CrcV2PersonalMint = "crc_v2_personal_mint";
    public const string CrcV2ConvertInflation = "crc_v2_convert_inflation";
    public const string CrcV2Trust = "crc_v2_trust";
    public const string CrcV2Stopped = "crc_v2_stopped";
    public const string Erc1155Uri = "erc1155_uri";
    public const string Erc1155TransferSingle = "erc1155_transfer_single";
    public const string Erc1155TransferBatch = "erc1155_transfer_batch";
    public const string Erc1155ApprovalForAll = "erc1155_approval_for_all";
    
    public static readonly string[] AllTableNames =
    [
        Block, CrcV1Signup, CrcV1Trust, CrcV1HubTransfer, Erc20Transfer, CrcV2RegisterHuman, CrcV2InviteHuman,
        CrcV2RegisterOrganization, CrcV2RegisterGroup, CrcV2PersonalMint, CrcV2ConvertInflation, CrcV2Trust, CrcV2Stopped,
        Erc1155Uri, Erc1155TransferSingle, Erc1155TransferBatch, Erc1155ApprovalForAll
    ];
}