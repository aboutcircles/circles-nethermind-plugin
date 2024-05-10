namespace Circles.Index.Common;

public interface IDatabaseSchema
{
    public ISchemaPropertyMap SchemaPropertyMap { get; }

    public IEventDtoTableMap EventDtoTableMap { get; }
    
    public IDictionary<(string Namespace, string Table), EventSchema> Tables { get; }
}