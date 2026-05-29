using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Circles.Rpc.Host.OpenRpc;

namespace Circles.Rpc.Host.Tests;

[TestFixture]
public class OpenRpcGeneratorTests
{
    [Test]
    public void Generate_FlowRequestSchema_IsPresent()
    {
        var doc = OpenRpcGenerator.Generate();

        Assert.That(doc.Components, Is.Not.Null);
        Assert.That(doc.Components!.Schemas, Is.Not.Null);
        Assert.That(doc.Components.Schemas.ContainsKey("FlowRequest"), Is.True,
            "FlowRequest schema must be present in components.schemas");

        var schema = doc.Components.Schemas["FlowRequest"];
        Assert.That(schema.Type, Is.EqualTo("object"));
        Assert.That(schema.Properties, Is.Not.Null);

        var expectedProps = new[]
        {
            "source", "sink", "targetFlow", "toTokens", "fromTokens",
            "excludedFromTokens", "excludedToTokens", "withWrap",
            "simulatedBalances", "simulatedTrusts", "simulatedConsentedAvatars",
            "maxTransfers", "quantizedMode", "debugShowIntermediateSteps"
        };

        foreach (var prop in expectedProps)
            Assert.That(schema.Properties!.ContainsKey(prop), Is.True, $"FlowRequest must have '{prop}' property");
    }

