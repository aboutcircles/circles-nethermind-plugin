using System.Numerics;

namespace Circles.Common
{
    /// <summary>Implements the linear interpolation used during v1→v2 migration.</summary>
    public static class V1Converter
    {
        public const ulong
            ACCURACY = 100_000_000; // 1e8

        /// <remarks>
        /// All inputs must be the exact values returned by HubV1:
        ///  - factorCurrent  = hubV1.inflate(ACCURACY, currentPeriod)
        ///  - factorNext     = hubV1.inflate(ACCURACY, currentPeriod+1)
        ///  - secondsInto    = now - (inflationDayZero + currentPeriod*period)
        ///  - period         = hubV1.period()
        /// </remarks>
        public static BigInteger V1ToDemurrage(
            BigInteger v1Amount,
            BigInteger factorCurrent,
            BigInteger factorNext,
            uint secondsInto,
            uint periodSec)
        {
            // rP = factorCur*(P-s) + factorNext*s   (see Migration.sol)
            BigInteger rP = factorCurrent * (periodSec - secondsInto) + factorNext * secondsInto;

            // amount * 3 * ACCURACY * P / rP  (same rounding-down)
            return (v1Amount * 3 * ACCURACY * periodSec) / rP;
        }
    }

    // -----------------------------------------------------------------------------
    //   V1Inflation – reproduces HubV1.inflate(ACCURACY, periodIndex) verbatim
    // -----------------------------------------------------------------------------
    public static class V1Inflation
    {
        private const uint INFLATION_PCT = 107; // constructor arg [0]
        private static readonly BigInteger INFLATION_NUM = INFLATION_PCT;
        private static readonly BigInteger INFLATION_DEN = 100; // implicit “percent”
        private const ulong ACC = 100_000_000; // 1e8, same as contract

        /// <summary>
        /// Returns the exact <c>factor = inflate(ACC, periodIdx)</c> the V1 Hub uses.
        /// </summary>
        public static BigInteger Factor(uint periodIdx)
        {
            if (periodIdx == 0) return ACC;

            // ACC * (107/100) ^ periodIdx   -- all integer math via BigInteger
            BigInteger numPow = BigInteger.Pow(INFLATION_NUM, (int)periodIdx);
            BigInteger denPow = BigInteger.Pow(INFLATION_DEN, (int)periodIdx);

            return (ACC * numPow) / denPow; // floor, same as Solidity
        }
    }
}
