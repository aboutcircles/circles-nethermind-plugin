// using Nethermind.Core;
//
// namespace Circles.Pathfinder.Host;
//
// public class Settings
// {
//     public readonly string CirclesRpcUrl =
//         Environment.GetEnvironmentVariable("CIRCLES_RPC_URL")
//         ?? throw new ArgumentException("CIRCLES_RPC_URL is not set.");
//
//     public readonly string IndexReadonlyDbConnectionString =
//         Environment.GetEnvironmentVariable("POSTGRES_READONLY_CONNECTION_STRING")
//         ?? throw new ArgumentException("POSTGRES_READONLY_CONNECTION_STRING is not set.");
//
//     public readonly string? LogDbConnectionString =
//         Environment.GetEnvironmentVariable("POSTGRES_LOGDB_CONNECTION_STRING");
//
//     public readonly int MaxConcurrentRequests = Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS") != null
//         ? int.Parse(Environment.GetEnvironmentVariable("MAX_CONCURRENT_REQUESTS")!)
//         : Environment.ProcessorCount * 2;
//
//     // Router address used for post-processing Avatar→Group transfers.
//     // The router node is tracked but not part of the capacity graph during pathfinding.
//     public readonly Address RouterAddress =
//         Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER") != null
//         ? new Address(Environment.GetEnvironmentVariable("V2_BASE_GROUP_ROUTER")!)
//         : new Address("0xdc287474114cc0551a81ddc2eb51783fbf34802f"); 
// }