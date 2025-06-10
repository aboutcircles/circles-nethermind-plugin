using System.Reflection;

static class SqlLoader
{
    // Looks for “…Queries.<fileName>” in the current assembly
    internal static string Load(string fileName)
    {
        Assembly asm = typeof(SqlLoader).Assembly;

        string? resourceName = asm
            .GetManifestResourceNames()
            .FirstOrDefault(r =>
                r.EndsWith($".Queries.{fileName}", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            throw new InvalidOperationException($"Embedded resource '{fileName}' not found.");
        }

        using Stream stream = asm.GetManifestResourceStream(resourceName)!;
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}