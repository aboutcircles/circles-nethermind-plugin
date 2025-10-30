using Circles.Index.Common;
using Nethermind.Int256;

namespace Circles.Index.CirclesV2.Erc20Lift;

public record CreateVault(
    long BlockNumber,
    long Timestamp,
    int TransactionIndex,
    int LogIndex,
    string TransactionHash,
    string Group,
    string Vault) : IIndexEvent;