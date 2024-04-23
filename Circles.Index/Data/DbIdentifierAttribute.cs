namespace Circles.Index.Data;

public static class EnumExtensions
{
    public static string GetIdentifier(this Enum enumValue)
    {
        var type = enumValue.GetType();
        var name = Enum.GetName(type, enumValue);
        if (name == null)
        {
            throw new InvalidOperationException($"Enum value {enumValue} (type: {type}) does not have a name.");
        }
        var field = type.GetField(name);
        if (field == null)
        {
            throw new InvalidOperationException($"Enum {type} does not have a field named {name}.");
        }
        var attribute = Attribute.GetCustomAttribute(field, typeof(DbIdentifierAttribute)) as DbIdentifierAttribute;
        var dbName = attribute?.Identifier ??
                     throw new InvalidOperationException(
                         $"Enum value {enumValue} does not have a DbIdentifierAttribute.");
        
        // Escape reserved keywords
        // TODO: Replace with a more robust solution
        if (dbName.ToLower() == "limit")
        {
            return "\"limit\"";
        }

        return dbName;
    }
}

[AttributeUsage(AttributeTargets.Field)]
public class DbIdentifierAttribute(string identifier) : Attribute
{
    public string Identifier { get; } = identifier;
}