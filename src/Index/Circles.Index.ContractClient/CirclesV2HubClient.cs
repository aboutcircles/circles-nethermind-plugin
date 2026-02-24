using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Web3;

namespace Circles.Index.ContractClient
{
    // ─────────────────────────── contract wrapper ──────────────────────────────
    public sealed class CirclesV2HubClient
    {
        private readonly Web3 _web3;
        private readonly string _hub;

        public CirclesV2HubClient(string web3RpcUrl, string hubAddress)
        {
            _web3 = new Web3(web3RpcUrl);
            _hub = hubAddress;
        }

        // pure functions – no CallAsync<> gas estimation required
        public BigInteger ConvertInflationaryToDemurrage(BigInteger infl, ulong day) =>
            _web3.Eth.GetContractHandler(_hub)
                .QueryAsync<ConvertInfl2DemArgs, BigInteger>(
                    new ConvertInfl2DemArgs { Value = infl, Day = day })
                .ConfigureAwait(false).GetAwaiter().GetResult();

        public BigInteger ConvertDemurrageToInflation(BigInteger dem, ulong lastUpdDay) =>
            _web3.Eth.GetContractHandler(_hub)
                .QueryAsync<ConvertDem2InflArgs, BigInteger>(
                    new ConvertDem2InflArgs { Value = dem, Day = lastUpdDay })
                .ConfigureAwait(false).GetAwaiter().GetResult();

        public ulong DayFromTimestamp(ulong ts) =>
            _web3.Eth.GetContractHandler(_hub)
                .QueryAsync<DayArgs, ulong>(new DayArgs { Timestamp = ts })
                .ConfigureAwait(false).GetAwaiter().GetResult();

        // ──────────────── small DTOs for Nethereum -----------------------------
        [Function("convertInflationaryToDemurrageValue", "uint256")]
        private sealed class ConvertInfl2DemArgs : FunctionMessage
        {
            [Parameter("uint256", "_inflationaryValue", 1)]
            public BigInteger Value { get; set; }

            [Parameter("uint64", " _day", 2)] public ulong Day { get; set; }
        }

        [Function("convertDemurrageToInflationaryValue", "uint256")]
        private sealed class ConvertDem2InflArgs : FunctionMessage
        {
            [Parameter("uint256", "_demurrageValue", 1)]
            public BigInteger Value { get; set; }

            [Parameter("uint64", "_dayUpdated", 2)]
            public ulong Day { get; set; }
        }

        [Function("day", "uint64")]
        private sealed class DayArgs : FunctionMessage
        {
            [Parameter("uint256", "_timestamp", 1)]
            public ulong Timestamp { get; set; }
        }
    }

    // ─────────────────────────── public converter ──────────────────────────────
    public static class CirclesConverter
    {
        // NOTE: same signatures as before + a required IHub client

        public static BigInteger AttoCrcToAttoCircles(BigInteger attoCrc, CirclesV2HubClient v2Hub)
        {
            ulong today = v2Hub.DayFromTimestamp(GetNow());
            return v2Hub.ConvertInflationaryToDemurrage(attoCrc, today);
        }

        public static BigInteger AttoCirclesToAttoCrc(BigInteger attoCircles, CirclesV2HubClient v2Hub)
        {
            ulong today = v2Hub.DayFromTimestamp(GetNow());
            return v2Hub.ConvertDemurrageToInflation(attoCircles, today);
        }

        public static BigInteger AttoCirclesToAttoStaticCircles(BigInteger attoCircles,
            CirclesV2HubClient v2Hub)
        {
            ulong today = v2Hub.DayFromTimestamp(GetNow());
            return v2Hub.ConvertDemurrageToInflation(attoCircles, today);
        }

        public static BigInteger AttoStaticCirclesToAttoCircles(BigInteger attoStatic,
            CirclesV2HubClient v2Hub)
        {
            ulong today = v2Hub.DayFromTimestamp(GetNow());
            return v2Hub.ConvertInflationaryToDemurrage(attoStatic, today);
        }

        // explicit-day helpers (mirror old API)
        public static BigInteger InflationaryToDemurrage(BigInteger inflationary,
            ulong day,
            CirclesV2HubClient v2Hub) =>
            v2Hub.ConvertInflationaryToDemurrage(inflationary, day);

        public static BigInteger DemurrageToInflationary(BigInteger demurraged,
            ulong day,
            CirclesV2HubClient v2Hub) =>
            v2Hub.ConvertDemurrageToInflation(demurraged, day);

        // ──────────────────────────── misc utils ───────────────────────────────
        private static ulong GetNow() =>
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
