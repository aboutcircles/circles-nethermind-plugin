using System.Numerics;
using System.Text;
using Circles.Index.ContractClient;

namespace Circles.Common.Tests;

using MathConv = Circles.Common.CirclesConverter;
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
        var rpc = "https://rpc.aboutcircles.com";
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

    [Test]
    public void ConvertStaticToDemurragedAndBack()
    {
        var values = new BigInteger[]
        {
            BigInteger.Parse("278918778808685268301"),
            BigInteger.Parse("199761716785870953714"),
            BigInteger.Parse("12345678901234567890123456"),
            BigInteger.Parse("1000"),
            BigInteger.Parse("1000000000000000000"),
            BigInteger.Parse("9999999999343265832415324322"),
            BigInteger.Parse("199761716785870953714")
        };

        foreach (var staticBalance in values)
        {
            // real static balance
            // var staticBalance = BigInteger.Parse("278918778808685268301");

            // Convert static to demurraged (pathfinder only works with demurraged balances)
            var day = MathConv.DayFromTimestamp(DateTimeOffset.UtcNow, 1_602_720_000);
            var evmDemurraged = ChainConv.InflationaryToDemurrage(staticBalance, day, _hub!);
            var mathDemurraged = MathConv.InflationaryToDemurrage(staticBalance, day);

            // Establish that the math and EVM results are equal
            Assert.That(mathDemurraged, Is.EqualTo(evmDemurraged), $"mismatch between contract and math");

            // Then convert back to static circles (if we want to use the balance, we need to unwrap first and need this amount)
            var evmStatic = ChainConv.DemurrageToInflationary(evmDemurraged, day, _hub!);
            var mathStatic = MathConv.DemurrageToInflationary(evmDemurraged, day);

            // Assert that the math and EVM results are equal
            Assert.That(mathStatic, Is.EqualTo(evmStatic), $"mismatch between contract and math");

            // Check if the value is the same as in the beginning
            // Assert.That(mathStatic, Is.EqualTo(staticBalance), $"mismatch after converting back");
            Console.WriteLine($"Expected: {staticBalance}, Actual: {mathStatic}, Diff: {staticBalance - mathStatic}");
        }
    }

    [Test]
    public void ConvertStaticToDemurragedAndBackAndToDemurrageAgain()
    {
        var values = new BigInteger[]
        {
            BigInteger.Parse("278918778808685268301"),
            BigInteger.Parse("199761716785870953714"),
            BigInteger.Parse("12345678901234567890123456"),
            BigInteger.Parse("1000"),
            BigInteger.Parse("1000000000000000000"),
            BigInteger.Parse("9999999999343265832415324322"),
            BigInteger.Parse("199761716785870953714")
        };

        StringBuilder sb = new StringBuilder();

        foreach (var staticBalance in values)
        {
            // real static balance
            // var staticBalance = BigInteger.Parse("278918778808685268301");

            sb.AppendLine($"Static balance:                                 {staticBalance}");

            // Convert static to demurraged (pathfinder only works with demurraged balances)
            var day = MathConv.DayFromTimestamp(DateTimeOffset.UtcNow, 1_602_720_000) - 1;
            var evmDemurraged = ChainConv.InflationaryToDemurrage(staticBalance, day, _hub!);
            var mathDemurraged = MathConv.InflationaryToDemurrage(staticBalance, day);

            sb.AppendLine($"* Static -> Demurraged:                         {evmDemurraged}");

            // Establish that the math and EVM results are equal
            Assert.That(mathDemurraged, Is.EqualTo(evmDemurraged), $"mismatch between contract and math");

            // Then convert back to static circles (if we want to use the balance, we need to unwrap first and need this amount)
            var evmStatic = ChainConv.DemurrageToInflationary(evmDemurraged, day, _hub!);
            var mathStatic = MathConv.DemurrageToInflationary(evmDemurraged, day);

            sb.AppendLine($"* Static -> Demurraged -> Static:               {evmStatic}");

            // Assert that the math and EVM results are equal
            Assert.That(mathStatic, Is.EqualTo(evmStatic), $"mismatch between contract and math");

            var evmDemurraged2 = MathConv.InflationaryToDemurrage(evmStatic, day);

            sb.AppendLine($"* Static -> Demurraged -> Static -> Demurraged: {evmDemurraged2}");

            Console.WriteLine(sb);
            sb.Clear();
        }
    }


    [Test]
    public void ConvertDemurragedToStaticAndBack()
    {
        var values = new BigInteger[]
        {
            BigInteger.Parse("278918778808685268301"),
            BigInteger.Parse("199761716785870953714"),
            BigInteger.Parse("12345678901234567890123456"),
            BigInteger.Parse("1000"),
            BigInteger.Parse("1000000000000000000"),
            BigInteger.Parse("9999999999343265832415324322"),
            BigInteger.Parse("199761716785870953714")
        };

        foreach (var demurragedBalance in values)
        {
            // real static balance
            // var demurragedBalance = BigInteger.Parse("199761716785870953714");

            // Convert static to demurraged (pathfinder only works with demurraged balances)
            var day = MathConv.DayFromTimestamp(DateTimeOffset.UtcNow, 1_602_720_000);
            var evmStatic = ChainConv.DemurrageToInflationary(demurragedBalance, day, _hub!);
            var mathStatic = MathConv.DemurrageToInflationary(demurragedBalance, day);

            // Establish that the math and EVM results are equal
            Assert.That(mathStatic, Is.EqualTo(evmStatic), $"mismatch between contract and math");

            // Then convert back to static circles (if we want to use the balance, we need to unwrap first and need this amount)
            var evmDemurraged = ChainConv.InflationaryToDemurrage(evmStatic, day, _hub!);
            var mathDemurraged = MathConv.InflationaryToDemurrage(evmStatic, day);

            // Assert that the math and EVM results are equal
            Assert.That(evmDemurraged, Is.EqualTo(mathDemurraged), $"mismatch between contract and math");

            // Check if the value is the same as in the beginning
            // Assert.That(evmDemurraged, Is.EqualTo(demurragedBalance), $"mismatch after converting back");
            Console.WriteLine(
                $"Expected: {demurragedBalance}, Actual: {mathDemurraged}, Diff: {demurragedBalance - mathDemurraged}");
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