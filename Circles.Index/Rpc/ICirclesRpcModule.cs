using Circles.Index.Data;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Circles.Index.Rpc;

#region DTOs

public class CirclesTokenBalance
{
    public Address Token { get; }
    public string Balance { get; }

    public CirclesTokenBalance(Address token, string balance)
    {
        Token = token;
        Balance = balance;
    }
}

#endregion


public class CirclesQuery
{
    public string? Table { get; set; }
    public string[]? Columns { get; set; }
    public List<Expression> Conditions { get; set; } = new();
    
    public List<OrderBy> OrderBy { get; set; } = new();
}

public class OrderBy
{
    public string? Column { get; set; }
    public string? SortOrder { get; set; }
}

public class Expression
{
    public string? Type { get; set; }  // "Equals", "GreaterThan", "LessThan", "And", "Or"
    public string? Column { get; set; }  // Null for composite types like "And" and "Or"
    public object? Value { get; set; }  // Null for composite types
    public List<Expression>? Elements { get; set; }  // Used only for "And" and "Or"
}

[RpcModule("Circles")]
public interface ICirclesRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Gets the Circles balance of the specified address", IsImplemented = true)]
    Task<ResultWrapper<string>> circles_getTotalBalance(Address address);

    [JsonRpcMethod(Description = "Gets the balance of each Circles token the specified address holds",
        IsImplemented = true)]
    Task<ResultWrapper<CirclesTokenBalance[]>> circles_getTokenBalances(Address address);

    [JsonRpcMethod(Description = "Queries the data of one Circles index table",
        IsImplemented = true)]
    ResultWrapper<IEnumerable<object[]>> circles_query(CirclesQuery query);

    [JsonRpcMethod(Description = "Calculates a transitive transfer path along the trust relations of a user",
        IsImplemented = true)]
    ResultWrapper<string> circles_computeTransfer(string from, string to, string amount);
}