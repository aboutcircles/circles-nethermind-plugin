using Circles.Index.Indexer;

namespace Circles.Index.Data;

public interface ISnapshotSource
{
    ISnapshot<Block> Blocks { get; }
    ISnapshot<CirclesSignupData> CirclesSignup { get; }
    ISnapshot<CirclesTrustData> CirclesTrust { get; }
    ISnapshot<CirclesHubTransferData> CirclesHubTransfer { get; }
    ISnapshot<Erc20TransferData> Erc20Transfer { get; }
    ISnapshot<CrcV2ConvertInflationData> CrcV2ConvertInflation { get; }
    ISnapshot<CrcV2InviteHumanData> CrcV2InviteHuman { get; }
    ISnapshot<CrcV2PersonalMintData> CrcV2PersonalMint { get; }
    ISnapshot<CrcV2RegisterGroupData> CrcV2RegisterGroup { get; }
    ISnapshot<CrcV2RegisterHumanData> CrcV2RegisterHuman { get; }
    ISnapshot<CrcV2RegisterOrganizationData> CrcV2RegisterOrganization { get; }
    ISnapshot<CrcV2TrustData> CrcV2Trust { get; }
    ISnapshot<CrcV2StoppedData> CrcV2Stopped { get; }
    ISnapshot<Erc1155TransferBatchData> Erc1155TransferBatch { get; }
    ISnapshot<Erc1155TransferSingleData> Erc1155TransferSingle { get; }
    ISnapshot<Erc1155ApprovalForAllData> Erc1155ApprovalForAll { get; }
    ISnapshot<Erc1155UriData> Erc1155Uri { get; }
}