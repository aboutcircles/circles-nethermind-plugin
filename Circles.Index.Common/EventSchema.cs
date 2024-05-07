using System.Text;
using Nethermind.Core.Crypto;

namespace Circles.Index.Common;

public record EventFieldSchema(string Column, ValueTypes Type, bool IsIndexed);

public class EventSchema(string table, Hash256 topic, List<EventFieldSchema> columns)
{
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
    /// <param name="solidityEventDefinition"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException">Thrown when the event definition is invalid (according to this parser ;).</exception>
    /// <remarks>
    /// Doesn't support all Solidity types yet (most notably arrays). Please handle events with these types manually.
    /// </remarks>
    public static EventSchema FromSolidity(string solidityEventDefinition)
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
        var columns = new List<EventFieldSchema>();

        StringBuilder sb = new StringBuilder();
        sb.Append(eventName);
        sb.Append("(");

        foreach (var columnDefinition in columnDefinitions)
        {
            var parts = columnDefinition.Trim().Split(' ');
            if (parts.Length < 2)
            {
                throw new ArgumentException(
                    $"Invalid column definition '${columnDefinition}'. Must contain a type and a name.");
            }

            if (parts.Length == 2)
            {
                var type = MapSolidityType(parts[0].Trim());
                var columnName = parts[1].Trim();
                columns.Add(new EventFieldSchema(columnName, type, false));
                sb.Append($"{type} {columnName}");
            }

            if (parts.Length == 3)
            {
                var isIndexed = parts[0].Trim() == "indexed";
                if (!isIndexed)
                {
                    throw new ArgumentException(
                        $"Invalid column definition '${columnDefinition}'.");
                }

                var type = MapSolidityType(parts[1].Trim());
                var columnName = parts[2].Trim();
                columns.Add(new EventFieldSchema(columnName, type, false));
                sb.Append($"{type} {columnName}");
            }
        }

        sb.Append(")");

        Hash256 topic = Keccak.Compute(sb.ToString());
        return new EventSchema(eventName, topic, columns);
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
            _ => throw new ArgumentException(
                $"'${type}' is not supported. Please handle this event manually.")
        };
    }
}