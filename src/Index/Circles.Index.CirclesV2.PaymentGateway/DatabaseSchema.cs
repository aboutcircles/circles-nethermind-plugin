using System.Numerics;
using Circles.Index.Common;

namespace Circles.Index.CirclesV2.PaymentGateway;

public class DatabaseSchema : BaseDatabaseSchema
{
    public const string Namespace = "CrcV2_PaymentGateway";

    // Factory
    public static readonly EventSchema GatewayCreated = EventSchema.FromSolidity(
        Namespace,
        "event GatewayCreated(address indexed owner, address indexed gateway)"
    );

    // Gateway
    public static readonly EventSchema PaymentReceived = EventSchema.FromSolidity(
        Namespace,
        "event PaymentReceived(address indexed payer, address indexed payee, address indexed gateway, uint256 tokenId, uint256 amount, bytes data)"
    );

    public static readonly EventSchema TrustUpdated = new(
        Namespace,
        "TrustUpdated",
        KeccakHelper.ComputeHash("TrustUpdated(address,address,uint96)"),
        new List<EventFieldSchema>
        {
            new("blockNumber", ValueTypes.Int, true, true),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, true, true),
            new("logIndex", ValueTypes.Int, true, true),
            new("transactionHash", ValueTypes.String, true),
            new("emitter", ValueTypes.String, true),
            new("gateway", ValueTypes.Address, true),
            new("trustReceiver", ValueTypes.Address, true),
            new("expiry", ValueTypes.BigInt, false)
        }
    );

    public DatabaseSchema()
    {
        AddMappings<Events.GatewayCreated>(
            ns: Namespace,
            table: nameof(GatewayCreated),
            eventSchema: GatewayCreated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("owner", e => e.Owner),
                ("gateway", e => e.Gateway)
            ]
        );

        AddMappings<Events.PaymentReceived>(
            ns: Namespace,
            table: nameof(PaymentReceived),
            eventSchema: PaymentReceived,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("payer", e => e.Payer),
                ("payee", e => e.Payee),
                ("gateway", e => e.Gateway),
                ("tokenId", e => (BigInteger)e.TokenId),
                ("amount", e => (BigInteger)e.Amount),
                ("data", e => e.Data)
            ]
        );

        AddMappings<Events.TrustUpdated>(
            ns: Namespace,
            table: nameof(TrustUpdated),
            eventSchema: TrustUpdated,
            databaseFieldMap:
            [
                ("emitter", e => e.Emitter),
                ("gateway", e => e.Gateway),
                ("trustReceiver", e => e.TrustReceiver),
                ("expiry", e => (BigInteger)e.Expiry)
            ]
        );
    }
}
