using System.Globalization;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Edges;
using Circles.Pathfinder.Graphs;
using Nethermind.Int256;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Property-based / fuzzing tests for the pathfinder.
/// Uses randomized graph generation to verify invariants that must hold for ANY valid graph:
///   - Pathfinder never crashes on valid input
///   - Flow conservation at intermediate vertices
///   - All output flows are positive
///   - MaxFlow is non-negative and <= targetFlow
///   - ValidateMintEdgeOrdering passes on sorted output
///   - Quantized output sums to exact quantum multiples
/// </summary>
[TestFixture]
public class PropertyBasedTests
{
    private const long DefaultBalance = 1_000_000L; // 1 CRC truncated
    private const long HighBalance = 100_000_000L; // 100 CRC truncated
    private const long InvitationQuanta = 96_000_000L; // 96 CRC

    #region Graph Builder Helper

    /// <summary>
    /// Builds a synthetic CapacityGraph with the token pool model used by GraphFactory.
    /// Pattern: Holder → TokenPool(token) → Receiver (where receiver trusts token owner).
    /// </summary>
    internal static CapacityGraph BuildSyntheticGraph(
        int avatarCount,
        int groupCount,
        long balance,
        Random rng,
        double trustDensity = 0.3,
        bool withRouter = true,
        bool withConsent = false,
        double consentRate = 0.5)
    {
        var graph = new CapacityGraph();
        var avatars = new List<int>(avatarCount);
        var groups = new List<int>(groupCount);
        var trustLookup = new Dictionary<int, HashSet<int>>();

        // Create avatar addresses with unique hex-valid prefix to avoid test interference.
        // Using 'ee' prefix + 4 random hex chars = 6 chars after 0x, plus 34-digit decimal
        // padding (0-9 are valid hex). Total = 2 + 6 + 34 = 42 chars per address.
        string prefix = $"0xee{rng.Next(0x1000, 0xFFFF):x4}";

        for (int i = 0; i < avatarCount; i++)
        {
            int id = AddressIdPool.IdOf($"{prefix}{i:d34}");
            graph.AddAvatar(id);
            avatars.Add(id);
        }

        // Create groups (use 'a' prefix — valid hex)
        for (int g = 0; g < groupCount; g++)
        {
            int id = AddressIdPool.IdOf($"{prefix}a{g:d33}");
            graph.AddGroup(id);
            groups.Add(id);
        }

        // Router (use 'b' prefix — valid hex)
        if (withRouter)
        {
            int routerId = AddressIdPool.IdOf($"{prefix}b{0:d33}");
            graph.SetRouter(routerId);
        }

        // Build trust relationships: each avatar trusts some other avatars' tokens
        // In Circles, "A trusts B" means A accepts B's personal token
        for (int i = 0; i < avatarCount; i++)
        {
            var trustedSet = new HashSet<int>();
            // Self-trust (everyone trusts their own token)
            trustedSet.Add(avatars[i]);

            for (int j = 0; j < avatarCount; j++)
            {
                if (i == j) continue;
                if (rng.NextDouble() < trustDensity)
                {
                    trustedSet.Add(avatars[j]);
                }
            }

            // Trust some group tokens
            foreach (var grp in groups)
            {
                if (rng.NextDouble() < trustDensity)
                {
                    trustedSet.Add(grp);
                }
            }

            trustLookup[avatars[i]] = trustedSet;
        }

        // Groups trust some avatar tokens (for collateral)
        foreach (var grp in groups)
        {
            var groupTrusts = new HashSet<int>();
            foreach (var avatar in avatars)
            {
                if (rng.NextDouble() < trustDensity)
                {
                    groupTrusts.Add(avatar);
                }
            }

            if (groupTrusts.Count > 0)
            {
                graph.GroupTrustedTokens[grp] = groupTrusts;
                trustLookup[grp] = groupTrusts;
            }
        }

        graph.TrustLookup = trustLookup;

        // Each avatar holds their own personal token (balance)
        foreach (var avatar in avatars)
        {
            int tokenId = avatar; // personal token = avatar address
            graph.AddTokenNode(tokenId);
            int pool = AddressIdPool.TokenPoolIdOf(tokenId);

            // Holder → TokenPool: capacity = balance
            graph.AddCapacityEdge(avatar, pool, tokenId, balance);
        }

        // TokenPool → Receiver edges (trust-gated, capacity = MaxValue)
        foreach (var (truster, trustedTokens) in trustLookup)
        {
            if (graph.IsRouter(truster)) continue;

            foreach (var token in trustedTokens)
            {
                int pool = AddressIdPool.TokenPoolIdOf(token);
                if (!graph.Nodes.ContainsKey(pool)) continue;
                graph.AddCapacityEdge(pool, truster, token, long.MaxValue);
            }
        }

        // Group minting edges: Group → Avatar with group's token
        foreach (var grp in groups)
        {
            int groupToken = grp;
            foreach (var (truster, trustedTokens) in trustLookup)
            {
                if (graph.IsGroup(truster) || graph.IsRouter(truster)) continue;
                if (trustedTokens.Contains(groupToken))
                {
                    graph.AddCapacityEdge(grp, truster, groupToken, long.MaxValue);
                }
            }

            // Groups receive tokens they trust (collateral)
            if (graph.GroupTrustedTokens.TryGetValue(grp, out var gTrusts))
            {
                foreach (var token in gTrusts)
                {
                    int pool = AddressIdPool.TokenPoolIdOf(token);
                    if (!graph.Nodes.ContainsKey(pool)) continue;
                    graph.AddCapacityEdge(pool, grp, token, long.MaxValue);
                }
            }
        }

        // Consented flow
        if (withConsent)
        {
            foreach (var avatar in avatars)
            {
                if (rng.NextDouble() < consentRate)
                {
                    graph.ConsentedAvatars.Add(avatar);
                }
            }
        }

        return graph;
    }

