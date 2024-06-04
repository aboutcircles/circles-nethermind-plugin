using System.Globalization;
using System.Numerics;
using Circles.Index.Common;
using Circles.Index.Query;
using Circles.Index.Query.Dto;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;

namespace Circles.Index.Rpc;

public class CirclesRpcModule : ICirclesRpcModule
{
    private readonly ILogger _pluginLogger;
    private readonly Context _indexerContext;

    public CirclesRpcModule(Context indexerContext)
    {
        ILogger baseLogger = indexerContext.NethermindApi.LogManager.GetClassLogger();
        _pluginLogger = new LoggerWithPrefix("Circles.Index.Rpc:", baseLogger);
        _indexerContext = indexerContext;
    }

    public async Task<ResultWrapper<string>> circles_getTotalBalance(Address address, bool asTimeCircles = false)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();

        string totalBalance = TotalBalance(rentedEthRpcModule.RpcModule!, address, asTimeCircles);
        return ResultWrapper<string>.Success(totalBalance);
    }

    public Task<ResultWrapper<CirclesTrustRelations>> circles_getTrustRelations(Address address)
    {
        var sql = @"
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

        var parameterizedSql = new ParameterizedSql(sql, new[]
        {
            _indexerContext.Database.CreateParameter("address", address.ToString(true, false))
        });

        var result = _indexerContext.Database.Select(parameterizedSql);

        var incomingTrusts = new List<CirclesTrustRelation>();
        var outgoingTrusts = new List<CirclesTrustRelation>();

        foreach (var resultRow in result.Rows)
        {
            var user = new Address(resultRow[0]?.ToString() ?? throw new Exception("A user in the result set is null"));
            var canSendTo = new Address(resultRow[1]?.ToString() ??
                                        throw new Exception("A canSendTo in the result set is null"));
            var limit = int.Parse(resultRow[2]?.ToString() ?? throw new Exception("A limit in the result set is null"));

            if (user == address)
            {
                // user is the sender
                outgoingTrusts.Add(new CirclesTrustRelation(canSendTo, limit));
            }
            else
            {
                // user is the receiver
                incomingTrusts.Add(new CirclesTrustRelation(user, limit));
            }
        }

        var trustRelations = new CirclesTrustRelations(address, outgoingTrusts.ToArray(), incomingTrusts.ToArray());
        return Task.FromResult(ResultWrapper<CirclesTrustRelations>.Success(trustRelations));
    }

    public async Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address,
        bool asTimeCircles = false)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();

        var balances =
            CirclesTokenBalances(rentedEthRpcModule.RpcModule!, address, asTimeCircles);

        return ResultWrapper<CirclesTokenBalance[]>.Success(balances.ToArray());
    }

    public Task<ResultWrapper<string>> circlesV2_getTotalBalance(Address address, bool asTimeCircles = false)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        rentedEthRpcModule.Rent().Wait();

        string totalBalance = TotalBalanceV2(rentedEthRpcModule.RpcModule!, address, asTimeCircles);
        return Task.FromResult(ResultWrapper<string>.Success(totalBalance));
    }

    private string TotalBalanceV2(IEthRpcModule rpcModule, Address address, bool asTimeCircles)
    {
        IEnumerable<UInt256> tokenIds = V2TokenIdsForAccount(address);

        // Call the erc1155's balanceOf function for each token using _ethRpcModule.eth_call().
        // Solidity function signature: balanceOf(address _account, uint256 _id) public view returns (uint256)
        byte[] functionSelector = Keccak.Compute("balanceOf(address,uint256)").Bytes.Slice(0, 4).ToArray();
        byte[] addressBytes = address.Bytes.PadLeft(32);

        UInt256 totalBalance = UInt256.Zero;

        foreach (UInt256 tokenId in tokenIds)
        {
            byte[] tokenIdBytes = tokenId.PaddedBytes(32);
            byte[] data = functionSelector.Concat(addressBytes).Concat(tokenIdBytes).ToArray();

            TransactionForRpc transactionCall = new()
            {
                To = _indexerContext.Settings.CirclesV2HubAddress,
                Input = data
            };

            ResultWrapper<string> result = rpcModule.eth_call(transactionCall);
            if (result.ErrorCode != 0)
            {
                throw new Exception(
                    $"Couldn't get the balance of token (hex: {tokenIdBytes.ToHexString()}; dec: {tokenId}) for account {address}. Error code: {result.ErrorCode}; Error message: {result.Result}");
            }

            byte[] uint256Bytes = Convert.FromHexString(result.Data.Substring(2));
            UInt256 tokenBalance = new(uint256Bytes, true);
            totalBalance += tokenBalance;
        }

        return asTimeCircles
            ? FormatTimeCircles(totalBalance)
            : totalBalance.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<ResultWrapper<CirclesTokenBalanceV2[]>> circlesV2_getTokenBalances(Address address,
        bool asTimeCircles = false)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();

        var balances =
            V2CirclesTokenBalances(rentedEthRpcModule.RpcModule!, address,
                _indexerContext.Settings.CirclesV2HubAddress, asTimeCircles);

        return ResultWrapper<CirclesTokenBalanceV2[]>.Success(balances.ToArray());
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

    #region private methods

    private IEnumerable<Address> TokenAddressesForAccount(Address circlesAccount)
    {
        var select = new Select(
            "CrcV1"
            , "Transfer"
            , new[] { "tokenAddress" }
            , new[]
            {
                new FilterPredicate("to", FilterType.Equals, circlesAccount.ToString(true, false))
            }
            , Array.Empty<OrderBy>()
            , null
            , true);

        var sql = select.ToSql(_indexerContext.Database);
        return _indexerContext.Database
            .Select(sql)
            .Rows
            .Select(o => new Address(o[0]?.ToString()
                                     ?? throw new Exception("A token address in the result set is null"))
            );
    }

    private IDictionary<string, string> GetTokenOwners(string[] tokenAddresses)
    {
        // Construct a query for "V_Crc_Avatars" and select the "avatar" and "tokenId" columns.
        // Use an IN clause to filter the results by the token addresses.
        var select = new Select(
            "V_Crc"
            , "Avatars"
            , new[] { "avatar", "tokenId" }
            , new[]
            {
                new FilterPredicate("tokenId", FilterType.In, tokenAddresses.Select(o => o.ToLowerInvariant()))
            }
            , Array.Empty<OrderBy>()
            , null
            , true);

        var sql = select.ToSql(_indexerContext.Database);
        var result = _indexerContext.Database.Select(sql);

        var tokenOwners = new Dictionary<string, string>();
        foreach (var row in result.Rows)
        {
            var avatar = row[0]?.ToString() ?? throw new Exception("An avatar in the result set is null");
            var tokenId = row[1]?.ToString() ?? throw new Exception("A tokenId in the result set is null");
            tokenOwners[tokenId] = avatar;
        }

        return tokenOwners;
    }

    private List<CirclesTokenBalance> CirclesTokenBalances(IEthRpcModule rpcModule, Address address, bool asTimeCircles)
    {
        IEnumerable<Address> tokens = TokenAddressesForAccount(address).ToArray();
        IDictionary<string, string> tokenOwners = GetTokenOwners(tokens.Select(o => o.ToString(true, false)).ToArray());

        // Call the erc20's balanceOf function for each token using _ethRpcModule.eth_call():
        byte[] functionSelector = Keccak.Compute("balanceOf(address)").Bytes.Slice(0, 4).ToArray();
        byte[] addressBytes = address.Bytes.PadLeft(32);
        byte[] data = functionSelector.Concat(addressBytes).ToArray();

        List<CirclesTokenBalance> balances = new();

        foreach (Address token in tokens)
        {
            TransactionForRpc transactionCall = new()
            {
                To = token,
                Input = data
            };

            ResultWrapper<string> result = rpcModule.eth_call(transactionCall);
            if (result.ErrorCode != 0)
            {
                throw new Exception($"Couldn't get the balance of token {token} for account {address}");
            }

            byte[] uint256Bytes = Convert.FromHexString(result.Data.Substring(2));
            UInt256 tokenBalance = new(uint256Bytes, true);

            if (asTimeCircles)
            {
                var tcBalance = ToTimeCircles(tokenBalance);
                balances.Add(new CirclesTokenBalance(token, tcBalance.ToString(CultureInfo.InvariantCulture),
                    tokenOwners[token.ToString(true, false)]));
            }
            else
            {
                balances.Add(new CirclesTokenBalance(token, tokenBalance.ToString(CultureInfo.InvariantCulture),
                    tokenOwners[token.ToString(true, false)]));
            }
        }

        return balances;
    }

    private static decimal ToTimeCircles(UInt256 tokenBalance)
    {
        var balance = FormatTimeCircles(tokenBalance);
        var tcBalance = TimeCirclesConverter.CrcToTc(DateTime.Now, decimal.Parse(balance));

        return tcBalance;
    }

    private static string FormatTimeCircles(UInt256 tokenBalance)
    {
        var ether = BigInteger.Divide((BigInteger)tokenBalance, BigInteger.Pow(10, 18));
        var remainder = BigInteger.Remainder((BigInteger)tokenBalance, BigInteger.Pow(10, 18));
        var remainderString = remainder.ToString("D18").TrimEnd('0');

        return remainderString.Length > 0
            ? $"{ether}.{remainderString}"
            : ether.ToString(CultureInfo.InvariantCulture);
    }

    private List<CirclesTokenBalanceV2> V2CirclesTokenBalances(IEthRpcModule rpcModule, Address address,
        Address hubAddress, bool asTimeCircles)
    {
        IEnumerable<UInt256> tokenIds = V2TokenIdsForAccount(address);

        // Call the erc1155's balanceOf function for each token using _ethRpcModule.eth_call().
        // Solidity function signature: balanceOf(address _account, uint256 _id) public view returns (uint256)
        byte[] functionSelector = Keccak.Compute("balanceOf(address,uint256)").Bytes.Slice(0, 4).ToArray();
        byte[] addressBytes = address.Bytes.PadLeft(32);

        var balances = new List<CirclesTokenBalanceV2>();

        foreach (var tokenId in tokenIds)
        {
            byte[] tokenIdBytes = tokenId.PaddedBytes(32);
            byte[] data = functionSelector.Concat(addressBytes).Concat(tokenIdBytes).ToArray();

            TransactionForRpc transactionCall = new()
            {
                To = hubAddress,
                Input = data
            };

            ResultWrapper<string> result = rpcModule.eth_call(transactionCall);
            if (result.ErrorCode != 0)
            {
                throw new Exception(
                    $"Couldn't get the balance of token (hex: {tokenIdBytes.ToHexString()}; dec: {tokenId}) for account {address}. Error code: {result.ErrorCode}; Error message: {result.Result}");
            }

            byte[] uint256Bytes = Convert.FromHexString(result.Data.Substring(2));
            UInt256 tokenBalance = new(uint256Bytes, true);

            if (asTimeCircles)
            {
                var tcBalance = FormatTimeCircles(tokenBalance);
                balances.Add(new CirclesTokenBalanceV2(tokenId, tcBalance, tokenId.ToHexString(true)));
            }
            else
            {
                balances.Add(new CirclesTokenBalanceV2(tokenId, tokenBalance.ToString(CultureInfo.InvariantCulture),
                    tokenId.ToHexString(true)));
            }
        }

        return balances;
    }

    private IEnumerable<UInt256> V2TokenIdsForAccount(Address address)
    {
        var select = new Select(
            "V_CrcV2"
            , "Transfers"
            , new[] { "id" }
            , new[]
            {
                new FilterPredicate("to", FilterType.Equals, address.ToString(true, false))
            }
            , Array.Empty<OrderBy>()
            , null
            , true);

        var sql = select.ToSql(_indexerContext.Database);

        return _indexerContext.Database
            .Select(sql)
            .Rows
            .Select(o => UInt256.Parse(o[0]?.ToString()
                                       ?? throw new Exception("A token id in the result set is null"))
            );
    }

    private string TotalBalance(IEthRpcModule rpcModule, Address address, bool asTimeCircles)
    {
        IEnumerable<Address> tokens = TokenAddressesForAccount(address);

        // Call the erc20's balanceOf function for each token using _ethRpcModule.eth_call():
        byte[] functionSelector = Keccak.Compute("balanceOf(address)").Bytes.Slice(0, 4).ToArray();
        byte[] addressBytes = address.Bytes.PadLeft(32);
        byte[] data = functionSelector.Concat(addressBytes).ToArray();

        UInt256 totalBalance = UInt256.Zero;

        foreach (Address token in tokens)
        {
            TransactionForRpc transactionCall = new()
            {
                To = token,
                Input = data
            };

            ResultWrapper<string> result = rpcModule.eth_call(transactionCall);
            if (result.ErrorCode != 0)
            {
                throw new Exception($"Couldn't get the balance of token {token} for account {address}");
            }

            byte[] uint256Bytes = Convert.FromHexString(result.Data.Substring(2));
            UInt256 tokenBalance = new(uint256Bytes, true);
            totalBalance += tokenBalance;
        }

        return asTimeCircles
            ? ToTimeCircles(totalBalance).ToString(CultureInfo.InvariantCulture)
            : totalBalance.ToString(CultureInfo.InvariantCulture);
    }

    #endregion
}