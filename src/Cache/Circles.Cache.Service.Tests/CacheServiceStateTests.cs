using Circles.Cache.Service;
using FluentAssertions;
using Xunit;

namespace Circles.Cache.Service.Tests;

public class CacheServiceStateTests
{
    [Fact]
    public void Defaults_ShouldStartUnset()
    {
        var state = new CacheServiceState(rollbackCapacity: 4);

        state.LastProcessedBlock.Should().Be(-1);
        state.WarmupComplete.Should().BeFalse();
        state.ListenerConnected.Should().BeFalse();
        state.WarmupTargetBlock.Should().Be(-1);
        state.BlockRingBuffer.Should().NotBeNull();
        state.BlockRingBuffer.Count.Should().Be(0);
    }

    [Fact]
    public void IsReady_ShouldRequireWarmupListenerAndAcceptableLag()
    {
        var state = new CacheServiceState(rollbackCapacity: 4)
        {
            LastProcessedBlock = 95
        };

        state.IsReady(dbHead: 100, maxLag: 5).Should().BeFalse("warmup incomplete");

        state.WarmupComplete = true;
        state.IsReady(100, 5).Should().BeFalse("listener not connected");

        state.ListenerConnected = true;
        state.IsReady(dbHead: 110, maxLag: 2).Should().BeFalse("lag too large");

        state.LastProcessedBlock = 108;
        state.IsReady(dbHead: 110, maxLag: 2).Should().BeTrue();
    }

    [Fact]
    public void GetLag_ShouldReportDifferenceFromDbHead()
    {
        var state = new CacheServiceState(rollbackCapacity: 2)
        {
            LastProcessedBlock = 250
        };

        state.GetLag(260).Should().Be(10);
    }
}
