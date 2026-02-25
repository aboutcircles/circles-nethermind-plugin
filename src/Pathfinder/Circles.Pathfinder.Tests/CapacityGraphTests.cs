using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Nodes;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Unit tests for CapacityGraph: avatar/group/router/token-node management,
/// edge creation, and special-type queries (IsGroup, IsRouter).
/// </summary>
[TestFixture, Parallelizable]
public class CapacityGraphTests
{
    private const string Prefix = "0xcg";

    private static int Id(string suffix) => AddressIdPool.IdOf($"{Prefix}{suffix}");

    #region AddAvatar

    [Test]
    public void AddAvatar_CreatesNodeAndAvatarEntries()
    {
        var g = new CapacityGraph();
        int a = Id("10000000000000000000000000000000000000c1");

        g.AddAvatar(a);

        Assert.That(g.AvatarNodes.ContainsKey(a), Is.True);
        Assert.That(g.Nodes.ContainsKey(a), Is.True);
        Assert.That(g.Nodes[a], Is.InstanceOf<AvatarNode>());
    }

    [Test]
    public void AddAvatar_Duplicate_IsIdempotent()
    {
        var g = new CapacityGraph();
        int a = Id("20000000000000000000000000000000000000c2");

        g.AddAvatar(a);
        g.AddAvatar(a);

        Assert.That(g.AvatarNodes.Count, Is.EqualTo(1));
        Assert.That(g.Nodes.Count, Is.EqualTo(1));
    }

    #endregion

    #region AddGroup

    [Test]
    public void AddGroup_AddsAsAvatarAndGroupNode()
    {
        var g = new CapacityGraph();
        int grp = Id("30000000000000000000000000000000000000c3");

        g.AddGroup(grp);

        Assert.That(g.AvatarNodes.ContainsKey(grp), Is.True);
        Assert.That(g.GroupNodes.Contains(grp), Is.True);
        Assert.That(g.IsGroup(grp), Is.True);
    }

    [Test]
    public void IsGroup_RegularAvatar_ReturnsFalse()
    {
        var g = new CapacityGraph();
        int a = Id("40000000000000000000000000000000000000c4");

        g.AddAvatar(a);

        Assert.That(g.IsGroup(a), Is.False);
    }

    #endregion

    #region SetRouter

    [Test]
    public void SetRouter_SetsRouterNodeAndAddsAvatar()
    {
        var g = new CapacityGraph();
        int r = Id("50000000000000000000000000000000000000c5");

        g.SetRouter(r);

        Assert.That(g.RouterNode, Is.EqualTo(r));
        Assert.That(g.AvatarNodes.ContainsKey(r), Is.True);
        Assert.That(g.IsRouter(r), Is.True);
    }

    [Test]
    public void IsRouter_NonRouterNode_ReturnsFalse()
    {
        var g = new CapacityGraph();
        int a = Id("60000000000000000000000000000000000000c6");
        int r = Id("61000000000000000000000000000000000000c6");

        g.SetRouter(r);
        g.AddAvatar(a);

        Assert.That(g.IsRouter(a), Is.False);
    }

    [Test]
    public void IsRouter_NoRouterSet_ReturnsFalse()
    {
        var g = new CapacityGraph();
        int a = Id("70000000000000000000000000000000000000c7");
        g.AddAvatar(a);

        Assert.That(g.IsRouter(a), Is.False);
    }

    #endregion

    #region AddTokenNode

    [Test]
    public void AddTokenNode_CreatesTokenNodeInBothDictionaries()
    {
        var g = new CapacityGraph();
        int tok = Id("80000000000000000000000000000000000000c8");

        g.AddTokenNode(tok);

        Assert.That(g.TokenNodes.Count, Is.EqualTo(1));
        var tn = g.TokenNodes.Values.First();
        Assert.That(tn.TokenId, Is.EqualTo(tok));
        Assert.That(g.Nodes.ContainsKey(tn.Address), Is.True);
    }

    [Test]
    public void AddTokenNode_Duplicate_IsIdempotent()
    {
        var g = new CapacityGraph();
        int tok = Id("90000000000000000000000000000000000000c9");

        g.AddTokenNode(tok);
        g.AddTokenNode(tok);

        Assert.That(g.TokenNodes.Count, Is.EqualTo(1));
    }

    #endregion

    #region AddCapacityEdge

    [Test]
    public void AddCapacityEdge_AddsEdgeWithCorrectProperties()
    {
        var g = new CapacityGraph();
        int a = Id("a0000000000000000000000000000000000000d0");
        int b = Id("b0000000000000000000000000000000000000d1");
        int tok = Id("c0000000000000000000000000000000000000d2");

        g.AddCapacityEdge(a, b, tok, 500);

        Assert.That(g.Edges.Count, Is.EqualTo(1));
        Assert.That(g.Edges[0].From, Is.EqualTo(a));
        Assert.That(g.Edges[0].To, Is.EqualTo(b));
        Assert.That(g.Edges[0].Token, Is.EqualTo(tok));
        Assert.That(g.Edges[0].InitialCapacity, Is.EqualTo(500));
    }

    [Test]
    public void AddCapacityEdge_MultipleEdges_AllStored()
    {
        var g = new CapacityGraph();
        int a = Id("d0000000000000000000000000000000000000d3");
        int b = Id("e0000000000000000000000000000000000000d4");
        int tok1 = Id("f0000000000000000000000000000000000000d5");
        int tok2 = Id("f1000000000000000000000000000000000000d5");

        g.AddCapacityEdge(a, b, tok1, 100);
        g.AddCapacityEdge(a, b, tok2, 200);

        Assert.That(g.Edges.Count, Is.EqualTo(2));
    }

    #endregion

    #region Initial State

    [Test]
    public void NewGraph_HasEmptyCollections()
    {
        var g = new CapacityGraph();

        Assert.That(g.Nodes, Is.Empty);
        Assert.That(g.AvatarNodes, Is.Empty);
        Assert.That(g.TokenNodes, Is.Empty);
        Assert.That(g.Edges, Is.Empty);
        Assert.That(g.GroupNodes, Is.Empty);
        Assert.That(g.GroupTrustedTokens, Is.Empty);
        Assert.That(g.ConsentedAvatars, Is.Empty);
        Assert.That(g.VirtualSinkAddress, Is.Null);
        Assert.That(g.RouterNode, Is.Null);
    }

    #endregion

    #region GroupTrustedTokens and ConsentedAvatars

    [Test]
    public void GroupTrustedTokens_CanBePopulated()
    {
        var g = new CapacityGraph();
        int grp = Id("g0000000000000000000000000000000000000g1");
        int tok = Id("g1000000000000000000000000000000000000g2");

        g.AddGroup(grp);
        g.GroupTrustedTokens[grp] = new HashSet<int> { tok };

        Assert.That(g.GroupTrustedTokens[grp], Does.Contain(tok));
    }

    [Test]
    public void ConsentedAvatars_CanBeReplaced()
    {
        var g = new CapacityGraph();
        int a = Id("h0000000000000000000000000000000000000h1");

        g.ConsentedAvatars = new HashSet<int> { a };

        Assert.That(g.ConsentedAvatars, Does.Contain(a));
    }

    #endregion
}
