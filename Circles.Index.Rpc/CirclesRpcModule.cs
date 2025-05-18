using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Runtime.CompilerServices;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Circles.Index.Utils;
using Circles.Pathfinder.DTOs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Circles.Index.Rpc;

public class CirclesRpcModule : ICirclesRpcModule
{
    private readonly InterfaceLogger _pluginLogger;
    private readonly Context _indexerContext;

    public CirclesRpcModule(Context indexerContext)
    {
        ILogger baseLogger = indexerContext.NethermindApi.LogManager.GetClassLogger();
        _pluginLogger = new LoggerWithPrefix("Circles.Index.Rpc:", baseLogger);
        _indexerContext = indexerContext;
    }

    // -----------------------------------------------------------------------------
    // 27-dec "ray" helpers  (same idea as Maker-DAO's RAY = 1e27)
    // -----------------------------------------------------------------------------
    private static class RayMath
    {
        // 1 * 10²⁷
        public static readonly BigInteger ONE = BigInteger.Pow(10, 27);

        // γ scaled to 27 decimals: 0.999801332008598957430648170 (rounded)
        public static readonly BigInteger GAMMA_RAY =
            BigInteger.Parse("999801332008598957430648170");

        // (a · b) / 1e27 (floored)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BigInteger Mul(BigInteger a, BigInteger b) => (a * b) / ONE;

        // a^n in ray fixed-point (exponentiation-by-squaring)
        public static BigInteger Pow(BigInteger a, long n)
        {
            BigInteger result = ONE;
            BigInteger basePow = a;
            long exp = n;

            while (exp > 0)
            {
                if ((exp & 1) == 1)
                    result = Mul(result, basePow);

                basePow = Mul(basePow, basePow);
                exp >>= 1;
            }

            return result;
        }
    }


    // Helpers for ABI words ------------------------------------------------------
    static byte[] Word(ulong value)
    {
        var word = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            word[31 - i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return word;
    }

    static byte[] Word(Address address) => address.Bytes.PadLeft(32);
    static byte[] Word(UInt256 value) => value.PaddedBytes(32);

    static byte[] EncodeAddressArray(IReadOnlyList<Address> addresses)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Word((ulong)addresses.Count));
        foreach (var addr in addresses)
        {
            bytes.AddRange(Word(addr));
        }

