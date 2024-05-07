using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

public class DatabaseSchema : IDatabaseSchema
{
    public IDictionary<string, EventSchema> Tables { get; } = new Dictionary<string, EventSchema>
    {
        {
            "Block",
            // Hash256 must be 32 bytes and was 0 bytes
            new EventSchema("Block", new Hash256(new byte[32]), [
                new("BlockNumber", ValueTypes.Int, true),
                new("Timestamp", ValueTypes.Int, true),
                new("BlockHash", ValueTypes.String, true)
            ])
        }
    };
}