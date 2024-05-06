namespace Circles.Index.Common;

public record ColumnSchema(Columns Column, ValueTypes Type, bool IsPrimaryKey, bool IsIndexed);

public class TableSchema(Tables table, List<ColumnSchema> columns)
{
    public Tables Table { get; } = table;
    public List<ColumnSchema> Columns { get; } = columns;
}