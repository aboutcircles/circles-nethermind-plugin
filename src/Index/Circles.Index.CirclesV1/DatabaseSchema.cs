using System.Numerics;
using Circles.Common;

namespace Circles.Index.CirclesV1;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema HubTransfer = EventSchema.FromSolidity("CrcV1",
        "event HubTransfer(address indexed from, address indexed to, uint256 amount)");

    public static readonly EventSchema Signup = EventSchema.FromSolidity("CrcV1",
        "event Signup(address indexed user, address indexed token)");

    public static readonly EventSchema OrganizationSignup = EventSchema.FromSolidity("CrcV1",
        "event OrganizationSignup(address indexed organization)");

    public static readonly EventSchema Trust = EventSchema.FromSolidity("CrcV1",
        "event Trust(address indexed canSendTo, address indexed user, uint256 limit)");

    public static readonly EventSchema Transfer = new("CrcV1", "Transfer",
        KeccakHelper.ComputeHash("Transfer(address,address,uint256)"),
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("tokenAddress", ValueTypes.Address, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false)
        ]);

    public static readonly EventSchema TransferSummary = new("CrcV1", "TransferSummary",
        new byte[32],
        [
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("from", ValueTypes.Address, true),
            new("to", ValueTypes.Address, true),
            new("amount", ValueTypes.BigInt, false),
            new("events", ValueTypes.Json, false)
        ]);

    public DatabaseSchema()
    {
        AddMappings<Signup>(
            ns: "CrcV1",
            table: "Signup",
            eventSchema: Signup,
            databaseFieldMap:
            [
                ("user", e => e.User),
                ("token", e => e.Token)
            ]
        );

        AddMappings<OrganizationSignup>(
            ns: "CrcV1",
            table: "OrganizationSignup",
            eventSchema: OrganizationSignup,
            databaseFieldMap:
            [
                ("organization", e => e.Organization)
            ]
        );

        AddMappings<Trust>(
            ns: "CrcV1",
            table: "Trust",
            eventSchema: Trust,
            databaseFieldMap:
            [
                ("canSendTo", e => e.CanSendTo),
                ("user", e => e.User),
                ("limit", e => e.Limit)
            ]
        );

        AddMappings<HubTransfer>(
            ns: "CrcV1",
            table: "HubTransfer",
            eventSchema: HubTransfer,
            databaseFieldMap:
            [
                ("from", e => e.From),
                ("to", e => e.To),
                ("amount", e => (BigInteger)e.Amount)
            ]
        );

        AddMappings<Transfer>(
            ns: "CrcV1",
            table: "Transfer",
            eventSchema: Transfer,
            databaseFieldMap:
            [
                ("tokenAddress", e => e.TokenAddress),
                ("from", e => e.From),
                ("to", e => e.To),
                ("amount", e => (BigInteger)e.Value)
            ]
        );

        AddMappings<TransferSummary>(
            ns: "CrcV1",
            table: "TransferSummary",
            eventSchema: TransferSummary,
            databaseFieldMap:
            [
                ("from", e => e.From),
                ("to", e => e.To),
                ("amount", e => (BigInteger)e.Amount),
                ("events", e => e.Events)
            ]
        );
    }
}