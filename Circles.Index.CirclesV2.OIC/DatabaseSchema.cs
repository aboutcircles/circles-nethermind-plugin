using System.Numerics;
using Circles.Index.Common;

namespace Circles.Index.CirclesV2.OIC;

public class DatabaseSchema : BaseDatabaseSchema
{
    public static readonly EventSchema OpenMiddlewareTransfer = EventSchema.FromSolidity(
        "CrcV2_OIC",
        "event OpenMiddlewareTransfer(address indexed onBehalf, address indexed sender, address indexed recipient, uint256 amount, uint256 inflationaryAmount, bytes data)"
    );

    public DatabaseSchema()
    {
        AddMappings<OpenMiddlewareTransfer>(
            ns: "CrcV2_OIC",
            table: "OpenMiddlewareTransfer",
            eventSchema: OpenMiddlewareTransfer,
            databaseFieldMap:
            [
                ("onBehalf", e => e.OnBehalf),
                ("sender", e => e.Sender),
                ("recipient", e => e.Recipient),
                ("amount", e => (BigInteger)e.Amount),
                ("inflationaryAmount", e => (BigInteger)e.InflationaryAmount),
                ("data", e => e.Data)
            ]
        );
    }
}
