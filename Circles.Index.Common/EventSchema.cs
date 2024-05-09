using System.Text;
using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

public record EventFieldSchema(string Column, ValueTypes Type, bool IsIndexed);

public class EventSchema(string @namespace, string table, Hash256 topic, List<EventFieldSchema> columns)
{
    public string Namespace { get; } = @namespace;
    public Hash256 Topic { get; } = topic;
    public string Table { get; } = table;
    public List<EventFieldSchema> Columns { get; } = columns;

    /// <summary>
    /// Parses a Solidity event definition and creates an EventSchema from it.
    /// 
    /// Example:
    /// ```
    /// event TransferBatch(
    ///  address indexed _operator, address indexed _from, address indexed _to, uint256[] _ids, uint256[] _values
    /// );
    /// ```
    /// </summary>
    /// <param name="namespace"></param>
    /// <param name="solidityEventDefinition"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException">Thrown when the event definition is invalid (according to this parser ;).</exception>
    /// <remarks>
    /// Doesn't support all Solidity types yet (most notably arrays). Please handle events with these types manually.
    /// </remarks>
    public static EventSchema FromSolidity(string @namespace, string solidityEventDefinition)
    {
        var trimmedDefinition = solidityEventDefinition.Trim();
        const string prefix = "event ";
        if (!trimmedDefinition.StartsWith(prefix))
        {
            throw new ArgumentException($"Invalid event definition. Must start with '${prefix}'.");
        }

        var eventDefinition = trimmedDefinition.Substring(prefix.Length);
        var openParenthesisIndex = eventDefinition.IndexOf('(');
        if (openParenthesisIndex == -1)
        {
            throw new ArgumentException("Invalid event definition. Must contain an opening parenthesis.");
        }

        var closeParenthesisIndex = eventDefinition.LastIndexOf(')');
        if (closeParenthesisIndex == -1)
        {
            throw new ArgumentException("Invalid event definition. Must contain a closing parenthesis.");
        }

        var eventName = eventDefinition.Substring(0, openParenthesisIndex).Trim();

        var parameters = eventDefinition
            .Substring(openParenthesisIndex + 1, closeParenthesisIndex - openParenthesisIndex - 1).Trim();

        var columnDefinitions = parameters.Split(',');
        var columns = new List<EventFieldSchema>
        {
            new("blockNumber", ValueTypes.Int, false),
            new("timestamp", ValueTypes.Int, true),
            new("transactionIndex", ValueTypes.Int, false),
            new("logIndex", ValueTypes.Int, false),
            new("transactionHash", ValueTypes.String, true)
        };

        StringBuilder eventTopic = new StringBuilder();
        eventTopic.Append(eventName);
        eventTopic.Append('(');

        for (int i = 0; i < columnDefinitions.Length; i++)
        {
            var columnDefinition = columnDefinitions[i];
            var parts = columnDefinition.Trim().Split(' ');
            if (parts.Length < 2)
            {
                throw new ArgumentException(
                    $"Invalid column definition '${columnDefinition}'. Must contain a type and a name.");
            }

            if (i > 0)
            {
                eventTopic.Append(',');
            }

            if (parts.Length == 2)
            {
                var type = MapSolidityType(parts[0].Trim());
                eventTopic.Append(parts[0].Trim());

                var columnName = parts[1].Trim();
                columns.Add(new EventFieldSchema(columnName, type, false));
            }

            if (parts.Length == 3)
            {
                var type = MapSolidityType(parts[0].Trim());
                eventTopic.Append(parts[0].Trim());

                var isIndexed = parts[1].Trim() == "indexed";
                if (!isIndexed)
                {
                    throw new ArgumentException(
                        $"Invalid column definition '${columnDefinition}'.");
                }

                var columnName = parts[2].Trim();
                columns.Add(new EventFieldSchema(columnName, type, isIndexed));
            }
        }

        eventTopic.Append(')');

        Hash256 topic = Keccak.Compute(eventTopic.ToString());
        return new EventSchema(@namespace, eventName, topic, columns);
    }

    private static ValueTypes MapSolidityType(string type)
    {
        return type switch
        {
            "address" => ValueTypes.Address,
            "uint8" => ValueTypes.Int,
            "uint16" => ValueTypes.Int,
            "uint32" => ValueTypes.Int,
            "uint64" => ValueTypes.Int,
            "uint128" => ValueTypes.BigInt,
            "uint256" => ValueTypes.BigInt,
            "string" => ValueTypes.String,
            "bool" => ValueTypes.Boolean,
            _ => throw new ArgumentException(
                $"'{type}' is not supported. Please handle this event manually.")
        };
    }
}