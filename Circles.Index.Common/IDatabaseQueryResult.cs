using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Circles.Index.Common;

[JsonConverter(typeof(DatabaseQueryResultConverter))]
public record DatabaseQueryResult(
    string[] Columns,
    IEnumerable<object?[]> Rows);

public class DatabaseQueryResultConverter : JsonConverter<DatabaseQueryResult>
{
    public override DatabaseQueryResult Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        throw new NotImplementedException("Deserialization is not implemented.");
    }

    public override void Write(Utf8JsonWriter writer, DatabaseQueryResult value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("Columns");
        JsonSerializer.Serialize(writer, value.Columns, options);

        writer.WritePropertyName("Rows");
        writer.WriteStartArray();
        foreach (var row in value.Rows)
        {
            writer.WriteStartArray();
            foreach (var item in row)
            {
                switch (item)
                {
                    case int i:
                        writer.WriteNumberValue(i);
                        break;
                    case long l:
                        writer.WriteNumberValue(l);
                        break;
                    case float f:
                        writer.WriteNumberValue(f);
                        break;
                    case double d:
                        writer.WriteNumberValue(d);
                        break;
                    case decimal dec:
                        writer.WriteNumberValue(dec);
                        break;
                    case BigInteger bigInt:
                        writer.WriteStringValue(bigInt.ToString());
                        break;
                    default:
                        JsonSerializer.Serialize(writer, item, options);
                        break;
                }
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }
}