using System.Globalization;
using System.Numerics;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Eth;
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

        _pluginLogger.Info($"Calling GetBalance with token: {token}, account: {account}, tokenId: {tokenId}");

        var transactionCall = new TransactionForRpc { To = token, Input = data };
        var result = rpcModule.eth_call(transactionCall);

        if (result.ErrorCode != 0)
        {
            _pluginLogger.Error($"Error in eth_call: {result.ErrorCode} - {result.Result.Error}");
            throw new Exception($"Couldn't get the balance of token {token} for account {account}");
        }

        byte[] uint256Bytes = Convert.FromHexString(result.Data.Substring(2));
        var balance = new UInt256(uint256Bytes, true);

        _pluginLogger.Info($"Received balance: {balance}");
        return balance;
    }

    private async Task<UInt256> FetchBalance(Address token, Address account, bool isErc20, Address hubAddress)
    {
        _pluginLogger.Info($"Fetching balance for token: {token}, account: {account}, isErc20: {isErc20}");
        using var rpc = new RentedEthRpcModule(_indexerContext.NethermindApi);
        await rpc.Rent();
        var balance = isErc20
            ? GetBalance(rpc.RpcModule!, token, account)
            : GetBalance(rpc.RpcModule!, hubAddress, account, Utils.ConversionUtils.AddressToUInt256(token));

        _pluginLogger.Info($"Fetched balance: {balance} for token: {token}, account: {account}");
        return balance;
    }

    private async Task<List<CirclesTokenBalance>> GetTokenBalancesForAccount(Address address, Address hubAddress)
    {
        _pluginLogger.Info($"Getting token balances for account: {address}");

        var tokens = GetTokensByAccount(address);
        var balanceTasks = tokens.ToDictionary(
            token => token.TokenAddress,
            token => FetchBalance(token.TokenAddress, address, token.IsErc20, hubAddress));

        await Task.WhenAll(balanceTasks.Values);

        decimal AttoCirclesToCircles(UInt256 weiBalance)
        {
            // Convert to Ether by dividing by 10^18 (Ether has 18 decimal places)
            BigInteger weiInEth = BigInteger.Pow(new BigInteger(10), 18);
            decimal etherValue = (decimal)weiBalance / (decimal)weiInEth;

            // Returning the converted value as a decimal
            return etherValue;
        }

        UInt256 CirclesToAttoCircles(decimal circlesBalance)
        {
            // Convert to Wei by multiplying by 10^18 (Ether has 18 decimal places)
            BigInteger weiInEth = BigInteger.Pow(new BigInteger(10), 18);
            decimal weiValue = circlesBalance * (decimal)weiInEth;

            // Returning the converted value as a UInt256
            var bigint = new BigInteger(weiValue);
            return (UInt256)bigint;
        }

        var tokenBalances = tokens.Select(token =>
            {
                var rawBalance = balanceTasks[token.TokenAddress].Result;

                UInt256 demurragedBalanceAttoCircles;
                decimal demurragedBalanceCircles;
                UInt256 inflationaryBalanceAttoCircles;
                decimal inflationaryBalanceCircles;

                if (token.IsInflationary)
                {
                    inflationaryBalanceAttoCircles = rawBalance;
                    inflationaryBalanceCircles = AttoCirclesToCircles(rawBalance);

                    demurragedBalanceCircles = TimeCirclesConverter.CrcToTc(DateTime.Now, inflationaryBalanceCircles);
                    demurragedBalanceAttoCircles = CirclesToAttoCircles(demurragedBalanceCircles);
                }
                else
                {
                    demurragedBalanceAttoCircles = rawBalance;
                    demurragedBalanceCircles = AttoCirclesToCircles(rawBalance);

                    inflationaryBalanceCircles = TimeCirclesConverter.TcToCrc(DateTime.Now, demurragedBalanceCircles);
                    inflationaryBalanceAttoCircles = CirclesToAttoCircles(inflationaryBalanceCircles);
                }

                var tokenAddress = token.TokenAddress;
                var tokenAddressString = tokenAddress.ToString(true, false);
                var tokenId = Utils.ConversionUtils.AddressToUInt256(tokenAddress)
                    .ToString(CultureInfo.InvariantCulture);

                return new CirclesTokenBalance(
                    tokenAddressString,
                    tokenId,
                    token.TokenOwner.ToString(true, false),
                    token.TokenType,
                    token.Version,
                    demurragedBalanceAttoCircles.ToString(CultureInfo.InvariantCulture),
                    demurragedBalanceCircles,
                    inflationaryBalanceAttoCircles.ToString(CultureInfo.InvariantCulture),
                    inflationaryBalanceCircles);
            })
            .Where(o => o.DemurragedBalanceCircles > 0)
            .OrderByDescending(o => o.InflationaryBalanceCircles)
            .ToList();

        _pluginLogger.Info($"Token balances retrieved for account {address}: {tokenBalances.Count} tokens.");
        return tokenBalances;
    }

    private async Task<ResultWrapper<string>> GetTotalBalance(Address address, int version, bool? asTimeCircles)
    {
        _pluginLogger.Info(
            $"Getting total balance for account: {address}, version: {version}, asTimeCircles: {asTimeCircles}");

        var balances = await GetTokenBalancesForAccount(address, _indexerContext.Settings.CirclesV2HubAddress);
        var relevantBalances = balances.Where(o => o.Version == version);

        if (asTimeCircles == null || asTimeCircles == true)
        {
            var totalBalance = relevantBalances
                .Select(o => o.DemurragedBalanceCircles)
                .Sum();

            _pluginLogger.Info($"Total balance for account {address}: {totalBalance}");
            return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            var totalBalance = relevantBalances
                .Select(o => o.DemurragedBalanceAttoCircles)
                .Aggregate(UInt256.Zero, (acc, val) => acc + UInt256.Parse(val));

            _pluginLogger.Info($"Total balance for account {address}: {totalBalance}");
            return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
        }
    }

    private List<TokenInfo> GetTokensByAccount(Address address)
    {
        _pluginLogger.Info($"Getting tokens for account: {address}");

        var tokenExposureRawIds = GetTokenExposureIds(address);

        _pluginLogger.Info($"Token exposure raw ids: {string.Join(", ", tokenExposureRawIds)}");

        var erc1155TokenAddresses = tokenExposureRawIds
            .Where(id => !id.StartsWith("0x"))
            .Select(id => new Address(UInt256.Parse(id).ToBigEndian()[12..].ToHexString()))
            .ToArray();

        var erc20TokenAddresses = tokenExposureRawIds
            .Where(id => id.StartsWith("0x"))
            .Select(id => new Address(id))
            .ToArray();

        var allTokenAddresses = erc1155TokenAddresses.Concat(erc20TokenAddresses).ToArray();
        var allTokenAddressStrings = allTokenAddresses.Select(o => o.ToString(true, false)).ToArray();

        var tokenInfoByAddress = FetchTokenInfo(allTokenAddressStrings);

        _pluginLogger.Info($"Fetched token info for account {address}");

        return allTokenAddresses.Select(token =>
        {
            if (!tokenInfoByAddress.TryGetValue(token, out var row))
                throw new Exception($"Token {token} not found in token info result set");

            var (isErc20, isErc1155, isWrapped, isInflationary) = ParseTokenType(row[1]);
            var tokenOwner = new Address(row[2]);
            var version = int.Parse(row[3]);

            return new TokenInfo(token, tokenOwner, row[1], version, isErc20, isErc1155, isWrapped, isInflationary);
        }).ToList();
    }

    private Dictionary<Address, string[]> FetchTokenInfo(string[] allTokenAddressStrings)
    {
        _pluginLogger.Info($"Fetching token info for token addresses: {string.Join(", ", allTokenAddressStrings)}");

        var selectTokenInfos = new Select(
            "V_Crc",
            "Tokens",
            ["token", "type", "tokenOwner", "version"],
            [new FilterPredicate("token", FilterType.In, allTokenAddressStrings)],
            Array.Empty<OrderBy>(),
            int.MaxValue);

        var sql = selectTokenInfos.ToSql(_indexerContext.Database);
        var result = _indexerContext.Database.Select(sql).Rows.ToDictionary(
            row => new Address(row[0]?.ToString() ?? throw new Exception("A token in the result set is null")),
            row => row.Select(o => o?.ToString() ?? throw new Exception("A value in the result set is null"))
                .ToArray());

        _pluginLogger.Info($"Token info retrieved for {allTokenAddressStrings.Length} tokens.");
        return result;
    }

    private (bool isErc20, bool isErc1155, bool isWrapped, bool isInflationary) ParseTokenType(string type)
    {
        return type switch
        {
            "CrcV1_Signup" => (true, false, false, true),
            "CrcV2_RegisterHuman" or "CrcV2_RegisterGroup" => (false, true, false, false),
            "CrcV2_ERC20WrapperDeployed_Inflationary" => (true, false, true, true),
            "CrcV2_ERC20WrapperDeployed_Demurraged" => (true, false, true, false),
            _ => throw new Exception($"Unknown token type: {type}")
        };
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
            _indexerContext.Database.CreateParameter("address", address.ToString(true, false))
        };

        var result = _indexerContext.Database.Select(new ParameterizedSql(sql, parameters));
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

    public ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query)
    {
        Select select = query.ToModel();
        var parameterizedSql = select.ToSql(_indexerContext.Database);

        StringWriter stringWriter = new();
        stringWriter.WriteLine($"circles_query(SelectDto query):");
        stringWriter.WriteLine($"  select: {parameterizedSql.Sql}");
        stringWriter.WriteLine($"  parameters:");
        foreach (var parameter in parameterizedSql.Parameters)
        {
            stringWriter.WriteLine($"    {parameter.ParameterName}: {parameter.Value}");
        }

        _pluginLogger.Info(stringWriter.ToString());

        var result = _indexerContext.Database.Select(parameterizedSql);

        return ResultWrapper<DatabaseQueryResult>.Success(result);
    }

    public ResultWrapper<CirclesEvent[]> circles_events(Address address, long fromBlock, long? toBlock = null)
    {
        var queryEvents = new QueryEvents(_indexerContext);
        return ResultWrapper<CirclesEvent[]>.Success(queryEvents.CirclesEvents(address, fromBlock, toBlock));
    }

    private string[] GetTokenExposureIds(Address address)
    {
        var selectTokenExposure = new Select(
            "V_Crc",
            "Transfers",
            ["id"],
            [
                new FilterPredicate("to", FilterType.Equals, address.ToString(true, false)),
                new FilterPredicate("type", FilterType.NotEquals, "CrcV1_HubTransfer")
            ],
            Array.Empty<OrderBy>(),
            int.MaxValue,
            true);

        var sql = selectTokenExposure.ToSql(_indexerContext.Database);
        return _indexerContext.Database.Select(sql)
            .Rows
            .Select(row => row[0]?.ToString() ?? throw new Exception("An id in the result set is null"))
            .ToArray();
    }
}