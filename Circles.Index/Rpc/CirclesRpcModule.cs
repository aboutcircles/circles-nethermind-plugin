using System.Globalization;
using Circles.Index.Common;
using Circles.Index.Utils;
using Nethermind.Api;
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
    private readonly INethermindApi _nethermindApi;
    private readonly IDatabase _database;

    public CirclesRpcModule(INethermindApi nethermindApi, IDatabase database)
    {
        ILogger baseLogger = nethermindApi.LogManager.GetClassLogger();
        _nethermindApi = nethermindApi;
        _pluginLogger = new LoggerWithPrefix("Circles.Index.Rpc:", baseLogger);
        _database = database;
    }

    public async Task<ResultWrapper<string>> circles_getTotalBalance(Address address)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_nethermindApi);
        await rentedEthRpcModule.Rent();

        UInt256 totalBalance = TotalBalance(rentedEthRpcModule.RpcModule!, address);
        return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_nethermindApi);
        await rentedEthRpcModule.Rent();

        var balances =
            CirclesTokenBalances(rentedEthRpcModule.RpcModule!, address);

        return ResultWrapper<CirclesTokenBalance[]>.Success(balances.ToArray());
    }

    public ResultWrapper<IEnumerable<object[]>> circles_query(CirclesQuery query)
    {
        if (query.Table == null)
        {
            throw new InvalidOperationException("Table is null");
        }

        Tables parsedTableName = Enum.Parse<Tables>(query.Table);

        var select = Query.Select(parsedTableName,
            query.Columns?.Select(Enum.Parse<Columns>)
            ?? throw new InvalidOperationException("Columns are null"));

        if (query.Conditions.Count != 0)
        {
            foreach (var condition in query.Conditions)
            {
                select.Where(BuildCondition(select.Table, condition));
            }
        }

        if (query.OrderBy.Count != 0)
        {
            foreach (var orderBy in query.OrderBy)
            {
                if (orderBy.Column == null || orderBy.SortOrder == null)
                {
                    throw new InvalidOperationException("OrderBy: Column or SortOrder is null");
                }

                Columns parsedColumnName = Enum.Parse<Columns>(orderBy.Column);
                select.OrderBy.Add((
                    parsedColumnName,
                    orderBy.SortOrder.Equals("asc", StringComparison.InvariantCultureIgnoreCase)
                        ? SortOrder.Asc
                        : SortOrder.Desc));
            }
        }

        Console.WriteLine(select.ToString());
        var result = _database.Select(select).ToList();

        return ResultWrapper<IEnumerable<object[]>>.Success(result);
    }

    public ResultWrapper<string> circles_computeTransfer(string from, string to, string amount)
    {
        // string result = LibPathfinder.ffi_compute_transfer(from, to, amount);
        return ResultWrapper<string>.Success("");
    }

    #region private methods

    private IEnumerable<Address> TokenAddressesForAccount(Address circlesAccount)
    {
        var select = Query.Select(
                Tables.Erc20Transfer
                , new[]
                {
                    Columns.TokenAddress
                })
            .Where(
                Query.Equals(
                    Tables.Erc20Transfer
                    , Columns.ToAddress
                    , circlesAccount.ToString(true, false)));

        return _database.Select(select)
            .Select(o => o[0]).Cast<byte[]>().Select(o => new Address(o));
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

    private IQuery BuildCondition(Tables table, QueryExpression queryExpression)
    {
        if (queryExpression.Type == "Equals")
        {
            Columns parsedColumnName = Enum.Parse<Columns>(queryExpression.Column!);
            return Query.Equals(table, parsedColumnName, queryExpression.Value);
        }

        if (queryExpression.Type == "GreaterThan")
        {
            Columns parsedColumnName = Enum.Parse<Columns>(queryExpression.Column!);
            return Query.GreaterThan(table, parsedColumnName, queryExpression.Value!);
        }

        if (queryExpression.Type == "LessThan")
        {
            Columns parsedColumnName = Enum.Parse<Columns>(queryExpression.Column!);
            return Query.LessThan(table, parsedColumnName, queryExpression.Value!);
        }

        if (queryExpression.Type == "And")
        {
            return Query.And(queryExpression.Elements!.Select(o => BuildCondition(table, o)).ToArray());
        }

        if (queryExpression.Type == "Or")
        {
            return Query.Or(queryExpression.Elements!.Select(o => BuildCondition(table, o)).ToArray());
        }

        throw new InvalidOperationException($"Unknown expression type: {queryExpression.Type}");
    }

    #endregion
}