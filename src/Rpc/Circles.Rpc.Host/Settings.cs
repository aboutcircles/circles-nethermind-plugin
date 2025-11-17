namespace Circles.Rpc.Host;

/// <summary>
/// Settings for the Circles.Rpc.Host service.
/// </summary>
public class Settings : Circles.Index.Common.Settings
{
    public new readonly string NethermindRpcUrl =
        Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL")
        ?? "http://localhost:8545";

    public readonly string BalanceMode =
        Environment.GetEnvironmentVariable("BALANCE_MODE")
        ?? "live"; // "database" or "live"
}