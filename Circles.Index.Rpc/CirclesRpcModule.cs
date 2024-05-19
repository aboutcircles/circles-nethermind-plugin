using System.Globalization;
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

    public async Task<ResultWrapper<string>> circles_getTotalBalance(Address address)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();

        UInt256 totalBalance = TotalBalance(rentedEthRpcModule.RpcModule!, address);
        return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
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

        foreach (var resultRow in result.rows)
        {
            var user = new Address(resultRow[0].ToString() ?? throw new Exception("A user in the result set is null"));
            var canSendTo = new Address(resultRow[1].ToString() ??
                                        throw new Exception("A canSendTo in the result set is null"));
            var limit = int.Parse(resultRow[2].ToString() ?? throw new Exception("A limit in the result set is null"));

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

    public async Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();

        var balances =
            CirclesTokenBalances(rentedEthRpcModule.RpcModule!, address);

        return ResultWrapper<CirclesTokenBalance[]>.Success(balances.ToArray());
    }

    public async Task<ResultWrapper<CirclesTokenBalanceV2[]>> circlesV2_getTokenBalances(Address address)
    {
        _pluginLogger.Info("circlesV2_getTokenBalances");
        _pluginLogger.Info($"address: {address}");
        _pluginLogger.Info("Renting EthRpcModule ..");
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();
        _pluginLogger.Info("EthRpcModule rented");

        var balances =
            V2CirclesTokenBalances(_pluginLogger, rentedEthRpcModule.RpcModule!, address,
                _indexerContext.Settings.CirclesV2HubAddress);

        return ResultWrapper<CirclesTokenBalanceV2[]>.Success(balances.ToArray());
    }

    public ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query)
    {
        Select select = query.ToModel();
        var parameterizedSql = select.ToSql(_indexerContext.Database);

        Console.WriteLine("circles_query: parameterizedSql:");
        Console.WriteLine(parameterizedSql.Sql);
        Console.WriteLine(string.Join(", ",
            parameterizedSql.Parameters.Select(p => $" * {p.ParameterName}={p.Value}")));


        var result = _indexerContext.Database.Select(parameterizedSql);

        // Log the .net types of the columns of the first row of the result set:
        foreach (var resultRow in result.rows)
        {
            for (int colIdx = 0; colIdx < resultRow.Length; colIdx++)
            {
                var colName = result.columns[colIdx];
                var colValue = resultRow[colIdx];

                _pluginLogger.Info($"Column '{colName}' is of type '{colValue?.GetType().Name ?? "null"}'");
            }

            break;
        }

        return ResultWrapper<DatabaseQueryResult>.Success(result);
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
            .rows
            .Select(o => new Address(o[0].ToString()
                                     ?? throw new Exception("A token address in the result set is null"))
            );
    }

    private List<CirclesTokenBalance> CirclesTokenBalances(IEthRpcModule rpcModule, Address address)
    {
        IEnumerable<Address> tokens = TokenAddressesForAccount(address);

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

            balances.Add(new CirclesTokenBalance(token, tokenBalance.ToString(CultureInfo.InvariantCulture)));
        }

        return balances;
    }

    private List<CirclesTokenBalanceV2> V2CirclesTokenBalances(ILogger logger, IEthRpcModule rpcModule, Address address,
        Address hubAddress)
    {
        IEnumerable<UInt256> tokenIds = V2TokenIdsForAccount(logger, address);

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

            balances.Add(new CirclesTokenBalanceV2(tokenId, tokenBalance.ToString(CultureInfo.InvariantCulture)));
        }

        return balances;
    }

    private IEnumerable<UInt256> V2TokenIdsForAccount(ILogger logger, Address address)
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

        logger.Info("V2TokenIdsForAccount:");
        logger.Info($"sql: {sql.Sql}");
        logger.Info($"sql.Parameters: {string.Join(", ", sql.Parameters.Select(p => p.Value))}");

        return _indexerContext.Database
            .Select(sql)
            .rows
            .Select(o => UInt256.Parse(o[0]?.ToString()
                                       ?? throw new Exception("A token id in the result set is null"))
            );
    }

    private UInt256 TotalBalance(IEthRpcModule rpcModule, Address address)
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

        return totalBalance;
    }

    #endregion
}