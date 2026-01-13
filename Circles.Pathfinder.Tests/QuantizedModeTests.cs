using Circles.Pathfinder;
using Circles.Pathfinder.Edges;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for the quantized mode feature in PathUtils.QuantizeSinkBoundFlows.
/// Quantized mode ensures all sink-bound transfers are exact multiples of 96 CRC
/// (96 × 10^6 in 6-decimal precision) for the invitation module.
/// </summary>
[TestFixture]
public class QuantizedModeTests
{
    // 96 CRC in 6-decimal precision (pathfinder internal format)
    // Internal balances: 1 CRC = 10^6 units
    private const long Quanta96CRC = 96_000_000L;

    // Test node IDs
    private const int Source = 1;
    private const int Intermediate = 2;
    private const int Sink = 3;
    private const int Token1 = 100;
    private const int Token2 = 101;

    [Test]
    public void QuantizeSinkBoundFlows_ExactMultiple_ReturnsUnchanged()
    {
        // Path with exactly 96 CRC flow should remain unchanged
        var path = CreatePath(Source, Sink, Token1, Quanta96CRC);
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_DoubleQuanta_ReturnsTwoQuanta()
    {
        // Path with exactly 192 CRC (2 × 96) should remain unchanged
        var path = CreatePath(Source, Sink, Token1, 2 * Quanta96CRC);
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, 2 * Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_NonMultiple_RoundsDown()
    {
        // Path with 100 CRC should round down to 96 CRC
        long flow100CRC = 100_000_000L;
        var path = CreatePath(Source, Sink, Token1, flow100CRC);
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, flow100CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_LessThanQuanta_ExcludesPath()
    {
        // Path with 50 CRC (less than 96) should be excluded entirely
        long flow50CRC = 50_000_000L;
        var path = CreatePath(Source, Sink, Token1, flow50CRC);
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, flow50CRC);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_MultiplePaths_EachQuantized()
    {
        // Multiple paths: 100, 200, 50 CRC → returns paths with 96, 192 (50 excluded)
        long flow100CRC = 100_000_000L;
        long flow200CRC = 200_000_000L;
        long flow50CRC = 50_000_000L;

        var path1 = CreatePath(Source, Sink, Token1, flow100CRC);
        var path2 = CreatePath(Source, Sink, Token2, flow200CRC);
        var path3 = CreatePath(Source, Sink, Token1, flow50CRC);
        var paths = new List<List<SimpleEdge>> { path1, path2, path3 };

        // Set targetFlow high enough to include all valid paths
        long targetFlow = 1000_000_000L;
        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, targetFlow);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
        Assert.That(result[1][0].Flow, Is.EqualTo(192_000_000L)); // 2 × 96 CRC
    }

    [Test]
    public void QuantizeSinkBoundFlows_TargetFlowLimitsResults()
    {
        // With targetFlow = 96 CRC, only 1 invite should be returned
        long flow200CRC = 200_000_000L;
        var path = CreatePath(Source, Sink, Token1, flow200CRC);
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC)); // Only 1 quanta despite 2 available
    }

