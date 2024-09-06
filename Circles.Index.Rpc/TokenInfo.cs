using Nethermind.Core;

namespace Circles.Index.Rpc;

public record TokenInfo(
    Address TokenAddress,
    Address TokenOwner,
    string TokenType,
    int Version,
    bool IsErc20,
    bool IsErc1155,
    bool IsWrapped,
    bool IsInflationary);