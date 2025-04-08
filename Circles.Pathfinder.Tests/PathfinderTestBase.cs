using Circles.Pathfinder.Tests.Fixtures;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Base class for all pathfinder tests that provides common functionality.
/// </summary>
public abstract class PathfinderTestBase
{
    protected PathfinderTestFixture Fixture { get; private set; }

    [SetUp]
    public virtual void Setup()
    {
        Fixture = new PathfinderTestFixture();
    }
    
    [TearDown]
    public virtual void TearDown()
    {
        // Any common cleanup code can go here
    }
    
    /// <summary>
    /// Configure test fixture with a custom network
    /// </summary>
    protected void ConfigureTestNetwork(int nodeCount, long baseAmount = 100000000, bool linear = false)
    {
        Fixture.ReconfigureWithTestNetwork(nodeCount, baseAmount, linear);
    }
}