using System.Collections;
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

public record TokenInfo(
    Address TokenAddress,
    Address TokenOwner,
    string TokenType,
    int Version,
    bool isErc20,
    bool isErc1155,
    bool IsWrapped,
    bool IsInflationary);

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

    public async Task<ResultWrapper<string>> circles_getTotalBalance(Address address, bool? asTimeCircles = true)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();

        var balances = await GetTokenBalancesForAccount(rentedEthRpcModule.RpcModule!, address,
            _indexerContext.Settings.CirclesV2HubAddress);

        var v1Balances = balances.Where(o => o.Version == 1);

        if (asTimeCircles == true || asTimeCircles == null)
        {
            var totalBalance = v1Balances
                .Select(o => o.Balance)
                .Select(decimal.Parse)
                .Sum();

            return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            var totalBalance = v1Balances.Select(o => o.InflationaryBalance)
                .Select(decimal.Parse)
                .Sum();

            return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
        }
    }

    public async Task<ResultWrapper<string>> circlesV2_getTotalBalance(Address address, bool? asTimeCircles = true)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();

        var balances = await GetTokenBalancesForAccount(rentedEthRpcModule.RpcModule!, address,
            _indexerContext.Settings.CirclesV2HubAddress);

        var v2Balances = balances.Where(o => o.Version == 2);

        if (asTimeCircles == true || asTimeCircles == null)
        {
            var totalBalance = v2Balances
                .Select(o => o.Balance)
                .Select(decimal.Parse)
                .Sum();

            return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            var totalBalance = v2Balances.Select(o => o.InflationaryBalance)
                .Select(decimal.Parse)
                .Sum();

            return ResultWrapper<string>.Success(totalBalance.ToString(CultureInfo.InvariantCulture));
        }
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

    public async Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address)
    {
        using RentedEthRpcModule rentedEthRpcModule = new(_indexerContext.NethermindApi);
        await rentedEthRpcModule.Rent();

        var balances = await GetTokenBalancesForAccount(rentedEthRpcModule.RpcModule!, address,
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

    #region private methods

    private UInt256 GetErc20Balance(IEthRpcModule rpcModule, Address token, Address account)
    {
        byte[] functionSelector = Keccak.Compute("balanceOf(address)").Bytes.Slice(0, 4).ToArray();
        byte[] addressBytes = account.Bytes.PadLeft(32);
        byte[] data = functionSelector.Concat(addressBytes).ToArray();

        TransactionForRpc transactionCall = new()
        {
            To = token,
            Input = data
        };

        ResultWrapper<string> result = rpcModule.eth_call(transactionCall);
        if (result.ErrorCode != 0)
        {
            throw new Exception($"Couldn't get the balance of token {token} for account {account}");
        }

        byte[] uint256Bytes = Convert.FromHexString(result.Data.Substring(2));
        UInt256 tokenBalance = new(uint256Bytes, true);

        return tokenBalance;
    }

    private UInt256 GetErc1155Balance(IEthRpcModule rpcModule, Address tokenContract, UInt256 tokenId, Address account)
    {
        byte[] functionSelector = Keccak.Compute("balanceOf(address,uint256)").Bytes.Slice(0, 4).ToArray();
        byte[] addressBytes = account.Bytes.PadLeft(32);
        byte[] tokenIdBytes = tokenId.PaddedBytes(32);
        byte[] data = functionSelector.Concat(addressBytes).Concat(tokenIdBytes).ToArray();

        TransactionForRpc transactionCall = new()
        {
            To = tokenContract,
            Input = data
        };

        ResultWrapper<string> result = rpcModule.eth_call(transactionCall);
        if (result.ErrorCode != 0)
        {
            throw new Exception(
                $"Couldn't get the balance of token (hex: {tokenIdBytes.ToHexString()}; dec: {tokenId}) for account {account}. Error code: {result.ErrorCode}; Error message: {result.Result}");
        }

        byte[] uint256Bytes = Convert.FromHexString(result.Data.Substring(2));
        UInt256 tokenBalance = new(uint256Bytes, true);

        return tokenBalance;
    }

    private static UInt256 AddressToUInt256(Address address)
    {
        return new(address.Bytes, true);
    }

    private static Address AddressFromUInt256(UInt256 uint256)
    {
        return new Address(uint256.ToBigEndian()[12..].ToHexString());
    }

    private static decimal ToTimeCircles(UInt256 tokenBalance)
    {
        var balance = FormatCircles(tokenBalance);
        var tcBalance = TimeCirclesConverter.CrcToTc(DateTime.Now, decimal.Parse(balance));

        return tcBalance;
    }

    private static decimal ToInflationCircles(UInt256 tokenBalance)
    {
        var balance = FormatCircles(tokenBalance);
        var crcBalance = TimeCirclesConverter.TcToCrc(DateTime.Now, decimal.Parse(balance));

        return crcBalance;
    }

    private static string FormatCircles(UInt256 tokenBalance)
    {
        var ether = BigInteger.Divide((BigInteger)tokenBalance, BigInteger.Pow(10, 18));
        var remainder = BigInteger.Remainder((BigInteger)tokenBalance, BigInteger.Pow(10, 18));
        var remainderString = remainder.ToString("D18").TrimEnd('0');

        return remainderString.Length > 0
            ? $"{ether}.{remainderString}"
            : ether.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<List<CirclesTokenBalance>> GetTokenBalancesForAccount(IEthRpcModule rpcModule, Address address,
        Address hubAddress)
    {
        var tokens = GetTokensByAccount(address);
        var balances = new Dictionary<Address, Task<UInt256>>();
        foreach (var token in tokens)
        {
            if (token.isErc20)
            {
                balances.Add(token.TokenAddress,
                    Task.Run(async () =>
                    {
                        using var rpc = new RentedEthRpcModule(_indexerContext.NethermindApi);
                        await rpc.Rent();
                        return GetErc20Balance(rpc.RpcModule!, token.TokenAddress, address);
                    }));
            }
            else if (token.isErc1155)
            {
                balances.Add(token.TokenAddress, Task.Run(async () =>
                {
                    using var rpc = new RentedEthRpcModule(_indexerContext.NethermindApi);
                    await rpc.Rent();
                    return GetErc1155Balance(rpc.RpcModule!, hubAddress, AddressToUInt256(token.TokenAddress), address);
                }));
            }
        }

        await Task.WhenAll(balances.Values);

        var results = new List<CirclesTokenBalance>();
        foreach (var token in tokens)
        {
            var rawBalance = balances[token.TokenAddress].Result;
            string? demurragedBalance = null;
            string? inflationaryBalance = null;

            if (token.IsInflationary)
            {
                inflationaryBalance = ToInflationCircles(rawBalance).ToString(CultureInfo.CurrentCulture);
            }
            else
            {
                demurragedBalance = ToTimeCircles(rawBalance).ToString(CultureInfo.CurrentCulture);
            }

            if (demurragedBalance == null && inflationaryBalance != null)
            {
                demurragedBalance = TimeCirclesConverter.CrcToTc(DateTime.Now, ToInflationCircles(rawBalance))
                    .ToString(CultureInfo.InvariantCulture);
            }
            else if (inflationaryBalance == null && demurragedBalance != null)
            {
                inflationaryBalance = TimeCirclesConverter.TcToCrc(DateTime.Now, ToTimeCircles(rawBalance))
                    .ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                throw new Exception("Unexpected state during tc <-> crc conversion");
            }

            var tokenId = new UInt256(token.TokenAddress.Bytes, true);
            var tokenAddress = token.TokenAddress.ToString(true, false);

            results.Add(new(tokenId, tokenAddress, demurragedBalance, token.TokenOwner.ToString(true, false),
                token.TokenType, inflationaryBalance, token.Version));
        }

        return results;
    }

    /// <summary>
    /// Returns a dictionary of token ids for the account grouped by token type.
    /// </summary>
    /// <param name="address">The address of the account.</param>
    /// <returns>A dictionary of token ids for the account grouped by token type.</returns>
    private List<TokenInfo> GetTokensByAccount(Address address)
    {
        // All distinct tokens and types from transfers to the account
        var selectTokenExposure = new Select(
            "V_Crc"
            , "Transfers"
            , ["id"]
            , [
                new FilterPredicate("to", FilterType.Equals, address.ToString(true, false)),
                new FilterPredicate("type", FilterType.NotEquals, "CrcV1_HubTransfer"),
            ]
            , Array.Empty<OrderBy>()
            , int.MaxValue
            , true);

        var selectTokenExposureSql = selectTokenExposure.ToSql(_indexerContext.Database);

        var tokenExposureRows = _indexerContext.Database
            .Select(selectTokenExposureSql)
            .Rows
            .ToArray();

        Console.WriteLine("TokenExposure of account " + address + ":");
        foreach (var tokenExposureRow in tokenExposureRows)
        {
            Console.WriteLine($"* id: {tokenExposureRow[0]}");
        }

        var tokenExposureRawIds = tokenExposureRows
            .Select(o => o[0]?.ToString() ?? throw new Exception("An id in the result set is null")).ToArray();

        // Convert erc1155 token ids to address hex strings
        var erc1155TokenIds = tokenExposureRawIds.Where(o => !o.StartsWith("0x")).Select(UInt256.Parse).ToArray();
        var erc1155TokenAddresses = erc1155TokenIds.Select(o => o.ToBigEndian()[12..].ToHexString())
            .Select(o => new Address(o)).ToArray();
        var erc20TokenAddresses =
            tokenExposureRawIds.Where(o => o.StartsWith("0x")).Select(o => new Address(o)).ToArray();

        var allTokenAddresses = erc1155TokenAddresses.Concat(erc20TokenAddresses).ToArray();
        var allTokenAddressStrings = allTokenAddresses.Select(o => o.ToString(true, false)).ToArray();

        // Get token infos (is wrapped? is group? ...) 
        var selectTokenInfos = new Select(
            "V_Crc",
            "Tokens",
            ["token", "type", "tokenOwner", "version"],
            [
                new FilterPredicate("token", FilterType.In, allTokenAddressStrings)
            ],
            [],
            int.MaxValue);

        var selectTokenInfosSql = selectTokenInfos.ToSql(_indexerContext.Database);
        var tokenInfoRows = _indexerContext.Database.Select(selectTokenInfosSql).Rows.ToArray();
        var tokenInfoByAddress = tokenInfoRows.ToDictionary(
            o => new Address(o[0]?.ToString() ?? throw new Exception("A token in the result set is null")));

        var tokenInfos = new List<TokenInfo>(allTokenAddresses.Length);
        foreach (var token in allTokenAddresses)
        {
            if (!tokenInfoByAddress.TryGetValue(token, out var row))
            {
                throw new Exception($"Token {token} not found in token info result set");
            }

            bool isInflationary = false;
            bool isWrapped = false;
            bool isErc20 = false;
            bool isErc1155 = false;

            var type = row[1]?.ToString() ?? throw new Exception("A type in the result set is null");
            var tokenOwner =
                new Address(row[2]?.ToString() ?? throw new Exception("A tokenOwner in the result set is null"));
            var version = int.Parse(row[3]?.ToString() ?? throw new Exception("A version in the result set is null"));

            switch (type)
            {
                case "CrcV1_Signup":
                    isInflationary = true;
                    isErc20 = true;
                    break;
                case "CrcV2_RegisterHuman":
                case "CrcV2_RegisterGroup":
                    isErc1155 = true;
                    break;
                case "CrcV2_ERC20WrapperDeployed_Inflationary":
                    isInflationary = true;
                    isWrapped = true;
                    isErc20 = true;
                    break;
                case "CrcV2_ERC20WrapperDeployed_Demurraged":
                    isWrapped = true;
                    isErc20 = true;
                    break;
                default:
                    throw new Exception("Unknown token type: " + row);
            }

            tokenInfos.Add(new TokenInfo(
                token,
                tokenOwner,
                type,
                version,
                isErc20,
                isErc1155,
                isWrapped,
                isInflationary));
        }

        return tokenInfos;
    }

    #endregion
}