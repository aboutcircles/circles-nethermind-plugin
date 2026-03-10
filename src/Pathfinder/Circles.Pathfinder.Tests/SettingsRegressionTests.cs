namespace Circles.Pathfinder.Tests;

/// <summary>
/// Regression tests for pathfinder settings defaults.
/// These guard against accidental changes to tuned values identified during load testing.
/// </summary>
[TestFixture, Parallelizable]
public class SettingsRegressionTests
{
    [SetUp]
    public void ClearEnvVars()
    {
        // Ensure env vars don't interfere with default checks
        Environment.SetEnvironmentVariable("PATHFINDER_SOLVER_TIMEOUT_SECONDS", null);
        Environment.SetEnvironmentVariable("MAX_CONCURRENT_REQUESTS", null);
    }

    [Test]
    public void SolverTimeoutSeconds_DefaultIs10()
    {
        var settings = new Settings();
        Assert.That(settings.SolverTimeoutSeconds, Is.EqualTo(10),
            "Default solver timeout should be 10s (reduced from 30s). " +
            "A 30s timeout blocks semaphore slots too long for user-facing requests.");
    }

    [Test]
    public void SolverTimeoutSeconds_CanBeOverriddenViaEnvVar()
    {
        Environment.SetEnvironmentVariable("PATHFINDER_SOLVER_TIMEOUT_SECONDS", "5");
        try
        {
            var settings = new Settings();
            Assert.That(settings.SolverTimeoutSeconds, Is.EqualTo(5));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATHFINDER_SOLVER_TIMEOUT_SECONDS", null);
        }
    }
}
