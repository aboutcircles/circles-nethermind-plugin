using Circles.Pathfinder.Tests.Helpers;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Assembly-level setup/teardown. Handles shared resource cleanup once
/// at the end of the entire test run instead of per-fixture.
/// SharedAnvilCache must be cleared BEFORE SharedGraphCache because
/// SharedAnvilCache registers sessions with SharedGraphCache — clearing
/// SharedGraphCache first would leave orphaned Anvil processes.
/// </summary>
[SetUpFixture]
public class GlobalTestSetup
{
    [OneTimeTearDown]
    public async Task GlobalTearDown()
    {
        await SharedAnvilCache.ClearAsync();
        await SharedGraphCache.ClearAsync();
    }
}