    [Test]
    public void Generate_FlowRequestSchema_HasPropertyDescriptions()
    {
        var doc = OpenRpcGenerator.Generate();

        var schema = doc.Components!.Schemas["FlowRequest"];
        Assert.That(schema.Properties!["source"].Description, Is.Not.Null.And.Not.Empty);
        Assert.That(schema.Properties!["sink"].Description, Is.Not.Null.And.Not.Empty);
        Assert.That(schema.Properties!["targetFlow"].Description, Is.Not.Null.And.Not.Empty);
        Assert.That(schema.Properties!["quantizedMode"].Description, Is.Not.Null.And.Not.Empty);
        Assert.That(schema.Properties!["maxTransfers"].Description, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Generate_SimulatedBalanceAndTrust_SchemasPresent()
    {
        var doc = OpenRpcGenerator.Generate();

        Assert.That(doc.Components!.Schemas.ContainsKey("SimulatedBalance"), Is.True);
        Assert.That(doc.Components.Schemas.ContainsKey("SimulatedTrust"), Is.True);

        var simBal = doc.Components.Schemas["SimulatedBalance"];
        Assert.That(simBal.Properties, Is.Not.Null);
        Assert.That(simBal.Properties!.ContainsKey("holder"), Is.True);
        Assert.That(simBal.Properties!.ContainsKey("token"), Is.True);
        Assert.That(simBal.Properties!.ContainsKey("amount"), Is.True);

        // Descriptions present
        Assert.That(simBal.Properties["holder"].Description, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Generate_FindPathResult_HasMaxFlowResponseSchema()
    {
        var doc = OpenRpcGenerator.Generate();

        var findPathMethod = doc.Methods.Find(m => m.Name == "circlesV2_findPath");
        Assert.That(findPathMethod, Is.Not.Null);
        Assert.That(findPathMethod!.Result, Is.Not.Null);
        Assert.That(findPathMethod.Result!.Schema?.Ref, Is.EqualTo("#/components/schemas/MaxFlowResponse"));

        Assert.That(doc.Components!.Schemas.ContainsKey("MaxFlowResponse"), Is.True);

        var mfr = doc.Components.Schemas["MaxFlowResponse"];
        Assert.That(mfr.Properties, Is.Not.Null);
        Assert.That(mfr.Properties!.ContainsKey("maxFlow"), Is.True);
        Assert.That(mfr.Properties!.ContainsKey("transfers"), Is.True);
    }

    [Test]
    public void Generate_TransferPathStep_SchemaPresent()
    {
        var doc = OpenRpcGenerator.Generate();

        Assert.That(doc.Components!.Schemas.ContainsKey("TransferPathStep"), Is.True);

        var step = doc.Components.Schemas["TransferPathStep"];
        Assert.That(step.Properties, Is.Not.Null);
        Assert.That(step.Properties!.ContainsKey("from"), Is.True);
        Assert.That(step.Properties!.ContainsKey("to"), Is.True);
        Assert.That(step.Properties!.ContainsKey("tokenOwner"), Is.True);
        Assert.That(step.Properties!.ContainsKey("value"), Is.True);
    }

    [Test]
    public void Generate_IsIdempotent_CallingTwiceProducesSameResult()
    {
        // Verify that the SchemaCache.Clear() bug is truly fixed by calling Generate() twice
        var doc1 = OpenRpcGenerator.Generate();
        var doc2 = OpenRpcGenerator.Generate();

        Assert.That(doc1.Components!.Schemas.ContainsKey("FlowRequest"), Is.True);
        Assert.That(doc2.Components!.Schemas.ContainsKey("FlowRequest"), Is.True);
        Assert.That(doc1.Components.Schemas.Count, Is.EqualTo(doc2.Components!.Schemas.Count));
    }

    [Test]
    public void Generate_AllMethods_HaveAtLeast1ParamWithDescription()
    {
        var doc = OpenRpcGenerator.Generate();

        foreach (var method in doc.Methods)
        {
            // Methods with no parameters (health, tables, networkSnapshot) are fine
            if (method.Params.Count == 0) continue;

            // Every param on every method should have a description (manual overrides or reflected)
            foreach (var p in method.Params)
            {
                Assert.That(p.Description, Is.Not.Null.And.Not.Empty,
                    $"Method {method.Name}, param {p.Name} is missing a description");
            }
        }
    }

    [Test]
    public void Generate_AllMethods_HaveExamples()
    {
        var doc = OpenRpcGenerator.Generate();

        foreach (var method in doc.Methods)
        {
            Assert.That(method.Examples, Is.Not.Null.And.Not.Empty,
                $"Method {method.Name} is missing examples");
        }
    }

    // SSOT for the live RPC surface is the dispatch switch in Dispatch/RpcDispatcher.cs
    // (split out from Program.cs). The OpenRPC spec is hand-derived from MethodMappings in
    // OpenRpcGenerator. This test parses the dispatcher source at test time and asserts every
    // dispatched circles_*/circlesV2_* arm has a MethodMappings entry — adding a handler
    // without updating MethodMappings fails CI here instead of silently shipping a stale
    // /openrpc.json.
    [Test]
    public void Generate_AllDispatchedMethods_ArePresentInOpenRpcSpec()
    {
        var dispatchSourcePath = ResolveDispatchSourcePath();
        var source = File.ReadAllText(dispatchSourcePath);

        var dispatchArm = new Regex(@"""(circles_[A-Za-z_]+|circlesV2_[A-Za-z_]+)""\s*=>");
        var dispatched = dispatchArm.Matches(source)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.That(dispatched, Is.Not.Empty,
            $"No dispatch arms matched in {dispatchSourcePath} — regex stale or file moved");

        var documented = OpenRpcGenerator.Generate().Methods
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = dispatched.Except(documented).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.That(missing, Is.Empty,
            "Methods dispatched in RpcDispatcher.cs but missing from OpenRpcGenerator.MethodMappings:\n  - " +
            string.Join("\n  - ", missing) +
            "\n\nAdd a MethodMappings entry for each dispatched method to keep /openrpc.json fresh.");
    }

    private static string ResolveDispatchSourcePath([CallerFilePath] string? thisFile = null)
    {
        var dir = Path.GetDirectoryName(thisFile!)!;
        // Search candidates in priority order: new (Dispatch/RpcDispatcher.cs) → legacy (Program.cs).
        // The split refactor moved the dispatch switch out of Program.cs into RpcDispatcher.cs;
        // the fallback keeps the test working on branches that predate the split.
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(dir, "..", "Circles.Rpc.Host", "Dispatch", "RpcDispatcher.cs")),
            Path.GetFullPath(Path.Combine(dir, "..", "Circles.Rpc.Host", "Program.cs")),
        };
        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        Assert.Ignore(
            $"OpenRPC drift guard requires source-tree access. None of [{string.Join(", ", candidates)}] exist " +
            "— skipping (test was likely built on a different host than it is run on).");
        return candidates[0]; // unreachable; satisfies non-null return
    }
}
