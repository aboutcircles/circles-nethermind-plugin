namespace Circles.Index.Common;

public interface IDatabaseSchema
{
    public IDictionary<Tables, TableSchema> Tables { get; }
}