        return bytes.ToArray();
    }

    static byte[] EncodeUIntArray(IReadOnlyList<UInt256> values)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Word((ulong)values.Count));
        foreach (var v in values)
        {
            bytes.AddRange(Word(v));
        }

        return bytes.ToArray();
    }

    // ---------------------------------------------------------------------------
    // Main helper – one ERC-1155, many (account,tokenId) pairs
    // ---------------------------------------------------------------------------
    private List<UInt256> GetBatchBalances(
        IEthRpcModule rpcModule,
        Address token,
        IReadOnlyList<Address> accounts,
        IReadOnlyList<UInt256> tokenIds)
    {
        if (accounts.Count != tokenIds.Count)
        {
            throw new ArgumentException("accounts and tokenIds length mismatch");
        }

        // 1. build calldata ------------------------------------------------------
        byte[] selector = Keccak
            .Compute("balanceOfBatch(address[],uint256[])")
            .Bytes[..4].ToArray();

        byte[] encAccounts = EncodeAddressArray(accounts);
        byte[] encIds = EncodeUIntArray(tokenIds);

        ulong offsetAccounts = 0x40; // first dynamic section starts right after the two offsets
        ulong offsetIds = offsetAccounts + (ulong)encAccounts.Length;

        var data = new List<byte>(selector);
        data.AddRange(Word(offsetAccounts));
        data.AddRange(Word(offsetIds));
        data.AddRange(encAccounts);
        data.AddRange(encIds);

        // 2. eth_call ------------------------------------------------------------
        var call = new LegacyTransactionForRpc
        {
            To = token,
            Input = data.ToArray()
        };

        var result = rpcModule.eth_call(call);

        if (result.ErrorCode != 0)
        {
            throw new Exception($"Couldn't get batch balances for token {token}");
        }

        // 3. decode uint256[] ----------------------------------------------------
        byte[] raw = Convert.FromHexString(result.Data.StartsWith("0x")
            ? result.Data[2..]
            : result.Data);

        // ── dynamic return value layout ───────────────────────────────────────────
        // 0x00..0x1F -> offset to start of array (always 0x20)
        // 0x20..0x3F -> length (N)
        // 0x40..     -> N × 32-byte uint256 values
        // -------------------------------------------------------------------------
        int arrayOffset = 32; // skip the offset word
        int count = (int)new UInt256(
            raw.AsSpan(arrayOffset, 32).ToArray(),
            true).ToUInt64(CultureInfo.InvariantCulture);

        int firstElement = arrayOffset + 32;
        int neededBytes = firstElement + count * 32;
        if (raw.Length < neededBytes)
            throw new Exception("Return data shorter than advertised length");

        var balances = new List<UInt256>(count);
        for (int i = 0; i < count; i++)
        {
            int start = firstElement + i * 32;
            balances.Add(new UInt256(raw.AsSpan(start, 32).ToArray(), true));
        }

        return balances;
    }

    private UInt256 GetBalance(IEthRpcModule rpcModule, Address token, Address account, UInt256? tokenId = null)
    {
        string functionName = tokenId == null ? "balanceOf(address)" : "balanceOf(address,uint256)";
        byte[] functionSelector = Keccak.Compute(functionName).Bytes.Slice(0, 4).ToArray();
        byte[] addressBytes = account.Bytes.PadLeft(32);
        byte[] data = functionSelector.Concat(addressBytes).ToArray();

        if (tokenId != null)
        {
            byte[] tokenIdBytes = tokenId.Value.PaddedBytes(32);
            data = data.Concat(tokenIdBytes).ToArray();
        }

        var transactionCall = new LegacyTransactionForRpc { To = token, Input = data };
        var result = rpcModule.eth_call(transactionCall);

        if (result.ErrorCode != 0)
        {
            throw new Exception($"Couldn't get the balance of token {token} for account {account}");
        }

        byte[] uint256Bytes = Convert.FromHexString(result.Data.Substring(2));
        var balance = new UInt256(uint256Bytes, true);

        return balance;
    }

    private UInt256 FetchBalance(IEthRpcModule rpcModule, Address token, Address account, bool isErc20,
        Address hubAddress)
    {
        var balance = isErc20
            ? GetBalance(rpcModule, token, account)
            : GetBalance(rpcModule, hubAddress, account, ConversionUtils.AddressToUInt256(token));

        return balance;
    }

    private async Task<List<CirclesTokenBalance>> GetTokenBalancesForAccount(Address address, Address hubAddress)
    {
        var tokens = GetTokenExposureIds(address);

        var erc20Tokens = tokens.Values.Where(o => o.IsErc20).ToArray();
        var erc1155Tokens = tokens.Values.Where(o => o.IsErc1155).ToArray();

        using var rpc = new RentedEthRpcModule(_indexerContext.NethermindApi);
        await rpc.Rent();

        var balances = new Dictionary<Address, UInt256>();

        // erc1155 balances can be fetched as batch
        if (erc1155Tokens.Length > 0)
        {
            var erc1155Balances = GetBatchBalances(
                rpc.RpcModule!,
                hubAddress,
                Enumerable.Repeat(address, erc1155Tokens.Length).ToArray(),
                erc1155Tokens.Select(o => ConversionUtils.AddressToUInt256(o.TokenAddress)).ToArray());

            for (int i = 0; i < erc1155Tokens.Length; i++)
            {
                balances.Add(erc1155Tokens[i].TokenAddress, erc1155Balances[i]);
            }
        }

        var erc20Balances = erc20Tokens.Select(o => FetchBalance(
            rpc.RpcModule!,
            o.TokenAddress,
            address,
            o.IsErc20,
            hubAddress)).ToArray();

        for (int i = 0; i < erc20Balances.Length; i++)
        {
            var tokenInfo = erc20Tokens[i];
            var balance = erc20Balances[i];

            balances.Add(tokenInfo.TokenAddress, balance);
        }

        var tokenBalances = tokens.Values.Select(token =>
            {
                var rawBalance = balances[token.TokenAddress];

                UInt256 attoCircles;
                decimal cirlces;

                UInt256 attoCrc;
                decimal crc;

                UInt256 staticAttoCircles;
                decimal staticCircles;

                if (token.TokenType == "CrcV1_Signup")
                {
                    // OG CRC
                    attoCrc = rawBalance;
                    crc = ConversionUtils.AttoCirclesToCircles(rawBalance);

                    cirlces = ConversionUtils.CrcToCircles(crc);
                    attoCircles = ConversionUtils.CirclesToAttoCircles(cirlces);

                    staticCircles = ConversionUtils.CirclesToStaticCircles(cirlces, DateTime.Now);
                    staticAttoCircles = ConversionUtils.CirclesToAttoCircles(staticCircles);
                }
                else
                {
                    if (token.IsInflationary)
                    {
                        staticAttoCircles = rawBalance;
                        staticCircles = ConversionUtils.AttoCirclesToCircles(rawBalance);

                        cirlces = ConversionUtils.StaticCirclesToCircles(staticCircles);
                        attoCircles = ConversionUtils.CirclesToAttoCircles(cirlces);

                        crc = ConversionUtils.CirclesToCrc(cirlces);
                        attoCrc = ConversionUtils.CirclesToAttoCircles(crc);
                    }
                    else
                    {
                        attoCircles = rawBalance;
                        cirlces = ConversionUtils.AttoCirclesToCircles(rawBalance);

                        crc = ConversionUtils.CirclesToCrc(cirlces);
                        attoCrc = ConversionUtils.CirclesToAttoCircles(crc);

                        staticCircles = ConversionUtils.CirclesToStaticCircles(cirlces, DateTime.Now);
                        staticAttoCircles = ConversionUtils.CirclesToAttoCircles(staticCircles);
                    }
                }

                var tokenAddress = token.TokenAddress;
                var tokenAddressString = tokenAddress.ToString(true, false);
                var tokenId = ConversionUtils.AddressToUInt256(tokenAddress)
                    .ToString(CultureInfo.InvariantCulture);

                return new CirclesTokenBalance(
                    tokenAddressString,
                    tokenId,
                    token.TokenOwner.ToString(true, false),
                    token.TokenType,
                    token.Version,
                    attoCircles.ToString(CultureInfo.InvariantCulture),
                    cirlces,
                    staticAttoCircles.ToString(CultureInfo.InvariantCulture),
                    staticCircles,
                    attoCrc.ToString(CultureInfo.InvariantCulture),
                    crc,
                    token.IsErc20,
                    token.IsErc1155,
                    token.IsWrapped,
                    token.IsInflationary,
                    token.IsGroup
                );
            })
            .Where(o => o.Circles > 0)
            .OrderByDescending(o => o.StaticCircles)
            .ToList();

        return tokenBalances;
    }

    private async Task<ResultWrapper<string>> GetTotalBalance(Address address, int version, bool? asTimeCircles)
    {
        var balances = await GetTokenBalancesForAccount(address, _indexerContext.Settings.CirclesV2HubAddress);
        var relevantBalances = balances.Where(o => o.Version == version);

        if (asTimeCircles == null || asTimeCircles == true)
        {
            var totalBalance = relevantBalances
                .Select(o => o.Circles)
                .Sum();

            return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            var totalBalance = relevantBalances
                .Select(o => o.StaticAttoCircles)
                .Aggregate(UInt256.Zero, (acc, val) => acc + UInt256.Parse(val));

            return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
        }
    }

    public async Task<ResultWrapper<string>> circles_getTotalBalance(Address address, bool? asTimeCircles = true)
        => await GetTotalBalance(address, 1, asTimeCircles);

    public async Task<ResultWrapper<string>> circlesV2_getTotalBalance(Address address, bool? asTimeCircles = true)
        => await GetTotalBalance(address, 2, asTimeCircles);

    public Task<ResultWrapper<CirclesTrustRelations>> circles_getTrustRelations(Address address)
    {
        const string sql = @"
            select ""user"",
                   ""canSendTo"",
                   ""limit""
            from (
                     select ""blockNumber"",
                            ""transactionIndex"",
                            ""logIndex"",
                            ""user"",
                            ""canSendTo"",
                            ""limit"",
                            row_number() over (partition by ""user"", ""canSendTo"" order by ""blockNumber"" desc, ""transactionIndex"" desc, ""logIndex"" desc) as rn
                     from ""CrcV1_Trust""
                 ) t
            where rn = 1
              and (""user"" = @address
               or ""canSendTo"" = @address)
        ";

        var parameters = new[]
        {
            _indexerContext.ReadonlyDatabase.CreateParameter("address", address.ToString(true, false))
        };

        var result = _indexerContext.ReadonlyDatabase.Select(new ParameterizedSql(sql, parameters));
        if (result.Rows == null)
        {
            throw new Exception("Failed to get trust relations");
        }

        var trustRelations = ParseTrustRelations(result.Rows!, address);
        return Task.FromResult(ResultWrapper<CirclesTrustRelations>.Success(trustRelations));
    }

    private CirclesTrustRelations ParseTrustRelations(IEnumerable<object[]> rows, Address address)
    {
        var incomingTrusts = new List<CirclesTrustRelation>();
        var outgoingTrusts = new List<CirclesTrustRelation>();

        foreach (var row in rows)
        {
            var user = new Address(row[0].ToString() ?? throw new Exception("User address is null"));
            var canSendTo = new Address(row[1].ToString() ?? throw new Exception("CanSendTo address is null"));
            var limit = int.Parse(row[2].ToString() ?? throw new Exception("Limit is null"));

            if (user == address)
                outgoingTrusts.Add(new CirclesTrustRelation(canSendTo, limit));
            else
                incomingTrusts.Add(new CirclesTrustRelation(user, limit));
        }

        return new CirclesTrustRelations(address, outgoingTrusts.ToArray(), incomingTrusts.ToArray());
    }

    public async Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address)
    {
        var balances = await GetTokenBalancesForAccount(address,
            _indexerContext.Settings.CirclesV2HubAddress);

        return ResultWrapper<CirclesTokenBalance[]>.Success(balances.ToArray());
    }

    public Task<ResultWrapper<Address[]>> circles_getCommonTrust(
        Address address1,
        Address address2,
        int? version = null)
    {
        var v1Sql = @"
            select trustee
            from ""V_Crc_TrustRelations""
            where truster in (@address1, @address2)
            and trustee not in (@address1, @address2)
            and version = 1
            group by trustee
            having count(truster) > 1
        ";

        var v2Sql = @"
            select trustee
            from ""V_Crc_TrustRelations""
            where truster in (@address1, @address2)
            and trustee not in (@address1, @address2)
            and version = 2
            group by trustee
            having count(truster) > 1
        ";

        var sql = version == 1
            ? v1Sql
            : version == 2
                ? v2Sql
                : $"{v1Sql} union {v2Sql}";

        var result = _indexerContext.ReadonlyDatabase.Select(new ParameterizedSql(sql, [
            _indexerContext.ReadonlyDatabase.CreateParameter("address1", address1.ToString(true, false)),
            _indexerContext.ReadonlyDatabase.CreateParameter("address2", address2.ToString(true, false))
        ]));

        var commonTrust = result.Rows
            .Select(row => new Address(row[0]?.ToString() ?? throw new Exception("Address is null")))
            .ToArray();

        return Task.FromResult(ResultWrapper<Address[]>.Success(commonTrust));
    }

    public ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query)
    {
        Select select = query.ToModel();
        var parameterizedSql = select.ToSql(_indexerContext.ReadonlyDatabase);

        // StringWriter stringWriter = new();
        // stringWriter.WriteLine($"circles_query(SelectDto query):");
        // stringWriter.WriteLine($"  select: {parameterizedSql.Sql}");
        // stringWriter.WriteLine($"  parameters:");
        // foreach (var parameter in parameterizedSql.Parameters)
        // {
        //     stringWriter.WriteLine($"    {parameter.ParameterName}: {parameter.Value}");
        // }
        // _pluginLogger.Info(stringWriter.ToString());

        var result = _indexerContext.ReadonlyDatabase.Select(parameterizedSql);

        return ResultWrapper<DatabaseQueryResult>.Success(result);
    }
    
    public ResultWrapper<IEnumerable<CirclesTokenBalance>>
        circles_getBalanceBreakdown(Address address)
    {
        var hasV1Balance =
            CirclesV1.LogParser.BalancesByAccountAndToken.TryGetValue(address.ToString(true, false),
                out var v1Balances);

        var hasV2Balance =
            CirclesV2.LogParser.BalancesByAccountAndToken.TryGetValue(address.ToString(true, false),
                out var v2Balances);

        if (!hasV1Balance && !hasV2Balance)
        {
            return ResultWrapper<IEnumerable<CirclesTokenBalance>>.Fail(
                "No balances found");
        }

        List<CirclesTokenBalance> result = new();

        foreach (var v1Balance in v1Balances)
        {
            // V1 circles are ERC20 and always inflationary
            var attoCrcBn = v1Balance.Value.Balance;
            var crc = ConversionUtils.AttoCirclesToCircles((UInt256)attoCrcBn);
            var circles = ConversionUtils.CrcToCircles(crc);
            var attoCircles = ConversionUtils.CirclesToAttoCircles(circles).ToString(NumberFormatInfo.InvariantInfo);
            var staticCircles = ConversionUtils.CirclesToStaticCircles(circles, DateTime.Now);
            var staticAttoCircles = ConversionUtils.CirclesToAttoCircles(staticCircles)
                .ToString(NumberFormatInfo.InvariantInfo);

            result.Add(new CirclesTokenBalance(
                v1Balance.Key,
                v1Balance.Key,
                v1Balance.Value.TokenOwner,
                "CrcV1_Signup",
                1,
                attoCircles,
                circles,
                staticAttoCircles,
                staticCircles,
                attoCrcBn.ToString(NumberFormatInfo.InvariantInfo),
                crc,
                true,
                false,
                false,
                true,
                false));
        }

        foreach (var v2TokenBalance in v2Balances)
        {
            var isGroup = CirclesV2.LogParser.Groups.ContainsKey(v2TokenBalance.Key);

            var isInflationary = (v2TokenBalance.Value.ValueRepresentation & TokenValueRepresentation.Inflationary) ==
                                 TokenValueRepresentation.Inflationary;
            var isWrapped = (v2TokenBalance.Value.ValueRepresentation & TokenValueRepresentation.IsWrapped) ==
                            TokenValueRepresentation.IsWrapped;

            if (isInflationary)
            {
                // Static Circles (only exist in ERC20 form as wrapper)
                var staticAttoCirclesBn = v2TokenBalance.Value.Balance;
                var staticAttoCircles = staticAttoCirclesBn.ToString(NumberFormatInfo.InvariantInfo);
                var staticCircles = ConversionUtils.AttoCirclesToCircles((UInt256)staticAttoCirclesBn);
                var circles = ConversionUtils.StaticCirclesToCircles(staticCircles);
                var attoCircles = ConversionUtils.CirclesToAttoCircles(circles)
                    .ToString(NumberFormatInfo.InvariantInfo);
                var crc = ConversionUtils.CirclesToCrc(circles);
                var attoCrc = ConversionUtils.CirclesToAttoCircles(crc).ToString(NumberFormatInfo.InvariantInfo);

                result.Add(new CirclesTokenBalance(
                    v2TokenBalance.Key,
                    v2TokenBalance.Key,
                    v2TokenBalance.Value.TokenOwner,
                    "CrcV2_ERC20WrapperDeployed_Inflationary",
                    2,
                    attoCircles,
                    circles,
                    staticAttoCircles,
                    staticCircles,
                    attoCrc,
                    crc,
                    true,
                    false,
                    true,
                    true,
                    isGroup));
            }
            else
            {
                // Demurraged Circles
                var hasTokenMoved = CirclesV2.LogParser.LastTokenMovement.TryGetValue(
                    (address.ToString(true, false), v2TokenBalance.Key),
                    out var lastTokenMovement);

                if (!hasTokenMoved)
                {
                    // Token never moved?! Should not be possible.
                    throw new InvalidOperationException(
                        $"Account {address} has a token {v2TokenBalance.Key} that was never moved.");
                }

                // We know when the token was last moved -> apply demurrage for the time between now and last move.
                long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long currentDay = nowSec / 86_400; // circles-days

                long lastMoveDay = lastTokenMovement / 86_400;
                long daysPassed = Math.Max(currentDay - lastMoveDay, 0);
                BigInteger factorRay = RayMath.Pow(RayMath.GAMMA_RAY, daysPassed);
                BigInteger attoCircles = (v2TokenBalance.Value.Item1 * factorRay) / RayMath.ONE;

                decimal circles = ConversionUtils.AttoCirclesToCircles((UInt256)attoCircles);
                decimal staticCircles = ConversionUtils.CirclesToStaticCircles(circles,
                    DateTimeOffset.FromUnixTimeSeconds(lastTokenMovement).DateTime);
                string staticAttoCircles = ConversionUtils.CirclesToAttoCircles(staticCircles)
                    .ToString(NumberFormatInfo.InvariantInfo);
                decimal crc = ConversionUtils.CirclesToCrc(circles);
                string attoCrc = ConversionUtils.CirclesToAttoCircles(crc).ToString(NumberFormatInfo.InvariantInfo);

                result.Add(new CirclesTokenBalance(
                    v2TokenBalance.Key,
                    !isWrapped
                        ? ConversionUtils.AddressToUInt256(new Address(v2TokenBalance.Key))
                            .ToString(NumberFormatInfo.InvariantInfo)
                        : v2TokenBalance.Key,
                    v2TokenBalance.Value.TokenOwner,
                    isWrapped
                        ? "CrcV2_ERC20WrapperDeployed_Demurraged"
                        : isGroup
                            ? "CrcV2_RegisterGroup"
                            : "CrcV2_RegisterHuman",
                    2,
                    attoCircles.ToString(NumberFormatInfo.InvariantInfo),
                    circles,
                    staticAttoCircles,
                    staticCircles,
                    attoCrc,
                    crc,
                    isWrapped,
                    !isWrapped,
                    isWrapped,
                    false,
                    isGroup));
            }
        }

        var orderedResult = result
            .Where(o => o.Circles > 0)
            .OrderByDescending(o => o.Circles);

        return ResultWrapper<IEnumerable<CirclesTokenBalance>>.Success(orderedResult);
    }

    public ResultWrapper<string> circles_getProfileCid(Address address)
    {
        var hasV2Profile = CirclesV2.NameRegistry.LogParser.V2AvatarToCidMap.TryGetValue(address, out var v2Profile);
        if (hasV2Profile)
        {
            return ResultWrapper<string>.Success(v2Profile);
        }

        var hasV1Profile = CirclesV1.NameRegistry.LogParser.V1AvatarToCidMap.TryGetValue(address, out var v1Profile);
        if (hasV1Profile)
        {
            return ResultWrapper<string>.Success(v1Profile);
        }

        return ResultWrapper<string>.Fail("No profile found");
    }

    public ResultWrapper<List<string?>> circles_getProfileCidBatch(Address[] addresses)
    {
        if (addresses.Length > 100)
        {
            return ResultWrapper<List<string?>>.Fail("Batch size exceeds 100");
        }

        List<string?> cids = new(addresses.Length);
        for (int i = 0; i < addresses.Length; i++)
        {
            var address = addresses[i];

            var hasV2Profile =
                CirclesV2.NameRegistry.LogParser.V2AvatarToCidMap.TryGetValue(address, out var v2Profile);
            if (hasV2Profile)
            {
                cids.Add(v2Profile);
                continue;
            }

            var hasV1Profile =
                CirclesV1.NameRegistry.LogParser.V1AvatarToCidMap.TryGetValue(address, out var v1Profile);
            if (hasV1Profile)
            {
                cids.Add(v1Profile);
                continue;
            }

            cids.Add(null);
        }

        return ResultWrapper<List<string?>>.Success(cids);
    }

    public ResultWrapper<CirclesEvent[]> circles_events(
        Address? address
        , long? fromBlock
        , long? toBlock = null
        , string[]? eventTypes = null
        , FilterPredicateDto[]? filterPredicates = null
        , bool? sortAscending = false)
    {
        var queryEvents = new QueryEvents(_indexerContext);
        return ResultWrapper<CirclesEvent[]>.Success(
            queryEvents.CirclesEvents(
                address
                , fromBlock
                , toBlock
                , eventTypes
                , filterPredicates
                , sortAscending));
    }

    private static readonly HttpClient HttpClient = new();
    private static long _totalTimeSpentProxying = 0L;
    private static long _totalProxyCount = 0L;

    public async Task<ResultWrapper<MaxFlowResponse>> circlesV2_findPath(FlowRequest flowRequest)
    {
        var sw = Stopwatch.StartNew();

        // Construct the final URL: <ExternalPathfinderUrl>/findPath?from=xxx&to=yyy&amount=zzz
        var baseUrl = _indexerContext.Settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/findPath?from={flowRequest.Source}&to={flowRequest.Sink}&amount={flowRequest.TargetFlow}";

        // Add the parameters if they are set
        if (flowRequest.FromTokens != null && flowRequest.FromTokens.Any())
        {
            foreach (var token in flowRequest.FromTokens)
            {
                url += $"&fromTokens={token}";
            }
        }

        if (flowRequest.ToTokens != null && flowRequest.ToTokens.Any())
        {
            foreach (var token in flowRequest.ToTokens)
            {
                url += $"&toTokens={token}";
            }
        }

        if (flowRequest.ExcludedFromTokens != null && flowRequest.ExcludedFromTokens.Any())
        {
            foreach (var token in flowRequest.ExcludedFromTokens)
            {
                url += $"&excludedFromTokens={token}";
            }
        }

        if (flowRequest.ExcludedToTokens != null && flowRequest.ExcludedToTokens.Any())
        {
            foreach (var token in flowRequest.ExcludedToTokens)
            {
                url += $"&excludedToTokens={token}";
            }
        }

        if (flowRequest.WithWrap.HasValue)
        {
            url += $"&withWrap={flowRequest.WithWrap.Value}";
        }

        // Perform the GET request to the external pathfinder
        using var response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        // Expect a JSON response that can deserialize into MaxFlowResponse
        var maxFlowResponse = await response.Content.ReadFromJsonAsync<MaxFlowResponse>();
        if (maxFlowResponse == null)
        {
            throw new Exception("Failed to deserialize MaxFlowResponse from external pathfinder.");
        }

        sw.Stop();
        _totalTimeSpentProxying += sw.ElapsedMilliseconds;
        _totalProxyCount++;

        if (_totalProxyCount % 1000 == 0)
        {
            Console.WriteLine($"Total time spent proxying: {_totalTimeSpentProxying}ms");
            Console.WriteLine($"Total proxy count: {_totalProxyCount}");
            Console.WriteLine($"Avg. duration: {_totalTimeSpentProxying / _totalProxyCount}ms");
        }

        return ResultWrapper<MaxFlowResponse>.Success(maxFlowResponse);
    }

    public ResultWrapper<string> circles_health()
    {
        var blockHead = _indexerContext.NethermindApi.BlockTree?.Head?.Number
                        ?? throw new Exception("BlockTree or Head is null. The node is not ready yet or not synced.");

        var lastPersisted = _indexerContext.Database.LatestBlock() ?? 0;

        if (blockHead - lastPersisted >= 3)
        {
            return ResultWrapper<string>.Fail(
                $"Indexing is lagging behind. Block head: {blockHead}, last persisted: {lastPersisted}");
        }

        return ResultWrapper<string>.Success("Healthy");
    }

    public Task<ResultWrapper<IEnumerable<DatabaseNamespace>>> circles_tables()
    {
        var namespaces = new List<DatabaseNamespace>();
        foreach (var @namespace in _indexerContext.ReadonlyDatabase.Schema.Tables.GroupBy(o => o.Key.Namespace))
        {
            var namespaceDto = new DatabaseNamespace(@namespace.Key);
            namespaces.Add(namespaceDto);

            foreach (var table in @namespace)
            {
                var topic = table.Value.Topic.ToHexString(true);
                var tableDto = new DatabaseTable(table.Key.Table, topic);
                namespaceDto.Tables = namespaceDto.Tables.Append(tableDto).ToArray();

                foreach (var column in table.Value.Columns)
                {
                    var columnDto = new DatabaseColumn(column.Column, column.Type.ToString());
                    tableDto.Columns = tableDto.Columns.Append(columnDto).ToArray();
                }
            }
        }

        return Task.FromResult(ResultWrapper<IEnumerable<DatabaseNamespace>>.Success(namespaces));
    }

    private Dictionary<Address, TokenInfo> GetTokenExposureIds(Address address)
    {
        var tokenExposure = _indexerContext.ReadonlyDatabase.Select(new ParameterizedSql(@"
            WITH tokens AS (
                SELECT ""tokenAddress""
                     , 'CrcV1_Signup' as ""type""
                     , s.""user"" as ""tokenOwner""
                FROM  public.""CrcV1_Transfer"" t
                join ""CrcV1_Signup"" s on s.token = t.""tokenAddress""
                WHERE ""to"" = @address

                UNION ALL
                SELECT ""tokenAddress""
                     , case when rh.avatar is not null then 'CrcV2_RegisterHuman' else 'CrcV2_RegisterGroup' end as ""type""
                     , ""tokenAddress"" as ""tokenOwner""
                FROM  public.""CrcV2_TransferSingle"" ts
                left join ""CrcV2_RegisterHuman"" rh on rh.avatar = ts.""tokenAddress""
                WHERE ""to"" = @address

                UNION ALL
                SELECT ""tokenAddress""
                     , case when rh.avatar is not null then 'CrcV2_RegisterHuman' else 'CrcV2_RegisterGroup' end as ""type""
                     , ""tokenAddress"" as ""tokenOwner""
                FROM  public.""CrcV2_TransferBatch"" tb
                          left join ""CrcV2_RegisterHuman"" rh on rh.avatar = tb.""tokenAddress""
                WHERE ""to"" = @address

                UNION ALL
                SELECT ""tokenAddress""
                     , case when wd.""circlesType"" = 0 then 'CrcV2_ERC20WrapperDeployed_Demurraged' else 'CrcV2_ERC20WrapperDeployed_Inflationary' end as type
                     , wd.avatar as tokenOwner
                FROM  public.""CrcV2_Erc20WrapperTransfer"" wt
                join ""CrcV2_ERC20WrapperDeployed"" wd on wd.""erc20Wrapper"" = wt.""tokenAddress""
                WHERE ""to"" = @address
            ), distinct_tokens as (
                SELECT DISTINCT ""tokenAddress"", ""type"", ""tokenOwner""
                FROM   tokens
            )
            select ""tokenAddress"", ""type"", ""tokenOwner""
            from distinct_tokens
        ", [
            _indexerContext.ReadonlyDatabase.CreateParameter("address", address.ToString(true, false))
        ]));

        var rows = tokenExposure.Rows.ToArray();
        Dictionary<Address, TokenInfo> tokenExposureIds = new(rows.Length);
        foreach (var tokenExposureRow in tokenExposure.Rows)
        {
            var token = new Address((string)tokenExposureRow[0]);
            var tokenType = (string)tokenExposureRow[1];
            var tokenOwner = new Address((string)tokenExposureRow[2]);

            var isWrapped = tokenType is "CrcV2_ERC20WrapperDeployed_Inflationary"
                or "CrcV2_ERC20WrapperDeployed_Demurraged";

            var isInflationary = tokenType is "CrcV2_ERC20WrapperDeployed_Inflationary";
            var isGroup = tokenType is "CrcV2_RegisterGroup";

            var isErc20 = tokenType == "CrcV1_Signup"
                          || isWrapped;

            var isErc1155 = tokenType is "CrcV2_RegisterHuman"
                or "CrcV2_RegisterGroup";

            var version = isWrapped || isErc1155 ? 2 : 1;

            var tokenInfo = new TokenInfo(
                token,
                tokenOwner,
                tokenType,
                version,
                isErc20,
                isErc1155,
                isWrapped,
                isInflationary,
                isGroup);

            tokenExposureIds.Add(token, tokenInfo);
        }

        return tokenExposureIds;
    }
}