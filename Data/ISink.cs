using Nethermind.Int256;

namespace Circles.Index.Data;

public interface ISink
{
    void AddBlock(long blockNumber, long timestamp, string blockHash);

    void AddCirclesSignup(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string circlesAddress, string? tokenAddress);

    void AddCirclesTrust(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string userAddress, string canSendToAddress, int limit);

    void AddCirclesHubTransfer(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string fromAddress, string toAddress, UInt256 amount);

    void AddErc20Transfer(long blockNumber, long timestamp, int transactionIndex, int logIndex,
        string transactionHash, string tokenAddress, string from, string to, UInt256 value);

    void AddCrcV2RegisterOrganization(long blockNumber, long blockTimestamp, int receiptIndex, int logIndex,
        string transactionHash, string orgAddress, string orgName);

    void AddCrcV2RegisterGroup(long blockNumber, long blockTimestamp, int receiptIndex, int logIndex,
        string transactionHash, string groupAddress, string mintPolicy, string treasury, string groupName,
        string groupSymbol);

    void AddCrcV2RegisterHuman(long blockNumber, long blockTimestamp, int receiptIndex, int logIndex,
        string transactionHash, string humanAddress);

    void AddCrcV2PersonalMint(long blockNumber, long blockTimestamp, int receiptIndex, int logIndex,
        string transactionHash, string toAddress, UInt256 amount, UInt256 startPeriod, UInt256 endPeriod);

    void AddCrcV2InviteHuman(long blockNumber, long blockTimestamp, int receiptIndex, int logIndex,
        string transactionHash, string inviterAddress, string inviteeAddress);

    void AddCrcV2ConvertInflation(long blockNumber, long blockTimestamp, int receiptIndex, int logIndex,
        string transactionHash, UInt256 inflationValue, UInt256 demurrageValue, ulong day);

    void AddCrcV2Trust(long blockNumber, long blockTimestamp, int receiptIndex, int logIndex,
        string transactionHash, string userAddress, string canSendToAddress, UInt256 limit);

    void AddCrcV2Stopped(long blockNumber, long blockTimestamp, int receiptIndex, int logIndex,
        string transactionHash, string address);

    Task Flush();
}