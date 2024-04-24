using Nethermind.Core.Crypto;

namespace Circles.Index.V1;

public static class StaticResources
{
    public static Hash256 CrcHubTransferEventTopic { get; } = Keccak.Compute("HubTransfer(address,address,uint256)");
    public static Hash256 CrcTrustEventTopic { get; } = Keccak.Compute("Trust(address,address,uint256)");
    public static Hash256 CrcSignupEventTopic { get; } = Keccak.Compute("Signup(address,address)");
    public static Hash256 CrcOrganisationSignupEventTopic { get; } = Keccak.Compute("OrganisationSignup(address)");
    public static Hash256 Erc20TransferTopic { get; } = Keccak.Compute("Transfer(address,address,uint256)");
}