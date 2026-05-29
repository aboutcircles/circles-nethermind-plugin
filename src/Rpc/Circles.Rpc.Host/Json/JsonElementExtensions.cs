using System.Text.Json;

// Originally defined inside Program.cs (global namespace). Kept in the global namespace
// here to preserve the type's full name and any reflection-based access. The class is
// currently unused but is part of the public surface, so removal is out of scope for
// this pure-split PR.
public static class JsonElementExtensions
{
    public static bool IsNullOrUndefined(this JsonElement element)
    {
        return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    }
}
