using Nethermind.Core.Crypto;

namespace Circles.Index.V2;

public static class StaticResources
{
    public static Hash256 CrcV2RegisterHumanTopic { get; } = Keccak.Compute("RegisterHuman(address)");
    public static Hash256 CrcV2InviteHumanTopic { get; } = Keccak.Compute("InviteHuman(address,address)");

    public static Hash256 CrcV2RegisterOrganizationTopic { get; } =
        Keccak.Compute("RegisterOrganization(address,string)");

    public static Hash256 CrcV2RegisterGroupTopic { get; } =
        Keccak.Compute("RegisterGroup(address,address,address,string,string)");

    public static Hash256 CrcV2TrustTopic { get; } = Keccak.Compute("Trust(address,address,uint256)");
    public static Hash256 CrcV2StoppedTopic { get; } = Keccak.Compute("Stopped(address)");

    public static Hash256 CrcV2PersonalMintTopic { get; } =
        Keccak.Compute("PersonalMint(address,uint256,uint256,uint256)");

    public static Hash256 CrcV2ConvertInflationTopic { get; } =
        Keccak.Compute("ConvertInflation(uint256,uint256,uint64");

    // All ERC1155 events
    public static Hash256 Erc1155TransferSingleTopic { get; } =
        Keccak.Compute("TransferSingle(address,address,address,uint256,uint256)");

    public static Hash256 Erc1155TransferBatchTopic { get; } =
        Keccak.Compute("TransferBatch(address,address,address,uint256[],uint256[])");

    public static Hash256 Erc1155ApprovalForAllTopic { get; } = Keccak.Compute("ApprovalForAll(address,address,bool)");
    public static Hash256 Erc1155UriTopic { get; } = Keccak.Compute("URI(uint256,string)");
}