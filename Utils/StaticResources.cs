using System.Collections.Immutable;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Circles.Index.Utils;

public static class StaticResources
{
    public const int AddressEmptyBytesPrefixLength = 26;

    public static Hash256 CrcHubTransferEventTopic { get; } = Keccak.Compute("HubTransfer(address,address,uint256)");
    public static Hash256 CrcTrustEventTopic { get; } = Keccak.Compute("Trust(address,address,uint256)");
    public static Hash256 CrcSignupEventTopic { get; } = Keccak.Compute("Signup(address,address)");
    public static Hash256 CrcOrganisationSignupEventTopic { get; } = Keccak.Compute("OrganisationSignup(address)");
    public static Hash256 Erc20TransferTopic { get; } = Keccak.Compute("Transfer(address,address,uint256)");

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
    public static Hash256 Erc1155URITopic { get; } = Keccak.Compute("URI(uint256,string)");
}