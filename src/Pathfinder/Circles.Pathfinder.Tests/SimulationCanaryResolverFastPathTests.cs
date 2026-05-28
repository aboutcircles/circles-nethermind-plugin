using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Circles.Common;
using Circles.Common.Dto;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.Canary;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Wiring tests for the canary's inflation resolver: prove that the all-DemurrageCircles
/// short-circuit is consulted, not just declared.
/// <para>A predicate-only test would still pass if a future change deleted the early-return —
/// the resolver would harmlessly dial both RPCs and produce the same output. These tests
/// inject a counting HttpMessageHandler so the contract under test is "no HTTP at all"
/// rather than "the output happens to be equal".</para>
/// </summary>
[TestFixture]
public class SimulationCanaryResolverFastPathTests
{
    private string? _prevRpcUrl;
    private string? _prevPgConn;

    [SetUp]
    public void SetUp()
    {
        // DO NOT add [Parallelizable] to this fixture: SetUp/TearDown mutate process-wide
        // environment variables (NETHERMIND_RPC_URL, POSTGRES_CONNECTION_STRING). Peer
        // fixtures such as HostSettingsRegressionTests, HttpEndpointTests, and
        // NetworkStateUpdaterServiceTests also read these. NUnit assembly default is
        // sequential per-fixture, which keeps this safe today — opting in to fixture
        // parallelism here would silently corrupt those neighbors.
        _prevRpcUrl = Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL");
        _prevPgConn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        // SimulationCanaryService caches NethermindRpcUrl in its ctor; the actual network
        // gate is the injected IHttpClientFactory, so the URL value here is irrelevant
        // as long as it satisfies the non-null contract. The base Common.Settings ctor
        // separately requires POSTGRES_CONNECTION_STRING (not used by the fast path).
        Environment.SetEnvironmentVariable(
            "NETHERMIND_RPC_URL", "http://localhost:0/canary-test-never-hit");
        Environment.SetEnvironmentVariable(
            "POSTGRES_CONNECTION_STRING", "Host=localhost;Port=0;Database=canary-test;Username=u;Password=p");
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("NETHERMIND_RPC_URL", _prevRpcUrl);
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", _prevPgConn);
    }

    [Test]
    public async Task ResolveInflationaryAmounts_AllDemurraged_MakesZeroRpcCalls()
    {
        var handler = new CountingHandler();
        var factory = new SingleHandlerHttpClientFactory(handler);
        var service = NewService(factory);
        var item = NewWorkItem();
        var calls = new[]
        {
            new SimulationCanaryService.DemurragedUnwrapCall(
                "0xfromA", "0xwrapA", BigInteger.Parse("100"), CirclesType.DemurrageCircles),
            new SimulationCanaryService.DemurragedUnwrapCall(
                "0xfromB", "0xwrapB", BigInteger.Parse("200"), CirclesType.DemurrageCircles),
            new SimulationCanaryService.DemurragedUnwrapCall(
                "0xfromC", "0xwrapC", BigInteger.Parse("300"), CirclesType.DemurrageCircles),
        };

        using var client = factory.CreateClient("canary-simulation");
        var resolved = await service.ResolveInflationaryAmountsAsync(
            item, calls, "0x3e8", client, CancellationToken.None);

        Assert.That(handler.Count, Is.EqualTo(0),
            "all-DemurrageCircles batch must short-circuit before any HTTP — both " +
            "eth_getBlockByNumber AND eth_simulateV1 are wasted RPCs in this path");
        Assert.That(resolved.Count, Is.EqualTo(3));
        // Per-row From/Wrapper assertions in addition to Amount: catches a future
        // factory mutation that swaps fields (e.g. `new(call.Wrapper, call.From, ...)`).
        Assert.That(resolved[0].From,    Is.EqualTo("0xfromA"));
        Assert.That(resolved[0].Wrapper, Is.EqualTo("0xwrapA"));
        Assert.That(resolved[0].Amount,  Is.EqualTo(BigInteger.Parse("100")));
        Assert.That(resolved[1].From,    Is.EqualTo("0xfromB"));
        Assert.That(resolved[1].Wrapper, Is.EqualTo("0xwrapB"));
        Assert.That(resolved[1].Amount,  Is.EqualTo(BigInteger.Parse("200")));
        Assert.That(resolved[2].From,    Is.EqualTo("0xfromC"));
        Assert.That(resolved[2].Wrapper, Is.EqualTo("0xwrapC"));
        Assert.That(resolved[2].Amount,  Is.EqualTo(BigInteger.Parse("300")));
    }

