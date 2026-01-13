using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for PathUtils.QuantizeSinkBoundFlows - the quantization logic
/// for invitation module transfers (96 CRC chunks).
/// </summary>
[TestFixture]
public class QuantizedModeTests
{
    // 96 CRC in 6-decimal precision = 96 * 10^12
    private const long Quanta96CRC = 96_000_000_000_000L;

    // Test addresses (just need unique IDs)
    private static int Node(int i) => AddressIdPool.IdOf($"0x{i:X40}");

    // ─────────────────────── Empty/Trivial Cases ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_EmptyPaths_ReturnsEmpty()
    {
        var paths = new List<List<SimpleEdge>>();
        var sink = Node(99);

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_NoSinkBoundPaths_ReturnsEmpty()
    {
        var source = Node(1);
        var other = Node(2);
        var sink = Node(99);
        var token = Node(100);

        // Path that doesn't end at sink - irrelevant for invitation flow
        var path = new List<SimpleEdge>
        {
            new(source, other, token, 1000) { Flow = 200_000_000_000_000L }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        // Non-sink-bound paths are excluded (they don't contribute to invitations)
        Assert.That(result, Is.Empty);
    }

    // ─────────────────────── Exact Multiple Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_ExactMultiple_Unchanged()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with exactly 96 CRC (1 quanta)
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = Quanta96CRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_Exact192CRC_TwoQuanta()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with exactly 192 CRC (2 quanta)
        long twoQuanta = 2 * Quanta96CRC;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = twoQuanta }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 2 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(twoQuanta));
    }

    // ─────────────────────── Rounding Down Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_NonMultiple_RoundsDown()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with 100 CRC (more than 1 quanta, less than 2)
        long hundredCRC = 100_000_000_000_000L;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = hundredCRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC), "Should round down to 96 CRC");
    }

    [Test]
    public void QuantizeSinkBoundFlows_250CRC_RoundsTo192()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with 250 CRC (between 2 and 3 quanta)
        long flow250CRC = 250_000_000_000_000L;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = flow250CRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 3 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC), "Should round down to 192 CRC");
    }

    // ─────────────────────── Exclusion Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_LessThanQuanta_Excluded()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with only 50 CRC (less than 1 quanta)
        long fiftyCRC = 50_000_000_000_000L;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = fiftyCRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty, "Path with < 96 CRC should be excluded");
    }

    [Test]
    public void QuantizeSinkBoundFlows_OneCRCLessThanQuanta_Excluded()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with 95.999... CRC (just under 1 quanta)
        long almostQuanta = Quanta96CRC - 1;
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = almostQuanta }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty, "Path with 95.999 CRC should be excluded");
    }

    // ─────────────────────── Multi-Edge Path Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_MultiEdgePath_AllEdgesQuantized()
    {
        var a = Node(1);
        var b = Node(2);
        var c = Node(3);
        var sink = Node(99);
        var token = Node(100);

        // Multi-hop path: A → B → C → Sink with 100 CRC
        long flow = 100_000_000_000_000L;
        var path = new List<SimpleEdge>
        {
            new(a, b, token, 1000) { Flow = flow },
            new(b, c, token, 1000) { Flow = flow },
            new(c, sink, token, 1000) { Flow = flow }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        // All edges in path should have the same quantized flow
        Assert.That(result[0], Has.All.Matches<SimpleEdge>(e => e.Flow == Quanta96CRC));
    }

    // ─────────────────────── Multiple Paths Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_MultiplePaths_EachQuantized()
    {
        var source = Node(1);
        var mid1 = Node(2);
        var mid2 = Node(3);
        var sink = Node(99);
        var token = Node(100);

        // Three paths with different flows
        var path1 = new List<SimpleEdge>
        {
            new(source, mid1, token, 1000) { Flow = 100_000_000_000_000L }, // 100 CRC → 96
            new(mid1, sink, token, 1000) { Flow = 100_000_000_000_000L }
        };
        var path2 = new List<SimpleEdge>
        {
            new(source, mid2, token, 1000) { Flow = 200_000_000_000_000L }, // 200 CRC → 192
            new(mid2, sink, token, 1000) { Flow = 200_000_000_000_000L }
        };
        var path3 = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 50_000_000_000_000L } // 50 CRC → excluded
        };

        var paths = new List<List<SimpleEdge>> { path1, path2, path3 };

        // Request 3 invites (3 × 96 = 288 CRC)
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 3 * Quanta96CRC);

        // path1 provides 1 quanta, path2 provides 2 quanta, path3 excluded
        Assert.That(result, Has.Count.EqualTo(2));

        // Total flow should be 288 CRC (3 quanta)
        var totalFlow = result.Sum(p => p[0].Flow);
        Assert.That(totalFlow, Is.EqualTo(3 * Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_TargetReached_StopsEarly()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Two paths, each with enough for 2 invites
        var path1 = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 200_000_000_000_000L } // 200 CRC
        };
        var path2 = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 200_000_000_000_000L } // 200 CRC
        };

        var paths = new List<List<SimpleEdge>> { path1, path2 };

        // Only request 2 invites (192 CRC)
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 2 * Quanta96CRC);

        // Should only use first path (which can provide 2 quanta)
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC));
    }

    // ─────────────────────── Target Invite Tests ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_RequestOneInvite_GetsOne()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with enough for 5 invites
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 500_000_000_000_000L }
        };
        var paths = new List<List<SimpleEdge>> { path };

        // Request only 1 invite
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_RequestThreeInvites_GetsThree()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with exactly 3 invites worth
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 3 * Quanta96CRC }
        };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 3 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(3 * Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_RequestMoreThanAvailable_GetsWhatsPossible()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        // Path with only 2 invites worth
        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 200_000_000_000_000L }
        };
        var paths = new List<List<SimpleEdge>> { path };

        // Request 5 invites, but only 2 are available
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 5 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC), "Should get 2 invites (all available)");
    }

    // ─────────────────────── Edge Cases ───────────────────────

    [Test]
    public void QuantizeSinkBoundFlows_ZeroInvitesRequested_ReturnsEmpty()
    {
        var source = Node(1);
        var sink = Node(99);
        var token = Node(100);

        var path = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 200_000_000_000_000L }
        };
        var paths = new List<List<SimpleEdge>> { path };

        // Request 0 invites
        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, 0L);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_EmptyPath_Skipped()
    {
        var sink = Node(99);

        var emptyPath = new List<SimpleEdge>();
        var paths = new List<List<SimpleEdge>> { emptyPath };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_MixedSinkAndNonSink_OnlySinkIncluded()
    {
        var source = Node(1);
        var other = Node(2);
        var sink = Node(99);
        var token = Node(100);

        // Path 1 ends at sink
        var pathToSink = new List<SimpleEdge>
        {
            new(source, sink, token, 1000) { Flow = 100_000_000_000_000L }
        };

        // Path 2 ends at other node (not sink) - irrelevant for invitation flow
        var pathToOther = new List<SimpleEdge>
        {
            new(source, other, token, 1000) { Flow = 100_000_000_000_000L }
        };

        var paths = new List<List<SimpleEdge>> { pathToSink, pathToOther };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, sink, Quanta96CRC, Quanta96CRC);

        // Only sink-bound path should be in result (non-sink paths are excluded)
        Assert.That(result, Has.Count.EqualTo(1));

        // Path to sink should be quantized to 96 CRC
        Assert.That(result[0][0].To, Is.EqualTo(sink));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }
}