    [Test]
    public void QuantizeSinkBoundFlows_TargetFlow192_ReturnsTwoInvites()
    {
        // With targetFlow = 192 CRC, up to 2 invites should be returned
        long flow300CRC = 300_000_000L;
        var path = CreatePath(Source, Sink, Token1, flow300CRC);
        var paths = new List<List<SimpleEdge>> { path };

        long targetFlow = 2 * Quanta96CRC; // 192 CRC
        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, targetFlow);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC)); // 2 quanta
    }

    [Test]
    public void QuantizeSinkBoundFlows_MultiHopPath_AllEdgesQuantized()
    {
        // Multi-hop path: Source → Intermediate → Sink
        // All edges in the path should have the same quantized flow
        long flow150CRC = 150_000_000L;

        var edge1 = new SimpleEdge(Source, Intermediate, Token1, long.MaxValue) { Flow = flow150CRC };
        var edge2 = new SimpleEdge(Intermediate, Sink, Token1, long.MaxValue) { Flow = flow150CRC };
        var path = new List<SimpleEdge> { edge1, edge2 };
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, flow150CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Has.Count.EqualTo(2));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
        Assert.That(result[0][1].Flow, Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_PathNotEndingAtSink_Excluded()
    {
        // A path that doesn't end at the sink should be excluded
        var path = CreatePath(Source, Intermediate, Token1, Quanta96CRC);
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_EmptyPaths_ReturnsEmpty()
    {
        var paths = new List<List<SimpleEdge>>();

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_EmptyPathInList_Skipped()
    {
        var emptyPath = new List<SimpleEdge>();
        var validPath = CreatePath(Source, Sink, Token1, Quanta96CRC);
        var paths = new List<List<SimpleEdge>> { emptyPath, validPath };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, Quanta96CRC);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(Quanta96CRC));
    }

    [Test]
    public void QuantizeSinkBoundFlows_MaxTargetFlow_ReturnsAllPossible()
    {
        // Simulate discovery mode with max uint256-like targetFlow
        long flow100CRC = 100_000_000L;
        long flow200CRC = 200_000_000L;
        long flow300CRC = 300_000_000L;

        var path1 = CreatePath(Source, Sink, Token1, flow100CRC);
        var path2 = CreatePath(Source, Sink, Token2, flow200CRC);
        var path3 = CreatePath(Source, Sink, Token1, flow300CRC);
        var paths = new List<List<SimpleEdge>> { path1, path2, path3 };

        // Use max long as targetFlow (simulating max uint256)
        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, long.MaxValue);

        Assert.That(result, Has.Count.EqualTo(3));
        // Total quanta: 1 + 2 + 3 = 6 invites possible
        long totalFlow = result.Sum(p => p[0].Flow);
        Assert.That(totalFlow, Is.EqualTo(6 * Quanta96CRC)); // 576 CRC total
    }

    [Test]
    public void QuantizeSinkBoundFlows_ZeroTargetFlow_ReturnsEmpty()
    {
        var path = CreatePath(Source, Sink, Token1, Quanta96CRC);
        var paths = new List<List<SimpleEdge>> { path };

        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, 0);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void QuantizeSinkBoundFlows_PartiallyMeetsTarget()
    {
        // Request 3 invites but only 2 possible
        long flow200CRC = 200_000_000L;
        var path = CreatePath(Source, Sink, Token1, flow200CRC);
        var paths = new List<List<SimpleEdge>> { path };

        long targetFlow = 3 * Quanta96CRC; // Want 3 invites
        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, targetFlow);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0][0].Flow, Is.EqualTo(2 * Quanta96CRC)); // Only 2 possible
    }

    [Test]
    public void QuantizeSinkBoundFlows_StopsAtTargetAcrossMultiplePaths()
    {
        // Two paths each with 192 CRC capacity, but only request 288 CRC (3 invites)
        long flow192CRC = 192_000_000L;
        var path1 = CreatePath(Source, Sink, Token1, flow192CRC);
        var path2 = CreatePath(Source, Sink, Token2, flow192CRC);
        var paths = new List<List<SimpleEdge>> { path1, path2 };

        long targetFlow = 3 * Quanta96CRC; // Want 3 invites (288 CRC)
        var result = PathUtils.QuantizeSinkBoundFlows(paths, Sink, Quanta96CRC, targetFlow);

        // First path provides 2 quanta (192), second provides 1 quanta (96) = 3 total
        Assert.That(result, Has.Count.EqualTo(2));
        long totalFlow = result.Sum(p => p[0].Flow);
        Assert.That(totalFlow, Is.EqualTo(3 * Quanta96CRC));
    }

    /// <summary>
    /// Helper to create a single-edge path from source to destination
    /// </summary>
    private static List<SimpleEdge> CreatePath(int from, int to, int token, long flow)
    {
        var edge = new SimpleEdge(from, to, token, long.MaxValue) { Flow = flow };
        return new List<SimpleEdge> { edge };
    }
}
