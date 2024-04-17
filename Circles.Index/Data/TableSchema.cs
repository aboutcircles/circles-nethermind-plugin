using Circles.Index.Data.Query;

namespace Circles.Index.Data;

public class TableSchema(Tables table, List<(Columns Column, ValueTypes Type, bool IsPrimaryKey, bool IsIndexed)> columns)
{
    public Tables Table { get; } = table;
    public List<(Columns Column, ValueTypes Type, bool IsPrimaryKey, bool IsIndexed)> Columns { get; } = columns;
}