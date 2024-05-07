namespace Circles.Index.Common;

public record ColumnSchema(Columns Column, ValueTypes Type, bool IsPrimaryKey, bool IsIndexed);

public class TableSchema(Tables table, List<ColumnSchema> columns)
{
    public Tables Table { get; } = table;
    public List<ColumnSchema> Columns { get; } = columns;

    // public static TableSchema FromSolidity(string solidityEventDefinition)
    // {
    //     /* Parse e.g. the following event definition and create a TableSchema from it:
    //        event TransferBatch(
    //            address indexed _operator, address indexed _from, address indexed _to, uint256[] _ids, uint256[] _values
    //        );
    //      */
    //     var trimmedDefinition = solidityEventDefinition.Trim();
    //     const string prefix = "event ";
    //     if (!trimmedDefinition.StartsWith(prefix))
    //     {
    //         throw new ArgumentException($"Invalid event definition. Must start with '${prefix}'.");
    //     }
    //
    //     var eventDefinition = trimmedDefinition.Substring(prefix.Length);
    //     var openParenthesisIndex = eventDefinition.IndexOf('(');
    //     if (openParenthesisIndex == -1)
    //     {
    //         throw new ArgumentException("Invalid event definition. Must contain an opening parenthesis.");
    //     }
    //
    //     var closeParenthesisIndex = eventDefinition.LastIndexOf(')');
    //     if (closeParenthesisIndex == -1)
    //     {
    //         throw new ArgumentException("Invalid event definition. Must contain a closing parenthesis.");
    //     }
    //
    //     var tableName = eventDefinition.Substring(0, openParenthesisIndex).Trim();
    //     var tablesSchema = new TableSchema(new Tables(tableName), new List<ColumnSchema>());
    //     var parameters = eventDefinition
    //         .Substring(openParenthesisIndex + 1, closeParenthesisIndex - openParenthesisIndex - 1).Trim();
    //     var columnDefinitions = parameters.Split(',');
    //
    //     var columns = new List<ColumnSchema>();
    //     foreach (var columnDefinition in columnDefinitions)
    //     {
    //         var parts = columnDefinition.Trim().Split(' ');
    //         if (parts.Length != 2)
    //         {
    //             throw new ArgumentException("Invalid column definition. Must contain a type and a name.");
    //         }
    //
    //         var type = parts[0].Trim();
    //         var name = parts[1].Trim();
    //         var isIndexed = name.StartsWith("indexed ");
    //         if (isIndexed)
    //         {
    //             name = name.Substring("indexed ".Length);
    //         }
    //
    //         var column = new ColumnSchema(
    //             new Columns(name),
    //             type switch
    //             {
    //                 "address" => ValueTypes.Address,
    //                 "uint8" => ValueTypes.Int,
    //                 "uint16" => ValueTypes.Int,
    //                 "uint32" => ValueTypes.Int,
    //                 "uint64" => ValueTypes.Int,
    //                 "uint128" => ValueTypes.BigInt,
    //                 "uint256" => ValueTypes.BigInt,
    //                 "string" => ValueTypes.String,
    //                 _ => throw new ArgumentException($"'${type}' is not supported yet. Please handle this event manually.")
    //             },
    //             isIndexed,
    //             false
    //         );
    //         columns.Add(column);
    //     }
    // }
}