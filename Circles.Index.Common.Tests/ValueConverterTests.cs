using System.Numerics;
using Circles.Index.ContractClient;

namespace Circles.Index.Common.Tests
{
    using MathConv = Circles.Index.Common.CirclesConverter;
    using ChainConv = Circles.Index.ContractClient.CirclesConverter;

    [TestFixture]
    public sealed class CirclesConverterTests
    {
        private static readonly BigInteger ONE_CRC = BigInteger.Pow(10, 18); // 1 Circle (atto)
        private static readonly BigInteger MAX_LOSS = BigInteger.One << 64; // ≤ 2^64 trunc error

        private CirclesV2HubClient? _hub; // null → no chain tests

        // ───────────────────────── one-time fixture ─────────────────────────────

        [OneTimeSetUp]
        public void Setup()
        {
            var rpc = "http://rpc.aboutcircles.com";
            var addr = "0xc12C1E50ABB450d6205Ea2C3Fa861b3B834d13e8";

            if (!string.IsNullOrWhiteSpace(rpc) && !string.IsNullOrWhiteSpace(addr))
                _hub = new CirclesV2HubClient(rpc, addr);
        }

        // ───────────────────────── epoch / day helper ───────────────────────────

        [Test]
        public void DayFromTimestamp_EpochIsZero()
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(1_602_720_000);
            var day = MathConv.DayFromTimestamp(ts, 1_602_720_000);

            Assert.That(day, Is.EqualTo(0UL));
        }

        // ───────────────────────── round-trip checks (math lib only) ────────────

        [TestCase(0UL)]
        [TestCase(1UL)]
        [TestCase(365UL)]
        [TestCase(1_234UL)]
        [TestCase(5_000UL)]
        public void Inflationary_Demurrage_RoundTrip_MathLossWithin2Pow64(ulong day)
        {
            var orig = BigInteger.Parse("12345678901234567890123456");
            var dem = MathConv.InflationaryToDemurrage(orig, day);
            var back = MathConv.DemurrageToInflationary(dem, day);

            Assert.That(orig - back, Is.LessThan(MAX_LOSS));
        }

        // ────────────────────────── math vs EVM comparisons ─────────────────────

        [TestCase(1UL)]
        [TestCase(365UL)]
        [TestCase(1000UL)]
        [TestCase(2500UL)]
        public void Inflationary_To_Demurrage_Math_Equals_Evm(ulong day)
        {
            var evm = ChainConv.InflationaryToDemurrage(ONE_CRC, day, _hub!);
            var math = MathConv.InflationaryToDemurrage(ONE_CRC, day);

            Assert.That(math, Is.EqualTo(evm),
                $"math diverged from EVM on day {day}");
        }

        [TestCase(10)]
        [TestCase(1234)]
        [TestCase(50000)]
        public void Random_Amounts_Math_Equals_Evm(int seed)
        {
            var rnd = new Random(seed);
            for (int i = 0; i < 20; i++)
            {
                ulong day = (ulong)rnd.Next(0, 6000);
                BigInteger amt = BigInteger.Abs(new BigInteger(rnd.NextInt64())) * ONE_CRC;

                var evm = ChainConv.InflationaryToDemurrage(amt, day, _hub!);
                var math = MathConv.InflationaryToDemurrage(amt, day);

                Assert.That(math, Is.EqualTo(evm),
                    $"mismatch at iter {i}, day {day}");
            }
        }

        // ───────────────── edge guards & other existing tests (unchanged) ───────

        [Test]
        public void Zero_Amount_Stays_Zero()
        {
            Assert.That(MathConv.InflationaryToDemurrage(BigInteger.Zero, 999),
                Is.EqualTo(BigInteger.Zero));
            Assert.That(MathConv.DemurrageToInflationary(BigInteger.Zero, 123),
                Is.EqualTo(BigInteger.Zero));
        }
    }
}