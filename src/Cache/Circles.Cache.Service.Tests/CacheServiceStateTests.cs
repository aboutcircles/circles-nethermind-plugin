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
    public void ReorgDetectionWindow_DetectsReorgsDeeperThanRollbackCapacity()
    {
        // With the detection window decoupled from (and larger than) the rollback window, the
        // block ring buffer retains enough hashes to DETECT a reorg deeper than the rollback
        // window. The listener then triggers a safe full re-warmup instead of silently missing it.
        const int rollbackCapacity = 12;
        const int detectionWindow = 256;
        var wide = new CacheServiceState(rollbackCapacity, detectionWindow).BlockRingBuffer;
        for (long b = 1; b <= 200; b++) wide.Add(b, $"0xhash{b}");

        // A reorg 100 blocks deep (within the 256 detection window) is detected.
        wide.UpdateFromBlocks(new[] { (100L, "0xCHANGED") }).Should().Be(100L);

        // A buffer sized to the rollback capacity alone (the pre-decoupling behavior, i.e. no
        // detection window supplied) evicted block 100 long ago, so the same reorg is invisible.
        var narrow = new CacheServiceState(rollbackCapacity).BlockRingBuffer;
        for (long b = 1; b <= 200; b++) narrow.Add(b, $"0xhash{b}");
        narrow.UpdateFromBlocks(new[] { (100L, "0xCHANGED") }).Should().BeNull();
    }

    [Fact]
    public void DefaultCtor_WithoutDetectionWindow_SizesBufferToRollbackCapacity()
    {
        // The optional reorgDetectionWindow defaults to rollbackCapacity, preserving the
        // pre-decoupling behavior for every existing single-arg caller.
        var buf = new CacheServiceState(rollbackCapacity: 5).BlockRingBuffer;
        for (long b = 1; b <= 20; b++) buf.Add(b, $"0xhash{b}");
        // Only the last 5 blocks are retained, so a reorg at block 10 (15 deep) is not detected.
        buf.UpdateFromBlocks(new[] { (10L, "0xCHANGED") }).Should().BeNull();
        // ...but a reorg within the last 5 (block 16) is.
        buf.UpdateFromBlocks(new[] { (16L, "0xCHANGED") }).Should().Be(16L);
    }

    [Theory]
    [InlineData(12, 12, true)]    // window == capacity: allowed (boundary)
    [InlineData(12, 256, true)]   // window > capacity: allowed
    [InlineData(12, 11, false)]   // window < capacity: rejected
    [InlineData(12, 10001, false)] // window > max: rejected
    public void Validate_EnforcesReorgDetectionWindowBounds(int rollbackCapacity, int detectionWindow, bool valid)
    {
        var settings = new CacheServiceSettings
        {
            PostgresConnectionString = "Host=localhost",
            RollbackCapacity = rollbackCapacity,
            ReorgDetectionWindow = detectionWindow
        };

        var act = () => settings.Validate();
        if (valid) act.Should().NotThrow();
        else act.Should().Throw<InvalidOperationException>().WithMessage("*REORG_DETECTION_WINDOW*");
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
