namespace Circles.Index.Common;

public record DatabaseQueryResult(string[] Columns, IEnumerable<object[]> Rows);