namespace Circles.Index.Common;

public interface IDatabaseSchema
{
    public IDictionary<string, EventSchema> Tables { get; }
}