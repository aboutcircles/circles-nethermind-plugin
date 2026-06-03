using Circles.Rpc.Host;
using Npgsql;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Unit tests for <see cref="TransferDataQuery.BuildWhereClause"/> — the filter/cursor branching
/// behind circles_getTransferData. These run without a database; the endpoint's DB execution and
/// result mapping remain covered by the (Nethermind-gated) integration tests.
/// </summary>
[TestFixture]
public class TransferDataQueryTests
{
    private const string Addr = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Counter = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string? Param(List<NpgsqlParameter> ps, string name) =>
        ps.FirstOrDefault(p => p.ParameterName == name)?.Value?.ToString();

    [Test]
    public void Sent_NoCounterparty_FiltersFromOnly()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, "sent", null, null, null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(where, Is.EqualTo(@"""from"" = @addr"));
            Assert.That(Param(ps, "addr"), Is.EqualTo(Addr));
            Assert.That(ps.Any(p => p.ParameterName == "counterparty"), Is.False);
        });
    }

    [Test]
    public void Sent_WithCounterparty_FiltersFromAndTo()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, "sent", Counter, null, null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(where, Is.EqualTo(@"""from"" = @addr AND ""to"" = @counterparty"));
            Assert.That(Param(ps, "addr"), Is.EqualTo(Addr));
            Assert.That(Param(ps, "counterparty"), Is.EqualTo(Counter));
        });
    }

    [Test]
    public void Received_NoCounterparty_FiltersToOnly()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, "received", null, null, null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(where, Is.EqualTo(@"""to"" = @addr"));
            Assert.That(Param(ps, "addr"), Is.EqualTo(Addr));
        });
    }

    [Test]
    public void Received_WithCounterparty_FiltersToAndFrom()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, "received", Counter, null, null, null, null, null);

        Assert.That(where, Is.EqualTo(@"""to"" = @addr AND ""from"" = @counterparty"));
        Assert.That(Param(ps, "counterparty"), Is.EqualTo(Counter));
    }

    [Test]
    public void BothDirections_NoCounterparty_OrClauseIsParenthesized()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, null, null, null, null, null, null, null);

        Assert.Multiple(() =>
        {
            // The OR clause stays grouped so later AND filters can't leak into a branch.
            // (The condition is pre-parenthesized then wrapped again → intentional double-parens.)
            Assert.That(where, Is.EqualTo(@"((""from"" = @addr OR ""to"" = @addr))"));
            Assert.That(where, Is.Not.Empty);
            Assert.That(Param(ps, "addr"), Is.EqualTo(Addr));
        });
    }

    [Test]
    public void BothDirections_WithCounterparty_BuildsParenthesizedBidirectionalOr()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, null, Counter, null, null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(where, Is.EqualTo(
                @"((""from"" = @addr AND ""to"" = @counterparty) OR (""from"" = @counterparty AND ""to"" = @addr))"));
            Assert.That(Param(ps, "addr"), Is.EqualTo(Addr));
            Assert.That(Param(ps, "counterparty"), Is.EqualTo(Counter));
        });
    }

    [Test]
    public void OrClause_StaysGrouped_WhenCombinedWithBlockFilter()
    {
        // Regression guard: the bidirectional OR must remain parenthesized when AND-joined with a
        // block-range filter, otherwise the block filter would only constrain one OR branch.
        var (where, _) = TransferDataQuery.BuildWhereClause(
            Addr, null, null, fromBlock: 100, null, null, null, null);

        Assert.That(where, Is.EqualTo(
            @"((""from"" = @addr OR ""to"" = @addr)) AND ""blockNumber"" >= @fromBlock"));
    }

    [Test]
    public void BothDirectionsWithCounterparty_StaysGrouped_WhenCombinedWithBlockFilter()
    {
        // The highest-value case: the bidirectional (A AND B) OR (C AND D) clause must stay wrapped
        // when AND-joined with a block filter, or the filter would bind to only one OR branch.
        var (where, _) = TransferDataQuery.BuildWhereClause(
            Addr, null, Counter, fromBlock: 100, null, null, null, null);

        Assert.That(where, Is.EqualTo(
            @"((""from"" = @addr AND ""to"" = @counterparty) OR (""from"" = @counterparty AND ""to"" = @addr)) AND ""blockNumber"" >= @fromBlock"));
    }

    [Test]
    public void BlockRange_AddsBothBounds()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, "sent", null, fromBlock: 100, toBlock: 200, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(where, Does.Contain(@"""blockNumber"" >= @fromBlock"));
            Assert.That(where, Does.Contain(@"""blockNumber"" <= @toBlock"));
            Assert.That(Param(ps, "fromBlock"), Is.EqualTo("100"));
            Assert.That(Param(ps, "toBlock"), Is.EqualTo("200"));
        });
    }

    [Test]
    public void Cursor_AddsKeysetTupleComparison()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, "sent", null, null, null,
            cursorBlock: 500, cursorTxIndex: 3, cursorLogIndex: 7);

        Assert.Multiple(() =>
        {
            Assert.That(where, Does.Contain(
                @"(""blockNumber"", ""transactionIndex"", ""logIndex"") < (@cursorBlock, @cursorTxIndex, @cursorLogIndex)"));
            Assert.That(Param(ps, "cursorBlock"), Is.EqualTo("500"));
            Assert.That(Param(ps, "cursorTxIndex"), Is.EqualTo("3"));
            Assert.That(Param(ps, "cursorLogIndex"), Is.EqualTo("7"));
        });
    }

    [Test]
    public void Counterparty_IsLowercased_InEveryDirectionBranch()
    {
        const string upper = "0xBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var (_, sent) = TransferDataQuery.BuildWhereClause(Addr, "sent", upper, null, null, null, null, null);
        var (_, received) = TransferDataQuery.BuildWhereClause(Addr, "received", upper, null, null, null, null, null);
        var (_, both) = TransferDataQuery.BuildWhereClause(Addr, null, upper, null, null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(Param(sent, "counterparty"), Is.EqualTo(Counter));
            Assert.That(Param(received, "counterparty"), Is.EqualTo(Counter));
            Assert.That(Param(both, "counterparty"), Is.EqualTo(Counter));
        });
    }

    [Test]
    public void AllFilters_Combined_AndJoinsEveryCondition()
    {
        var (where, ps) = TransferDataQuery.BuildWhereClause(
            Addr, "received", Counter, fromBlock: 10, toBlock: 20,
            cursorBlock: 30, cursorTxIndex: 1, cursorLogIndex: 2);

        Assert.Multiple(() =>
        {
            Assert.That(where, Does.Contain(@"""to"" = @addr"));
            Assert.That(where, Does.Contain(@"""from"" = @counterparty"));
            Assert.That(where, Does.Contain(@"""blockNumber"" >= @fromBlock"));
            Assert.That(where, Does.Contain(@"""blockNumber"" <= @toBlock"));
            Assert.That(where, Does.Contain("(\"blockNumber\", \"transactionIndex\", \"logIndex\") <"));
            // 4 AND separators for 5 conditions
            Assert.That(where.Split(" AND ").Length, Is.EqualTo(5));
            Assert.That(ps, Has.Count.EqualTo(7));
        });
    }
}
