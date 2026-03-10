namespace Circles.Pathfinder.Tests;

/// <summary>
/// Regression tests for pathfinder host settings defaults.
/// Guards the minimum concurrency floor identified during load testing.
/// </summary>
[TestFixture, Parallelizable]
public class HostSettingsRegressionTests
{
    [SetUp]
    public void ClearEnvVars()
    {
        Environment.SetEnvironmentVariable("MAX_CONCURRENT_REQUESTS", null);
        Environment.SetEnvironmentVariable("NETHERMIND_RPC_URL", "http://localhost:8545");
        // Common.Settings requires a Postgres connection string
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING", "Host=localhost;Database=test;Username=test;Password=test");
    }

    [Test]
    public void MaxConcurrentRequests_HasMinimumFloorOf8()
    {
        var settings = new Host.Settings();
        Assert.That(settings.MaxConcurrentRequests, Is.GreaterThanOrEqualTo(8),
            "MaxConcurrentRequests must be at least 8 even on low-core machines. " +
            "On a 2-core VM, ProcessorCount*2=4 was too low — caused 503 rejections.");
    }

    [Test]
    public void MaxConcurrentRequests_ScalesWithProcessorCount()
    {
        var settings = new Host.Settings();
        var expected = Math.Max(Environment.ProcessorCount * 2, 8);
        Assert.That(settings.MaxConcurrentRequests, Is.EqualTo(expected),
            "Default should be Math.Max(ProcessorCount * 2, 8)");
    }

    [Test]
    public void MaxConcurrentRequests_CanBeOverriddenViaEnvVar()
    {
        Environment.SetEnvironmentVariable("MAX_CONCURRENT_REQUESTS", "32");
        try
        {
            var settings = new Host.Settings();
            Assert.That(settings.MaxConcurrentRequests, Is.EqualTo(32));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAX_CONCURRENT_REQUESTS", null);
        }
    }
}
