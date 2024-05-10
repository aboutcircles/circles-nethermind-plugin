using System.Collections.Immutable;
using Circles.Index.Common;
using Nethermind.Core;

namespace Circles.Index;

public class Settings
{
    public readonly Address CirclesV1HubAddress = Environment.GetEnvironmentVariable("V1_HUB_ADDRESS") != null
        ? new(Environment.GetEnvironmentVariable("V1_HUB_ADDRESS")!)
        : new("0x29b9a7fBb8995b2423a71cC17cf9810798F6C543");

    public readonly Address CirclesV2HubAddress = Environment.GetEnvironmentVariable("V2_HUB_ADDRESS") != null
        ? new(Environment.GetEnvironmentVariable("V2_HUB_ADDRESS")!)
        : new("0x29b9a7fBb8995b2423a71cC17cf9810798F6C543");

    public readonly string IndexDbConnectionString =
        Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
        ?? "Server=postgres;Port=5432;User Id=postgres;Password=postgres;Database=postgres;";

    public readonly int BlockBufferSize = 20000;
    public readonly int EventBufferSize = 100000;

    public readonly long StartBlock = Environment.GetEnvironmentVariable("START_BLOCK") != null
        ? long.Parse(Environment.GetEnvironmentVariable("START_BLOCK")!)
        : 12541946L;
}