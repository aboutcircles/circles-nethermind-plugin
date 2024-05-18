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

        foreach (var resultRow in result.Rows)
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

    public ResultWrapper<DatabaseQueryResult> circles_query(SelectDto query)
    {
        Select select = query.ToModel();
        var parameterizedSql = select.ToSql(_indexerContext.Database);
        var result = _indexerContext.Database.Select(parameterizedSql);

        // Log the .net types of the columns of the first row of the result set:
        foreach (var resultRow in result.Rows)
        {
            for (int colIdx = 0; colIdx < resultRow.Length; colIdx++)
            {
                var colName = result.Columns[colIdx];
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
            .Rows
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