    [Test]
    public async Task ResolveInflationaryAmounts_EmptyBatch_MakesZeroRpcCalls()
    {
        // Defensive case: zero-length unwrap list (e.g. wrapperToAvatar populated but no
        // transfer actually referenced a wrapper) must short-circuit cleanly. The predicate
        // must not throw and must not dial out.
        var handler = new CountingHandler();
        var factory = new SingleHandlerHttpClientFactory(handler);
        var service = NewService(factory);
        var item = NewWorkItem();
        var calls = Array.Empty<SimulationCanaryService.DemurragedUnwrapCall>();

        using var client = factory.CreateClient("canary-simulation");
        var resolved = await service.ResolveInflationaryAmountsAsync(
            item, calls, "0x3e8", client, CancellationToken.None);

        Assert.That(handler.Count, Is.EqualTo(0));
        Assert.That(resolved.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task ResolveInflationaryAmounts_OneInflationaryEntry_TriggersTwoRpcs()
    {
        // Positive control: when a single InflationaryCircles entry needs γ^day conversion
        // the predicate must NOT short-circuit. Expect exactly two RPCs:
        //   1. eth_getBlockByNumber (resolve simulation block timestamp → day)
        //   2. eth_simulateV1 (one convertDemurrageToInflationaryValue per inflationary entry)
        // The demurraged neighbor must pass through unchanged.
        var handler = new ScriptedHandler();
        var factory = new SingleHandlerHttpClientFactory(handler);
        var service = NewService(factory);
        var item = NewWorkItem();
        var calls = new[]
        {
            new SimulationCanaryService.DemurragedUnwrapCall(
                "0xfromD", "0xwrapD", BigInteger.Parse("100"), CirclesType.DemurrageCircles),
            new SimulationCanaryService.DemurragedUnwrapCall(
                "0xfromI", "0xwrapI", BigInteger.Parse("200"), CirclesType.InflationaryCircles),
        };

        using var client = factory.CreateClient("canary-simulation");
        var resolved = await service.ResolveInflationaryAmountsAsync(
            item, calls, "0x3e8", client, CancellationToken.None);

        Assert.That(handler.Count, Is.EqualTo(2),
            "positive control: 1× eth_getBlockByNumber + 1× eth_simulateV1 batch");
        Assert.That(resolved.Count, Is.EqualTo(2));
        Assert.That(resolved[0].From,    Is.EqualTo("0xfromD"));
        Assert.That(resolved[0].Wrapper, Is.EqualTo("0xwrapD"));
        Assert.That(resolved[0].Amount,  Is.EqualTo(BigInteger.Parse("100")),
            "DemurrageCircles entry passes through unchanged");
        Assert.That(resolved[1].From,    Is.EqualTo("0xfromI"));
        Assert.That(resolved[1].Wrapper, Is.EqualTo("0xwrapI"));
        Assert.That(resolved[1].Amount,  Is.EqualTo(BigInteger.Parse("300")),
            "InflationaryCircles entry substituted with scripted convertDemurrageToInflationaryValue result");
    }

    [Test]
    public async Task ResolveInflationaryAmounts_MixedBatch_WalksInfIdxAcrossDemurragedGaps()
    {
        // ApplyInflationaryAmounts walks an `infIdx` cursor that ONLY advances for
        // InflationaryCircles positions. An off-by-one (e.g. advancing on every position)
        // still passes OneInflationaryEntry because there's only one inflationary slot.
        // This test forces the resolver to walk past a demurraged entry between two
        // inflationary ones, with two distinct scripted return amounts so a misaligned
        // cursor produces a visibly wrong assignment.
        var handler = new MixedBatchScriptedHandler();
        var factory = new SingleHandlerHttpClientFactory(handler);
        var service = NewService(factory);
        var item = NewWorkItem();
        var calls = new[]
        {
            new SimulationCanaryService.DemurragedUnwrapCall(
                "0xfromI1", "0xwrapI1", BigInteger.Parse("200"), CirclesType.InflationaryCircles),
            new SimulationCanaryService.DemurragedUnwrapCall(
                "0xfromD",  "0xwrapD",  BigInteger.Parse("500"), CirclesType.DemurrageCircles),
            new SimulationCanaryService.DemurragedUnwrapCall(
                "0xfromI2", "0xwrapI2", BigInteger.Parse("400"), CirclesType.InflationaryCircles),
        };

        using var client = factory.CreateClient("canary-simulation");
        var resolved = await service.ResolveInflationaryAmountsAsync(
            item, calls, "0x3e8", client, CancellationToken.None);

        Assert.That(handler.Count, Is.EqualTo(2),
            "two RPCs: eth_getBlockByNumber + eth_simulateV1 with batch of 2 convert calls");
        Assert.That(resolved.Count, Is.EqualTo(3));
        // Slot 0 (inflationary) gets the first scripted return = 300
        Assert.That(resolved[0].Wrapper, Is.EqualTo("0xwrapI1"));
        Assert.That(resolved[0].Amount,  Is.EqualTo(BigInteger.Parse("300")));
        // Slot 1 (demurraged) passes through with original 500 — proves the resolver
        // does NOT consume an inflationary-batch result for this position.
        Assert.That(resolved[1].Wrapper, Is.EqualTo("0xwrapD"));
        Assert.That(resolved[1].Amount,  Is.EqualTo(BigInteger.Parse("500")));
        // Slot 2 (inflationary) gets the second scripted return = 700; an off-by-one
        // infIdx would either reuse 300 here or assign 700 to slot 0 / 500 to slot 2.
        Assert.That(resolved[2].Wrapper, Is.EqualTo("0xwrapI2"));
        Assert.That(resolved[2].Amount,  Is.EqualTo(BigInteger.Parse("700")));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static SimulationCanaryService NewService(IHttpClientFactory factory) =>
        // Fully qualified: Circles.Pathfinder.Settings (base) is also in scope via the
        // Pathfinder reference, and the derived Host.Settings is the one SimulationCanaryService accepts.
        new(new Circles.Pathfinder.Host.Settings(), NullLogger<SimulationCanaryService>.Instance, factory);

    private static CanaryWorkItem NewWorkItem() =>
        new("test-req", "0xsource", "0xsink", 1000, new List<TransferPathStep>());

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            // Return a syntactically valid JSON-RPC envelope so the resolver's downstream
            // parsing doesn't blow up on an unexpected execution path (test still asserts
            // Count == 0 first, but we want the failure mode to be a clean assertion miss,
            // not a parser exception masking the actual regression).
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}")
            });
        }
    }

    /// <summary>
    /// Scripts the two RPCs the resolver issues when an InflationaryCircles entry is present:
    /// (1) eth_getBlockByNumber → timestamp 0x6 (pre-epoch → day=0, exercises the
    /// <c>ComputeInflationDay</c> floor branch); (2) eth_simulateV1 → one call returning
    /// 0x12c (300) for the lone convertDemurrageToInflationaryValue.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        // 300 (decimal) = 0x12c, left-padded to 64 hex chars for the returnData word
        private static readonly string SimulateV1Body =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[{\"calls\":[{\"status\":\"0x1\",\"returnData\":\"0x" +
            new string('0', 61) + "12c\"}]}]}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref _count);
            string body = n == 1
                ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"timestamp\":\"0x6\"}}"
                : SimulateV1Body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    /// <summary>
    /// Variant of <see cref="ScriptedHandler"/> that returns TWO distinct values in the
    /// eth_simulateV1 batch (300, 700) — so an off-by-one <c>infIdx</c> in
    /// <c>ApplyInflationaryAmounts</c> produces a visibly wrong assignment to slot 0 vs slot 2.
    /// </summary>
    private sealed class MixedBatchScriptedHandler : HttpMessageHandler
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        // 300 (decimal) = 0x12c, 700 = 0x2bc — both padded to 64 hex chars
        private static readonly string SimulateV1Body =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[{\"calls\":[" +
            "{\"status\":\"0x1\",\"returnData\":\"0x" + new string('0', 61) + "12c\"}," +
            "{\"status\":\"0x1\",\"returnData\":\"0x" + new string('0', 61) + "2bc\"}" +
            "]}]}";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            int n = Interlocked.Increment(ref _count);
            string body = n == 1
                ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"timestamp\":\"0x6\"}}"
                : SimulateV1Body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class SingleHandlerHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleHandlerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/canary-test") };
    }
}