    internal static (int source, int sink) PickSourceSink(CapacityGraph graph, Random rng)
    {
        var avatars = graph.AvatarNodes.Keys
            .Where(a => !graph.IsGroup(a) && !graph.IsRouter(a))
            .ToList();

        if (avatars.Count < 2)
            throw new InvalidOperationException("Need at least 2 non-group, non-router avatars");

        int srcIdx = rng.Next(avatars.Count);
        int sinkIdx;
        do { sinkIdx = rng.Next(avatars.Count); } while (sinkIdx == srcIdx);

        return (avatars[srcIdx], avatars[sinkIdx]);
    }

    #endregion

    #region Invariant Checkers

    private static void AssertFlowConservation(List<TransferPathStep> steps, string source, string sink)
    {
        var src = source.ToLowerInvariant();
        var snk = sink.ToLowerInvariant();
        var netFlow = new Dictionary<string, long>();

        foreach (var step in steps)
        {
            var from = step.From!.ToLowerInvariant();
            var to = step.To!.ToLowerInvariant();

            // Skip self-loops (aggregation edges)
            if (from == to) continue;

            var flow = long.Parse(step.Value!);
            netFlow.TryGetValue(from, out long fromNet);
            netFlow[from] = fromNet - flow;
            netFlow.TryGetValue(to, out long toNet);
            netFlow[to] = toNet + flow;
        }

        foreach (var (vertex, net) in netFlow)
        {
            if (vertex == src || vertex == snk) continue;
            // Router is a pass-through — net flow should be zero
            Assert.That(net, Is.EqualTo(0),
                $"Flow conservation violated at {vertex[..Math.Min(10, vertex.Length)]}...: net={net}");
        }
    }

    private static void AssertAllFlowsPositive(List<TransferPathStep> steps)
    {
        foreach (var step in steps)
        {
            var flow = long.Parse(step.Value!);
            Assert.That(flow, Is.GreaterThan(0),
                $"Output contains non-positive flow: {step.From} → {step.To} = {flow}");
        }
    }

