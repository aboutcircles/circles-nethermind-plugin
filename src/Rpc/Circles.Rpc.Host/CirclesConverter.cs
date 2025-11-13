using System.Numerics;

namespace Circles.Rpc.Host;

/// <summary>
/// Helper class for converting between different Circles value representations.
/// Note: These conversions are approximations without blockchain state for time-based adjustments.
/// </summary>
public static class CirclesConverter
{
    private const decimal AttoDivisor = 1_000_000_000_000_000_000m; // 10^18

    /// <summary>
    /// Converts atto-circles (10^-18 circles) to circles.
    /// </summary>
    public static decimal AttoCirclesToCircles(BigInteger attoCircles)
    {
        return (decimal)attoCircles / AttoDivisor;
    }

    /// <summary>
    /// Converts circles to atto-circles.
    /// </summary>
    public static BigInteger CirclesToAttoCircles(decimal circles)
    {
        return (BigInteger)(circles * AttoDivisor);
    }

    /// <summary>
    /// NOTE: This is a placeholder that returns the input unchanged.
    /// In the full implementation with blockchain connector, this would:
    /// - Apply time-based inflation for V1 tokens
    /// - Use the current timestamp to calculate inflated value
    /// </summary>
    public static BigInteger AttoCrcToAttoCircles(BigInteger attoCrc, ulong timestamp)
    {
        // Database-only limitation: Cannot calculate time-based inflation
        // Return the value unchanged as a best-effort approximation
        return attoCrc;
    }

    /// <summary>
    /// NOTE: This is a placeholder that returns the input unchanged.
    /// In the full implementation with blockchain connector, this would:
    /// - Apply time-based deflation to get CRC value
    /// - Use the current timestamp to calculate deflated value
    /// </summary>
    public static BigInteger AttoCirclesToAttoCrc(BigInteger attoCircles, ulong timestamp)
    {
        // Database-only limitation: Cannot calculate time-based deflation
        // Return the value unchanged as a best-effort approximation
        return attoCircles;
    }

    /// <summary>
    /// NOTE: This is a placeholder that returns the input unchanged.
    /// In the full implementation, this would convert demurraged circles to static circles.
    /// </summary>
    public static BigInteger AttoCirclesToAttoStaticCircles(BigInteger attoCircles)
    {
        // Database-only limitation: No demurrage calculation
        // Return the value unchanged as a best-effort approximation
        return attoCircles;
    }

    /// <summary>
    /// NOTE: This is a placeholder that returns the input unchanged.
    /// In the full implementation, this would convert static circles to demurraged circles.
    /// </summary>
    public static BigInteger AttoStaticCirclesToAttoCircles(BigInteger staticAttoCircles)
    {
        // Database-only limitation: No demurrage calculation
        // Return the value unchanged as a best-effort approximation
        return staticAttoCircles;
    }
}
