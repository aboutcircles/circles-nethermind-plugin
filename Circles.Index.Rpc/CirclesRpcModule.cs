using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Circles.Index.Rpc.Queries;
using Circles.Pathfinder.DTOs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Npgsql;

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
            : GetBalance(rpcModule, hubAddress, account, AddressConverter.AddressToUInt256(token));

        return balance;
    }

    private async Task<List<CirclesTokenBalance>> GetTokenBalancesForAccount(Address address, Address hubAddress)
    {
        var tokens = await GetTokenExposureIds(address);

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
                erc1155Tokens.Select(o => AddressConverter.AddressToUInt256(o.TokenAddress)).ToArray());

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
                var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var rawBalance = (BigInteger)balances[token.TokenAddress];

                BigInteger attoCircles;
                decimal cirlces;

                BigInteger attoCrc;
                decimal crc;

                BigInteger staticAttoCircles;
                decimal staticCircles;

                if (token.TokenType == "CrcV1_Signup")
                {
                    // OG CRC
                    attoCrc = rawBalance;
                    crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                    attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, now);
                    cirlces = CirclesConverter.AttoCirclesToCircles(attoCircles);

                    staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                    staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                }
                else
                {
                    if (token.IsInflationary)
                    {
                        staticAttoCircles = rawBalance;
                        staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                        attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                        cirlces = CirclesConverter.AttoCirclesToCircles(attoCircles);

                        attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                        crc = CirclesConverter.AttoCirclesToCircles(attoCrc);
                    }
                    else
                    {
                        attoCircles = rawBalance;
                        cirlces = CirclesConverter.AttoCirclesToCircles(attoCircles);

                        attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                        crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                        staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                        staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);
                    }
                }

                var tokenAddress = token.TokenAddress;
                var tokenAddressString = tokenAddress.ToString(true, false);
                var tokenId = AddressConverter.AddressToUInt256(tokenAddress)
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

    public ResultWrapper<AvatarRow> circles_getAvatarInfo(Address address)
    {
        var avatarInfoResult = GetAvatarInfoBatch([address]);
        if (avatarInfoResult[0] == null)
        {
            return ResultWrapper<AvatarRow>.Fail($"No avatar found for address {address}");
        }

        return ResultWrapper<AvatarRow>.Success(avatarInfoResult[0]);
    }

    public ResultWrapper<AvatarRow?[]> circles_getAvatarInfoBatch(Address[] addresses)
    {
        if (addresses.Length > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(addresses), "Too many addresses. Max allowed are 1000.");
        }

        var result = GetAvatarInfoBatch(addresses);
        return ResultWrapper<AvatarRow?[]>.Success(result);
    }

    private static AvatarRow?[] GetAvatarInfoBatch(Address[] addresses)
    {
        AvatarRow?[] result = new AvatarRow?[addresses.Length];
        for (int i = 0; i < addresses.Length; i++)
        {
            var avatarAddress = addresses[i];
            string addressString = avatarAddress.ToString(true, false);

            bool hasV1 = CirclesV1.LogParser.V1Avatars.TryGetValue(addressString, out var v1Avatar);
            bool hasV2 = CirclesV2.LogParser.V2Avatars.TryGetValue(addressString, out var v2Avatar);

            if (!hasV1 && !hasV2)
            {
                result[i] = null;
                continue;
            }

            int version = hasV2 ? 2 : 1;
            string type = hasV2 ? v2Avatar.Type : v1Avatar.Type;
            string avatar = addressString;
            string tokenId = hasV2 ? v2Avatar.TokenId : v1Avatar.TokenAddress;
            string? v1Token = hasV1 ? v1Avatar.TokenAddress : null;

            bool hasV1Cid = CirclesV1.NameRegistry.LogParser.V1AvatarToCidMap.TryGetValue(avatarAddress, out var v1Cid);
            bool hasV2Cid = CirclesV2.NameRegistry.LogParser.V2AvatarToCidMap.TryGetValue(avatarAddress, out var v2Cid);

            string? cid = hasV2Cid ? v2Cid : hasV1Cid ? v1Cid : null;
            bool isHuman = type == "CrcV2_RegisterHuman" || type == "CrcV1_Signup";

            result[i] = new AvatarRow(
                version,
                type,
                avatar,
                tokenId,
                hasV1,
                v1Token,
                "",
                cid,
                isHuman,
                hasV2 ? v2Avatar.Name : null,
                "");
        }

        return result;
    }

    public async Task<ResultWrapper<CirclesTrustRelations>> circles_getTrustRelations(Address address)
    {
        await using var conn = new NpgsqlConnection(_indexerContext.Settings.IndexReadonlyDbConnectionString);
        await conn.OpenAsync();

        var rows = await TrustRelationsQuery.ExecuteAsync(
            conn,
            address: address.ToString(true, false));

        var trustRelations = ParseTrustRelations(rows
                .Select(r => new object[] { r.User, r.CanSendTo, r.Limit }), // keep old parser
            address);

        return ResultWrapper<CirclesTrustRelations>.Success(trustRelations);
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

    public async Task<ResultWrapper<Address[]>> circles_getCommonTrust(
        Address address1,
        Address address2,
        int? version = null)
    {
        await using var conn = new NpgsqlConnection(_indexerContext.Settings.IndexReadonlyDbConnectionString);
        await conn.OpenAsync();

        var addr1 = address1.ToString(true, false);
        var addr2 = address2.ToString(true, false);

        IEnumerable<CommonTrustQuery.Row> rows = version switch
        {
            1 => await CommonTrustQuery.ExecuteV1Async(conn, addr1, addr2),
            2 => await CommonTrustQuery.ExecuteV2Async(conn, addr1, addr2),
            _ => (await Task.WhenAll(
                    CommonTrustQuery.ExecuteV1Async(conn, addr1, addr2),
                    CommonTrustQuery.ExecuteV2Async(conn, addr1, addr2)))
                .SelectMany(x => x)
        };

        var result = rows
            .Select(r => new Address(r.Trustee))
            .Distinct()
            .ToArray();

        return ResultWrapper<Address[]>.Success(result);
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

        if (!hasV1Balance)
        {
            v1Balances = ImmutableDictionary<string, (BigInteger Balance, string TokenOwner)>.Empty;
        }

        var hasV2Balance =
            CirclesV2.LogParser.BalancesByAccountAndToken.TryGetValue(address.ToString(true, false),
                out var v2Balances);

        if (!hasV2Balance)
        {
            v2Balances =
                ImmutableDictionary<string, (BigInteger Balance, TokenValueRepresentation ValueRepresentation, string
                    TokenOwner)>.Empty;
        }

        if (!hasV1Balance && !hasV2Balance)
        {
            return ResultWrapper<IEnumerable<CirclesTokenBalance>>.Fail(
                "No balances found");
        }

        List<CirclesTokenBalance> result = new();

        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var v1Balance in v1Balances)
        {
            // V1 circles are ERC20 and always inflationary
            BigInteger attoCrc = v1Balance.Value.Balance;
            decimal crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

            BigInteger attoCircles = CirclesConverter.AttoCrcToAttoCircles(attoCrc, now);
            decimal circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

            BigInteger staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
            decimal staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

            result.Add(new CirclesTokenBalance(
                v1Balance.Key,
                v1Balance.Key,
                v1Balance.Value.TokenOwner,
                "CrcV1_Signup",
                1,
                attoCircles.ToString(NumberFormatInfo.InvariantInfo),
                circles,
                staticAttoCircles.ToString(NumberFormatInfo.InvariantInfo),
                staticCircles,
                attoCrc.ToString(NumberFormatInfo.InvariantInfo),
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
                BigInteger staticAttoCircles = v2TokenBalance.Value.Balance;
                decimal staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                BigInteger attoCircles = CirclesConverter.AttoStaticCirclesToAttoCircles(staticAttoCircles);
                decimal circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                BigInteger attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                decimal crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                result.Add(new CirclesTokenBalance(
                    v2TokenBalance.Key,
                    v2TokenBalance.Key,
                    v2TokenBalance.Value.TokenOwner,
                    "CrcV2_ERC20WrapperDeployed_Inflationary",
                    2,
                    attoCircles.ToString(NumberFormatInfo.InvariantInfo),
                    circles,
                    staticAttoCircles.ToString(NumberFormatInfo.InvariantInfo),
                    staticCircles,
                    attoCrc.ToString(NumberFormatInfo.InvariantInfo),
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
                if (!CirclesV2.LogParser.LastTokenMovement.TryGetValue(
                        (address.ToString(true, false), v2TokenBalance.Key),
                        out var lastTokenMovement))
                {
                    throw new InvalidOperationException(
                        $"Account {address} has a token {v2TokenBalance.Key} that was never moved.");
                }

                ulong today = CirclesConverter.DayFromTimestamp(DateTimeOffset.UtcNow, 1_602_720_000);
                var (attoCircles, _) = Demurrage.ApplyDemurrage(
                    storedBalance: v2TokenBalance.Value.Item1,
                    storedDay: (ulong)lastTokenMovement,
                    targetDay: today);
                decimal circles = CirclesConverter.AttoCirclesToCircles(attoCircles);

                BigInteger staticAttoCircles = CirclesConverter.AttoCirclesToAttoStaticCircles(attoCircles);
                decimal staticCircles = CirclesConverter.AttoCirclesToCircles(staticAttoCircles);

                BigInteger attoCrc = CirclesConverter.AttoCirclesToAttoCrc(attoCircles, now);
                decimal crc = CirclesConverter.AttoCirclesToCircles(attoCrc);

                result.Add(new CirclesTokenBalance(
                    v2TokenBalance.Key,
                    !isWrapped
                        ? AddressConverter.AddressToUInt256(new Address(v2TokenBalance.Key))
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
                    staticAttoCircles.ToString(NumberFormatInfo.InvariantInfo),
                    staticCircles,
                    attoCrc.ToString(NumberFormatInfo.InvariantInfo),
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

        var baseUrl = _indexerContext.Settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/findPath"; // POST body carries the payload

        // Send the request
        using var response = await HttpClient.PostAsJsonAsync(url, flowRequest);

        response.EnsureSuccessStatusCode();

        var maxFlowResponse = await response.Content.ReadFromJsonAsync<MaxFlowResponse>();
        var failedToParse = maxFlowResponse is null;
        if (failedToParse)
        {
            throw new Exception("Failed to deserialize MaxFlowResponse from external pathfinder.");
        }

        sw.Stop();
        _totalTimeSpentProxying += sw.ElapsedMilliseconds;
        _totalProxyCount++;

        var shouldLogStats = _totalProxyCount % 1000 == 0;
        if (shouldLogStats)
        {
            Console.WriteLine($"Total time spent proxying: {_totalTimeSpentProxying}ms");
            Console.WriteLine($"Total proxy count: {_totalProxyCount}");
            Console.WriteLine($"Avg. duration: {_totalTimeSpentProxying / _totalProxyCount}ms");
        }

        return ResultWrapper<MaxFlowResponse>.Success(maxFlowResponse!);
    }

    public async Task<ResultWrapper<JsonElement>> circles_getNetworkSnapshot()
    {
        var sw = Stopwatch.StartNew();
        var baseUrl = _indexerContext.Settings.ExternalPathfinderUrl.TrimEnd('/');
        var url = $"{baseUrl}/snapshot";

        // Ask only for headers first – we’re going to stream the body.
        using var response = await HttpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();

        // Parse to JsonDocument so we can hand a JsonElement out.
        using var doc = await JsonDocument.ParseAsync(stream);
        JsonElement snapshot = doc.RootElement.Clone(); // detach from 'doc'

        sw.Stop();
        _totalTimeSpentProxying += sw.ElapsedMilliseconds;
        _totalProxyCount++;

        if (_totalProxyCount % 1000 == 0)
        {
            Console.WriteLine($"Total time spent proxying: {_totalTimeSpentProxying}ms");
            Console.WriteLine($"Total proxy count: {_totalProxyCount}");
            Console.WriteLine($"Avg. duration: {_totalTimeSpentProxying / _totalProxyCount}ms");
        }

        return ResultWrapper<JsonElement>.Success(snapshot);
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

    private async Task<Dictionary<Address, TokenInfo>> GetTokenExposureIds(Address address)
    {
        await using var conn = new NpgsqlConnection(_indexerContext.Settings.IndexReadonlyDbConnectionString);
        await conn.OpenAsync();

        var rows = await TokenExposureIdsQuery.ExecuteAsync(
            conn,
            address: address.ToString(true, false));

        var dict = new Dictionary<Address, TokenInfo>();

        foreach (var r in rows)
        {
            var token = new Address(r.TokenAddress);
            var tokenOwner = new Address(r.TokenOwner);
            var tokenType = r.Type;

            bool isWrapped = tokenType.StartsWith("CrcV2_ERC20Wrapper");
            bool isInflationary = tokenType.EndsWith("Inflationary");
            bool isGroup = tokenType == "CrcV2_RegisterGroup";
            bool isErc20 = tokenType == "CrcV1_Signup" || isWrapped;
            bool isErc1155 = tokenType is "CrcV2_RegisterHuman" or "CrcV2_RegisterGroup";
            int version = isWrapped || isErc1155 ? 2 : 1;

            dict[token] = new TokenInfo(
                token, tokenOwner, tokenType, version,
                isErc20, isErc1155, isWrapped, isInflationary, isGroup);
        }

        return dict;
    }

    public async Task<ResultWrapper<Profile>> circles_getProfileByCid(string cid)
    {
        await using var conn = new NpgsqlConnection(_indexerContext.Settings.IndexReadonlyDbConnectionString);
        await conn.OpenAsync();

        var row = await circles_getProfileByCidQuery.ExecuteAsync(conn, cid);

        if (row is null)
        {
            return ResultWrapper<Profile>.Fail($"No profile found for cid {cid}");
        }

        var profile = JsonSerializer.Deserialize<Profile>(row.Payload);
        return ResultWrapper<Profile>.Success(profile!);
    }

    public async Task<ResultWrapper<Profile?[]>> circles_getProfileByCidBatch(string[] cids)
    {
        if (cids.Length > 100)
        {
            return ResultWrapper<Profile?[]>.Fail("Batch size exceeds 100");
        }

        await using var conn = new NpgsqlConnection(_indexerContext.Settings.IndexReadonlyDbConnectionString);
        await conn.OpenAsync();

        var rows = await circles_getProfileByCidBatchQuery.ExecuteAsync(conn, cids);

        var result = rows
            .Select(r => r.Payload is null
                ? null
                : JsonSerializer.Deserialize<Profile>(r.Payload))
            .ToArray();

        return ResultWrapper<Profile?[]>.Success(result);
    }

    public async Task<ResultWrapper<Profile>> circles_getProfileByAddress(Address avatar)
    {
        bool hasV1Cid = CirclesV1.NameRegistry.LogParser.V1AvatarToCidMap.TryGetValue(avatar, out var v1Cid);
        bool hasV2Cid = CirclesV2.NameRegistry.LogParser.V2AvatarToCidMap.TryGetValue(avatar, out var v2Cid);

        var cid = hasV2Cid ? v2Cid : hasV1Cid ? v1Cid : null;

        Profile? result;

        if (cid == null)
        {
            // Check if the account has a name and return this instead.
            var hasV2Name = CirclesV2.LogParser.V2Avatars.TryGetValue(avatar.ToString(true, false), out var v2Avatar);
            if (!hasV2Name)
            {
                return ResultWrapper<Profile>.Fail($"No profile found for avatar {avatar}");
            }

            result = new Profile(
                null,
                null,
                null,
                v2Avatar.Name,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }
        else
        {
            var profileByCidResultWrapper = await circles_getProfileByCid(cid);
            if (profileByCidResultWrapper.ErrorCode != 0)
            {
                return ResultWrapper<Profile>.Fail(
                    $"Error retrieving profile for {avatar} by CID {cid}: {profileByCidResultWrapper.ErrorCode}");
            }

            result = profileByCidResultWrapper.Data;
        }

        var hasShortName = CirclesV2.NameRegistry.LogParser.V2AvatarToShortNameMap.TryGetValue(
            avatar,
            out var shortName);

        var enrichedProfile = result with
        {
            address = avatar.ToString(true, false),
            shortName = hasShortName ? shortName : null
        };

        return ResultWrapper<Profile>.Success(enrichedProfile);
    }

    public async Task<ResultWrapper<Profile?[]>> circles_getProfileByAddressBatch(Address?[] avatars)
    {
        if (avatars.Length > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(avatars), "Batch size exceeds 100");
        }

        var cids = avatars.Select(avatar =>
            {
                if (avatar == null)
                {
                    return null;
                }

                bool hasV1Cid = CirclesV1.NameRegistry.LogParser.V1AvatarToCidMap.TryGetValue(avatar, out var v1Cid);
                bool hasV2Cid = CirclesV2.NameRegistry.LogParser.V2AvatarToCidMap.TryGetValue(avatar, out var v2Cid);
                return hasV2Cid ? v2Cid : hasV1Cid ? v1Cid : null;
            })
            .ToArray();

        var result = await circles_getProfileByCidBatch(cids);
        if (result.ErrorCode != 0)
        {
            throw new Exception($"Error during batch profile retrieval");
        }

        var enrichedProfiles = new List<Profile?>(avatars.Length);
        for (int i = 0; i < avatars.Length; i++)
        {
            if (result.Data[i] == null)
            {
                enrichedProfiles.Add(null);
                continue;
            }

            var profile = result.Data[i];
            if (profile == null)
            {
                enrichedProfiles.Add(null);
                continue;
            }

            bool hasShortName =
                CirclesV2.NameRegistry.LogParser.V2AvatarToShortNameMap.TryGetValue(avatars[i]!, out var shortName);
            var enrichedProfile = profile with
            {
                address = avatars[i]!.ToString(true, false),
                shortName = hasShortName ? shortName : null
            };
            enrichedProfiles.Add(enrichedProfile);
        }

        return ResultWrapper<Profile?[]>.Success(enrichedProfiles.ToArray());
    }

    public Task<ResultWrapper<TokenInfo>> circles_getTokenInfo(Address tokenAddress)
    {
        var isV1 = CirclesV1.LogParser.CirclesV1TokenOwnersByToken.TryGetValue(tokenAddress, out var v1TokenOwner);
        if (isV1)
        {
            return ResultWrapper<TokenInfo>.Success(new TokenInfo(tokenAddress, v1TokenOwner,
                "CrcV1_Signup", 1, true, false, false, true, false));
        }

        var isV2_1155 = CirclesV2.LogParser.V2Avatars.TryGetValue(tokenAddress.ToString(true, false), out var v2Avatar);
        if (isV2_1155)
        {
            return ResultWrapper<TokenInfo>.Success(new TokenInfo(tokenAddress, tokenAddress,
                v2Avatar.Type, 2, false, true, false, false, v2Avatar.Type == "CrcV2_RegisterGroup"));
        }

        var isV2_20 =
            CirclesV2.LogParser.Erc20WrapperAddresses.TryGetValue(tokenAddress.ToString(true, false), out var v2Erc20);
        if (isV2_20)
        {
            bool isGroup = CirclesV2.LogParser.Groups.ContainsKey(v2Erc20.TokenOwner);
            return ResultWrapper<TokenInfo>.Success(new TokenInfo(tokenAddress, new Address(v2Erc20.TokenOwner),
                v2Erc20.ValueRepresentation == TokenValueRepresentation.DemurragedWrapped
                    ? "CrcV2_ERC20WrapperDeployed_Demurraged"
                    : "CrcV2_ERC20WrapperDeployed_Inflationary", 2, true, false, true,
                v2Erc20.ValueRepresentation.HasFlag(TokenValueRepresentation.Inflationary),
                isGroup));
        }

        return ResultWrapper<TokenInfo>.Fail($"No token info found for {tokenAddress}.");
    }

    public async Task<ResultWrapper<TokenInfo?[]>> circles_getTokenInfoBatch(Address[] tokenAddresses)
    {
        if (tokenAddresses.Length > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenAddresses), "Batch size exceeds 100");
        }

        var getTokenInfoTasks = tokenAddresses.Select(circles_getTokenInfo).ToArray();
        var results = await Task.WhenAll(getTokenInfoTasks);

        List<TokenInfo?> tokenInfos = new(tokenAddresses.Length);
        for (int i = 0; i < tokenAddresses.Length; i++)
        {
            if (results[i].ErrorCode != 0)
            {
                tokenInfos.Add(null);
            }
            else
            {
                tokenInfos.Add(results[i].Data);
            }
        }

        return ResultWrapper<TokenInfo?[]>.Success(tokenInfos.ToArray());
    }

    public async Task<ResultWrapper<Profile[]>> circles_searchProfiles(string text, int? limit = 20, int? offset = 0)
    {
        const int hardLimit = 100;
        int take = limit ?? 20;
        int skip = offset ?? 0;

        if (take > hardLimit)
        {
            return ResultWrapper<Profile[]>.Fail($"limit must not exceed {hardLimit}.");
        }

        string trimmed = text.Trim();
        string[] terms = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (terms.All(t => t.Length <= 1))
        {
            return ResultWrapper<Profile[]>.Success(Array.Empty<Profile>());
        }

        if (terms.Length > 3)
        {
            return ResultWrapper<Profile[]>.Fail("Too many search terms. Maximum is 3.");
        }

        string qText = string.Join(' ', terms);

        await using var conn = new NpgsqlConnection(_indexerContext.Settings.IndexReadonlyDbConnectionString);
        await conn.OpenAsync();

        var rows = await SearchProfilesQuery.ExecuteAsync(conn, qText, take, skip);

        var profiles = rows.Select(r =>
        {
            // payload present → real profile
            if (r.Payload is not null)
            {
                var prof = JsonSerializer.Deserialize<Profile>(r.Payload);
                return prof with { address = r.Avatar, shortName = r.Short_Name };
            }

            // otherwise synthesise minimal profile
            return new Profile(
                address: r.Avatar,
                CID: null,
                lastUpdatedAt: null,
                name: r.Avatar_Name,
                description: null,
                registeredName: null,
                location: null,
                imageUrl: null,
                previewImageUrl: null,
                geoLocation: null,
                longitude: null,
                latitude: null,
                shortName: r.Short_Name);
        }).ToArray();

        return ResultWrapper<Profile[]>.Success(profiles);
    }
}