using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Circles.Index.Common;

public class UInt256AsStringConverter : JsonConverter<UInt256>
{
    public override UInt256 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Expect a decimal string (e.g. "1234567890123456789"):
        string? decimalString = reader.GetString();
        if (string.IsNullOrEmpty(decimalString))
            return UInt256.Zero;

        // Parse as System.Numerics.BigInteger, then cast:
        BigInteger bigInt = BigInteger.Parse(decimalString);
        return (UInt256)bigInt;
    }

    public override void Write(Utf8JsonWriter writer, UInt256 value, JsonSerializerOptions options)
    {
        // Convert to BigInteger and write as decimal string
        BigInteger bigInt = (BigInteger)value;
        writer.WriteStringValue(bigInt.ToString());
    }
}