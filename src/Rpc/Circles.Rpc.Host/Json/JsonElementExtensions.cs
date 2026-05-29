using System.Text.Json;

// Kept in the global namespace to preserve the type's fully-qualified name for any
// reflection-based consumers.
public static class JsonElementExtensions
{
    public static bool IsNullOrUndefined(this JsonElement element)
    {
        return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    }
}
