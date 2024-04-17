using Circles.Index.Indexer;
using Nethermind.Int256;

namespace Circles.Index.Data;

public abstract class BufferSink : ISnapshotSource, ISink
{
    public ISnapshot<Block> Blocks => _blockData;
    private readonly InsertBuffer<Block> _blockData = new();

    public ISnapshot<CirclesSignupData> CirclesSignup => _circlesSignupData;
    private readonly InsertBuffer<CirclesSignupData> _circlesSignupData = new();

    public ISnapshot<CirclesTrustData> CirclesTrust => _circlesTrustData;
    private readonly InsertBuffer<CirclesTrustData> _circlesTrustData = new();

    public ISnapshot<CirclesHubTransferData> CirclesHubTransfer => _circlesHubTransferData;
    private readonly InsertBuffer<CirclesHubTransferData> _circlesHubTransferData = new();

    public ISnapshot<Erc20TransferData> Erc20Transfer => _erc20TransferData;
    private readonly InsertBuffer<Erc20TransferData> _erc20TransferData = new();

    public ISnapshot<CrcV2ConvertInflationData> CrcV2ConvertInflation => _crcV2ConvertInflationData;
    private readonly InsertBuffer<CrcV2ConvertInflationData> _crcV2ConvertInflationData = new();

    public ISnapshot<CrcV2InviteHumanData> CrcV2InviteHuman => _crcV2InviteHumanData;
    private readonly InsertBuffer<CrcV2InviteHumanData> _crcV2InviteHumanData = new();

    public ISnapshot<CrcV2PersonalMintData> CrcV2PersonalMint => _crcV2PersonalMintData;
    private readonly InsertBuffer<CrcV2PersonalMintData> _crcV2PersonalMintData = new();

    public ISnapshot<CrcV2RegisterGroupData> CrcV2RegisterGroup => _crcV2RegisterGroupData;
    private readonly InsertBuffer<CrcV2RegisterGroupData> _crcV2RegisterGroupData = new();

    public ISnapshot<CrcV2RegisterHumanData> CrcV2RegisterHuman => _crcV2RegisterHumanData;
    private readonly InsertBuffer<CrcV2RegisterHumanData> _crcV2RegisterHumanData = new();

    public ISnapshot<CrcV2RegisterOrganizationData> CrcV2RegisterOrganization => _crcV2RegisterOrganizationData;
    private readonly InsertBuffer<CrcV2RegisterOrganizationData> _crcV2RegisterOrganizationData = new();

    public ISnapshot<CrcV2TrustData> CrcV2Trust => _crcV2TrustData;
    private readonly InsertBuffer<CrcV2TrustData> _crcV2TrustData = new();

    public ISnapshot<CrcV2StoppedData> CrcV2Stopped => _crcV2StoppedData;
    private readonly InsertBuffer<CrcV2StoppedData> _crcV2StoppedData = new();

    public ISnapshot<Erc1155TransferBatchData> Erc1155TransferBatch => _erc1155TransferBatchData;
    private readonly InsertBuffer<Erc1155TransferBatchData> _erc1155TransferBatchData = new();

    public ISnapshot<Erc1155TransferSingleData> Erc1155TransferSingle => _erc1155TransferSingleData;
    private readonly InsertBuffer<Erc1155TransferSingleData> _erc1155TransferSingleData = new();

    public ISnapshot<Erc1155ApprovalForAllData> Erc1155ApprovalForAll => _erc1155ApprovalForAllData;
    private readonly InsertBuffer<Erc1155ApprovalForAllData> _erc1155ApprovalForAllData = new();

    public ISnapshot<Erc1155UriData> Erc1155Uri => _erc1155UriData;
    private readonly InsertBuffer<Erc1155UriData> _erc1155UriData = new();

    public void AddBlock(long blockNumber, long timestamp, string blockHash)
    {
        _blockData.Add(new(blockNumber, timestamp, blockHash));
    }

    public void AddCirclesSignup(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string circlesAddress, string? tokenAddress)
    {
        _circlesSignupData.Add(new CirclesSignupData(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, circlesAddress,
            tokenAddress));
    }

    public void AddCirclesTrust(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string userAddress, string canSendToAddress, int limit)
    {
        _circlesTrustData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, userAddress,
            canSendToAddress, limit));
    }

    public void AddCirclesHubTransfer(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string fromAddress, string toAddress, UInt256 amount)
    {
        _circlesHubTransferData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, fromAddress,
            toAddress, amount
        ));
    }

    public void AddErc20Transfer(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string tokenAddress, string from, string to, UInt256 value)
    {
        _erc20TransferData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, tokenAddress, from,
            to, value
        ));
    }


    public void AddCrcV2RegisterOrganization(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string orgAddress, string orgName)
    {
        _crcV2RegisterOrganizationData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, orgAddress,
            orgName
        ));
    }

    public void AddCrcV2RegisterGroup(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string groupAddress, string mintPolicy, string treasury, string groupName,
        string groupSymbol)
    {
        _crcV2RegisterGroupData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, groupAddress,
            mintPolicy, treasury, groupName, groupSymbol
        ));
    }

    public void AddCrcV2RegisterHuman(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string humanAddress)
    {
        _crcV2RegisterHumanData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash
            , humanAddress
        ));
    }

    public void AddCrcV2PersonalMint(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string toAddress, UInt256 amount, UInt256 startPeriod, UInt256 endPeriod)
    {
        _crcV2PersonalMintData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, toAddress, amount, startPeriod,
            endPeriod
        ));
    }

    public void AddCrcV2InviteHuman(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string inviterAddress, string inviteeAddress)
    {
        _crcV2InviteHumanData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, inviterAddress, inviteeAddress
        ));
    }

    public void AddCrcV2ConvertInflation(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, UInt256 inflationValue, UInt256 demurrageValue, ulong day)
    {
        _crcV2ConvertInflationData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, inflationValue, demurrageValue, day
        ));
    }

    public void AddCrcV2Trust(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string userAddress, string canSendToAddress, UInt256 limit)
    {
        _crcV2TrustData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, userAddress, canSendToAddress, limit
        ));
    }

    public void AddCrcV2Stopped(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string address)
    {
        _crcV2StoppedData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, address
        ));
    }

    public void AddErc1155TransferSingle(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string operatorAddress, string fromAddress, string toAddress, UInt256 id, UInt256 value)
    {
        _erc1155TransferSingleData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, operatorAddress, fromAddress, toAddress, id, value
        ));
    }
    public void AddErc1155TransferBatch(long blockNumber, long timestamp, int transactionIndex, int logIndex, int batchIndex,
        string transactionHash, string operatorAddress, string fromAddress, string toAddress, UInt256 id,
        UInt256 amount)
    {
        _erc1155TransferBatchData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, batchIndex, transactionHash, operatorAddress, fromAddress, toAddress, id, amount
        ));
    }

    public void AddErc1155Uri(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string tokenAddress, UInt256 id, string uri)
    {
        _erc1155UriData.Add(new(blockNumber, timestamp, transactionIndex, logIndex, transactionHash, id, uri));
    }

    public void AddErc1155ApprovalForAll(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string ownerAddress, string operatorAddress, bool approved)
    {
        _erc1155ApprovalForAllData.Add(new(
            blockNumber, timestamp, transactionIndex, logIndex, transactionHash, ownerAddress, operatorAddress, approved
        ));
    }

    public abstract Task Flush();
}