using System;
using System.Collections.Generic;
using System.Linq;
using Circles.Cache.Service;
using FluentAssertions;
using Xunit;

namespace Circles.Cache.Service.Tests;

public class BlockRingBufferTests
{
    [Fact]
    public void Add_ShouldTrimToCapacity()
    {
        var buffer = new BlockRingBuffer(capacity: 3);

        buffer.Add(1, "0x01");
        buffer.Add(2, "0x02");
        buffer.Add(3, "0x03");
        buffer.Add(4, "0x04");

        buffer.Count.Should().Be(3);
        var snapshot = buffer.GetSnapshot();
        snapshot.Should().HaveCount(3);
        snapshot.First().BlockNumber.Should().Be(2);
        snapshot.Last().BlockNumber.Should().Be(4);
    }

    [Fact]
    public void Add_ShouldRequireStrictlyIncreasingBlocks()
    {
        var buffer = new BlockRingBuffer(capacity: 2);

        buffer.Add(5, "0x05");
        Action act = () => buffer.Add(4, "0x04");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DetectReorg_ShouldReturnBlockNumberOnHashMismatch()
    {
        var buffer = new BlockRingBuffer(capacity: 4);
        buffer.Add(10, "0xAAA");

        var reorgPoint = buffer.DetectReorg(10, "0xBBB");
        reorgPoint.Should().Be(10);

        buffer.DetectReorg(11, "0xCCC").Should().BeNull();
    }

    [Fact]
    public void UpdateFromBlocks_ShouldDetectReorgAndReplaceState()
    {
        var buffer = new BlockRingBuffer(capacity: 5);
        buffer.Add(20, "0xAAA");
        buffer.Add(21, "0xBBB");
        buffer.Add(22, "0xCCC");

        var newBlocks = new List<(long BlockNumber, string BlockHash)>
        {
            (21, "0xDDD"), // change hash to simulate reorg
            (22, "0xEEE"),
            (23, "0xFFF")
        };

        var reorgPoint = buffer.UpdateFromBlocks(newBlocks);

        reorgPoint.Should().Be(21);
        var snapshot = buffer.GetSnapshot();
        snapshot.Should().HaveCount(4);
        snapshot.First().BlockNumber.Should().Be(20);
        snapshot.Skip(1).Select(b => b.BlockHash)
            .Should().ContainInOrder("0xDDD", "0xEEE", "0xFFF");
    }

    [Fact]
    public void Rollback_ShouldRemoveBlocksGreaterOrEqualThreshold()
    {
        var buffer = new BlockRingBuffer(capacity: 5);
        buffer.Add(30, "0xAAA");
        buffer.Add(31, "0xBBB");
        buffer.Add(32, "0xCCC");

        var removed = buffer.Rollback(31);

        removed.Should().Be(2);
        buffer.Count.Should().Be(1);
        buffer.LatestBlockNumber.Should().Be(30);
    }
}