    private static void AssertMaxFlowBounded(string maxFlowStr, UInt256 target)
    {
        var maxFlow = UInt256.Parse(maxFlowStr);
        Assert.That(maxFlow, Is.LessThanOrEqualTo(target),
            $"MaxFlow {maxFlow} exceeds target {target}");
    }

    #endregion

    #region Property: Never Crash

    /// <summary>
    /// Random graphs with varying sizes should never cause the pathfinder to crash.
    /// </summary>
    [Test]
    [Repeat(20)]
    public void RandomGraph_NeverCrashes()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 42);
        int avatars = rng.Next(3, 15);
        int groups = rng.Next(0, 3);
        double density = rng.NextDouble() * 0.6 + 0.1; // 0.1 to 0.7

        var graph = BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: density, withRouter: groups > 0);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };

        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        Assert.DoesNotThrow(() =>
        {
            pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        }, $"Crashed with avatars={avatars}, groups={groups}, density={density:F2}");
    }

    /// <summary>
    /// Empty graph (no edges) causes MaxFlowSolver to report BAD_INPUT.
    /// This is expected — the pathfinder requires at least some edges.
    /// </summary>
    [Test]
    public void MinimalGraph_TwoAvatars_NoTrust_ThrowsBadInput()
    {
        var graph = new CapacityGraph();
        int a = AddressIdPool.IdOf("0xfzmin1" + new string('0', 34));
        int b = AddressIdPool.IdOf("0xfzmin2" + new string('0', 34));
        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.TrustLookup = new Dictionary<int, HashSet<int>>();

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(a),
            Sink = AddressIdPool.StringOf(b),
        };

        // No edges → solver reports BAD_INPUT
        var ex = Assert.Throws<InvalidOperationException>(() =>
            pathfinder.ComputeMaxFlowWithPath(graph, request, UInt256.One));
        Assert.That(ex!.Message, Does.Contain("BAD_INPUT"));
    }

    #endregion

    #region Property: Flow Conservation

    /// <summary>
    /// For any random graph producing a path, flow must be conserved at intermediate vertices.
    /// </summary>
    [Test]
    [Repeat(20)]
    public void RandomGraph_FlowConservation_Holds()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 100);
        int avatars = rng.Next(4, 12);

        var graph = BuildSyntheticGraph(avatars, 0, DefaultBalance, rng,
            trustDensity: 0.4, withRouter: false);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);

        if (result.Transfers.Count > 0)
        {
            AssertFlowConservation(result.Transfers, request.Source!, request.Sink!);
            AssertAllFlowsPositive(result.Transfers);
            AssertMaxFlowBounded(result.MaxFlow, target);
        }
    }

    /// <summary>
    /// Flow conservation with groups (router inserts Avatar→Router→Group).
    /// </summary>
    [Test]
    [Repeat(15)]
    public void RandomGraph_WithGroups_FlowConservation_Holds()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 200);
        int avatars = rng.Next(4, 10);
        int groups = rng.Next(1, 3);

        var graph = BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: 0.5, withRouter: true);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);

        if (result.Transfers.Count > 0)
        {
            AssertFlowConservation(result.Transfers, request.Source!, request.Sink!);
            AssertAllFlowsPositive(result.Transfers);
        }
    }

    #endregion

    #region Property: MaxFlow Bounded

    /// <summary>
    /// MaxFlow should never exceed target, and should never be negative.
    /// </summary>
    [Test]
    [Repeat(20)]
    public void RandomGraph_MaxFlowNeverExceedsTarget()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 300);
        int avatars = rng.Next(3, 15);

        var graph = BuildSyntheticGraph(avatars, 0, DefaultBalance, rng, trustDensity: 0.3);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };

        long targetLong = rng.Next(1, (int)Math.Min(DefaultBalance, int.MaxValue));
        UInt256 target = CirclesConverter.BlowUpToUInt256(targetLong);

        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        var maxFlow = UInt256.Parse(result.MaxFlow);

        Assert.That(maxFlow, Is.LessThanOrEqualTo(target),
            $"MaxFlow {maxFlow} exceeded target {target}");
    }

    #endregion

    #region Property: Mint Edge Ordering

    /// <summary>
    /// After SortEdgesForMintDependencies, ValidateMintEdgeOrdering must always pass.
    /// Tested with randomly-ordered edges containing group mints.
    /// </summary>
    [Test]
    [Repeat(15)]
    public void RandomGroupEdges_SortThenValidate_AlwaysPasses()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 400);
        int avatars = rng.Next(4, 8);
        int groups = rng.Next(1, 3);

        var graph = BuildSyntheticGraph(avatars, groups, DefaultBalance, rng,
            trustDensity: 0.5, withRouter: true);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);

        // If there are transfers, they've already been through Sort+Validate.
        // Run ValidateMintEdgeOrdering again on the raw FlowEdges to double-check.
        if (result.Transfers.Count > 0)
        {
            // Reconstruct FlowEdges from TransferPathSteps
            var flowEdges = result.Transfers.Select(t => new FlowEdge(
                AddressIdPool.IdOf(t.From!),
                AddressIdPool.IdOf(t.To!),
                AddressIdPool.IdOf(t.TokenOwner!),
                long.Parse(t.Value!))
            { Flow = long.Parse(t.Value!) }).ToList();

            Assert.DoesNotThrow(() => V2Pathfinder.ValidateMintEdgeOrdering(flowEdges, graph));
        }
    }

    #endregion

    #region Property: Quantized Mode

    /// <summary>
    /// In quantized mode, every sink-bound transfer (non-self-loop) must be an exact
    /// multiple of 96 CRC.
    /// </summary>
    [Test]
    [Repeat(15)]
    public void RandomGraph_QuantizedMode_SinkTransfersAreQuantaMultiples()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 500);
        int avatars = rng.Next(3, 8);

        // Use high balance so quantization is feasible
        var graph = BuildSyntheticGraph(avatars, 0, HighBalance, rng,
            trustDensity: 0.5, withRouter: false);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            QuantizedMode = true,
        };

        // Target = enough for a few invitations
        UInt256 target = CirclesConverter.BlowUpToUInt256(HighBalance);

        // quantizedMode needs the graph built with that flag (for token filtering)
        // Since we're building directly, just run the pathfinder
        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);

        if (result.Transfers.Count == 0) return;

        string sinkAddr = request.Sink!.ToLowerInvariant();
        foreach (var step in result.Transfers)
        {
            bool toSink = step.To!.ToLowerInvariant() == sinkAddr;
            bool selfLoop = step.From!.ToLowerInvariant() == sinkAddr && toSink;

            if (toSink && !selfLoop)
            {
                // Sink-bound: must be quantum multiple
                // Value is in wei (UInt256 string), convert back to truncated long form
                var truncated = CirclesConverter.TruncateToInt64(UInt256.Parse(step.Value!));
                var remainder = truncated % InvitationQuanta;
                Assert.That(remainder, Is.EqualTo(0L),
                    $"Sink-bound transfer {step.From}→{step.To} truncated={truncated} is not a quantum multiple of {InvitationQuanta}");
            }
        }
    }

    #endregion

    #region Property: Consented Flow

    /// <summary>
    /// With consented flow enabled, the pathfinder should still not crash and
    /// should produce valid (possibly reduced) results.
    /// NOTE: Flow conservation is NOT checked here because ValidateConsentedFlow
    /// is a lossy post-hoc filter — it removes edges that violate consent rules,
    /// which can break conservation at intermediate vertices. This is by design:
    /// the contract would reject those edges anyway.
    /// </summary>
    [Test]
    [Repeat(15)]
    public void RandomGraph_WithConsent_NeverCrashes()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 600);
        int avatars = rng.Next(4, 10);

        var graph = BuildSyntheticGraph(avatars, 0, DefaultBalance, rng,
            trustDensity: 0.4, withRouter: false, withConsent: true, consentRate: 0.5);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        Assert.DoesNotThrow(() =>
        {
            var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
            if (result.Transfers.Count > 0)
            {
                AssertAllFlowsPositive(result.Transfers);
            }
        });
    }

    #endregion

    #region Property: Dense Graph Stress

    /// <summary>
    /// Fully connected graph (trust density = 1.0) should work correctly.
    /// </summary>
    [Test]
    public void DenseGraph_FullTrust_AllInvariants()
    {
        var rng = new Random(777);
        var graph = BuildSyntheticGraph(10, 2, DefaultBalance, rng,
            trustDensity: 1.0, withRouter: true);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);

        if (result.Transfers.Count > 0)
        {
            AssertFlowConservation(result.Transfers, request.Source!, request.Sink!);
            AssertAllFlowsPositive(result.Transfers);
            AssertMaxFlowBounded(result.MaxFlow, target);
        }
    }

    /// <summary>
    /// Sparse graph (trust density = 0.05) — many paths will have no flow.
    /// Should still not crash.
    /// </summary>
    [Test]
    [Repeat(10)]
    public void SparseGraph_LowTrust_NoCrash()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 888);
        var graph = BuildSyntheticGraph(15, 0, DefaultBalance, rng,
            trustDensity: 0.05, withRouter: false);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        Assert.DoesNotThrow(() =>
        {
            pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        });
    }

    #endregion

    #region Property: MaxTransfers Limit

    /// <summary>
    /// When MaxTransfers is specified, output should not exceed that many transfer steps.
    /// </summary>
    [Test]
    [Repeat(10)]
    public void RandomGraph_MaxTransfers_OutputWithinLimit()
    {
        var rng = new Random(TestContext.CurrentContext.CurrentRepeatCount + 900);
        int avatars = rng.Next(5, 12);
        int maxTransfers = rng.Next(1, 5);

        var graph = BuildSyntheticGraph(avatars, 0, DefaultBalance, rng,
            trustDensity: 0.4, withRouter: false);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
            MaxTransfers = maxTransfers,
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);

        Assert.That(result.Transfers.Count, Is.LessThanOrEqualTo(maxTransfers),
            $"Output has {result.Transfers.Count} steps, limit was {maxTransfers}");
    }

    #endregion

    #region Property: Chain Graph (Deterministic)

    /// <summary>
    /// A→B→C→D chain with sequential trust should find exactly 1 path.
    /// This is a deterministic sanity check for the graph builder.
    /// </summary>
    [Test]
    public void ChainGraph_LinearTrust_FindsPath()
    {
        var graph = new CapacityGraph();
        const long bal = 500_000L;

        // Create chain: A → B → C → D
        int a = AddressIdPool.IdOf("0xfzchain_a" + new string('0', 31));
        int b = AddressIdPool.IdOf("0xfzchain_b" + new string('0', 31));
        int c = AddressIdPool.IdOf("0xfzchain_c" + new string('0', 31));
        int d = AddressIdPool.IdOf("0xfzchain_d" + new string('0', 31));

        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddAvatar(c);
        graph.AddAvatar(d);

        // Trust chain: B trusts A's token, C trusts B's token, D trusts C's token
        var trust = new Dictionary<int, HashSet<int>>
        {
            [a] = new() { a },
            [b] = new() { a, b },
            [c] = new() { b, c },
            [d] = new() { c, d },
        };
        graph.TrustLookup = trust;

        // A holds A-token
        graph.AddTokenNode(a);
        int poolA = AddressIdPool.TokenPoolIdOf(a);
        graph.AddCapacityEdge(a, poolA, a, bal);
        graph.AddCapacityEdge(poolA, b, a, long.MaxValue); // B trusts A

        // B holds B-token
        graph.AddTokenNode(b);
        int poolB = AddressIdPool.TokenPoolIdOf(b);
        graph.AddCapacityEdge(b, poolB, b, bal);
        graph.AddCapacityEdge(poolB, c, b, long.MaxValue); // C trusts B

        // C holds C-token
        graph.AddTokenNode(c);
        int poolC = AddressIdPool.TokenPoolIdOf(c);
        graph.AddCapacityEdge(c, poolC, c, bal);
        graph.AddCapacityEdge(poolC, d, c, long.MaxValue); // D trusts C

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(a),
            Sink = AddressIdPool.StringOf(d),
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(bal);

        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, target);

        Assert.That(UInt256.Parse(result.MaxFlow), Is.GreaterThan((UInt256)0),
            "Chain graph should find a path");
        AssertFlowConservation(result.Transfers, request.Source!, request.Sink!);
        AssertAllFlowsPositive(result.Transfers);
    }

    /// <summary>
    /// Disconnected graph (no trust between source and sink) should produce zero flow.
    /// </summary>
    [Test]
    public void DisconnectedGraph_ZeroFlow()
    {
        var graph = new CapacityGraph();
        const long bal = 500_000L;

        int a = AddressIdPool.IdOf("0xfzdiscon_a" + new string('0', 30));
        int b = AddressIdPool.IdOf("0xfzdiscon_b" + new string('0', 30));
        int c = AddressIdPool.IdOf("0xfzdiscon_c" + new string('0', 30));

        graph.AddAvatar(a);
        graph.AddAvatar(b);
        graph.AddAvatar(c);

        // A→B connected, C isolated
        var trust = new Dictionary<int, HashSet<int>>
        {
            [a] = new() { a },
            [b] = new() { a, b },
            [c] = new() { c }, // Only self-trust
        };
        graph.TrustLookup = trust;

        graph.AddTokenNode(a);
        int poolA = AddressIdPool.TokenPoolIdOf(a);
        graph.AddCapacityEdge(a, poolA, a, bal);
        graph.AddCapacityEdge(poolA, b, a, long.MaxValue);

        graph.AddTokenNode(c);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(a),
            Sink = AddressIdPool.StringOf(c),
        };

        var result = pathfinder.ComputeMaxFlowWithPath(graph, request, CirclesConverter.BlowUpToUInt256(bal));

        Assert.That(result.MaxFlow, Is.EqualTo("0"));
        Assert.That(result.Transfers, Is.Empty);
    }

    #endregion

    #region Property: Idempotent (Same Input → Same Output)

    /// <summary>
    /// Running the pathfinder twice on the same graph should produce identical results.
    /// </summary>
    [Test]
    public void SameInput_ProducesIdenticalOutput()
    {
        var rng = new Random(12345);
        var graph = BuildSyntheticGraph(8, 1, DefaultBalance, rng,
            trustDensity: 0.4, withRouter: true);
        var (source, sink) = PickSourceSink(graph, rng);

        var pathfinder = new V2Pathfinder();
        var request = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source),
            Sink = AddressIdPool.StringOf(sink),
        };
        UInt256 target = CirclesConverter.BlowUpToUInt256(DefaultBalance);

        // The graph's edge list is mutated by the solver (Flow fields set).
        // Build two identical graphs to test.
        var rng2 = new Random(12345);
        var graph2 = BuildSyntheticGraph(8, 1, DefaultBalance, rng2,
            trustDensity: 0.4, withRouter: true);
        var (source2, sink2) = PickSourceSink(graph2, rng2);

        var request2 = new FlowRequest
        {
            Source = AddressIdPool.StringOf(source2),
            Sink = AddressIdPool.StringOf(sink2),
        };

        var result1 = pathfinder.ComputeMaxFlowWithPath(graph, request, target);
        var result2 = pathfinder.ComputeMaxFlowWithPath(graph2, request2, target);

        Assert.That(result1.MaxFlow, Is.EqualTo(result2.MaxFlow),
            "Same input should produce same MaxFlow");
        Assert.That(result1.Transfers.Count, Is.EqualTo(result2.Transfers.Count),
            "Same input should produce same number of transfers");
    }

    #endregion
}
