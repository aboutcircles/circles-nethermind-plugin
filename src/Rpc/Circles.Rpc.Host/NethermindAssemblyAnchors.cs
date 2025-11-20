using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NethermindLogging = Nethermind.Logging;

namespace Circles.Rpc.Host;

/// <summary>
/// References Nethermind assemblies explicitly so the runtime carries them even
/// though they are only used transitively by the Circles index packages.
/// </summary>
internal static class NethermindAssemblyAnchors
{
    private static readonly string[] RequiredAssemblies =
    [
        "Nethermind.Core",
        "Nethermind.Logging"
    ];

    private static bool _initialized;

    public static void EnsureLoaded()
    {
        if (_initialized)
        {
            return;
        }

        foreach (var assemblyName in RequiredAssemblies)
        {
            LoadAssemblyIfMissing(assemblyName);
        }

        // The typeof expressions keep the assembly references alive without pulling any runtime cost.
        _ = typeof(Address);
        _ = typeof(Hash256);
        _ = typeof(NethermindLogging.ILogger);

        _initialized = true;
    }

    private static void LoadAssemblyIfMissing(string assemblyName)
    {
        if (AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidateDirectories = BuildCandidateDirectories(baseDirectory);

        foreach (var directory in candidateDirectories)
        {
            var candidatePath = Path.Combine(directory, $"{assemblyName}.dll");
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath);
            return;
        }

        var searchedLocations = string.Join(", ", candidateDirectories);
        throw new FileNotFoundException(
            $"Unable to locate {assemblyName}.dll next to the RPC host output. " +
            "Ensure the nethermind submodule has been built (see DEVELOPMENT.md). " +
            $"Searched: {searchedLocations}.",
            $"{assemblyName}.dll");
    }

    private static IReadOnlyCollection<string> BuildCandidateDirectories(string baseDirectory)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            baseDirectory
        };

        // dotnet may place runtime-specific builds in nested RID folders (osx-arm64, linux-x64, ...)
        foreach (var rid in new[] { "osx-arm64", "linux-arm64", "linux-x64", "win-x64", "win-arm64" })
        {
            var ridPath = Path.Combine(baseDirectory, rid);
            if (Directory.Exists(ridPath))
            {
                directories.Add(ridPath);
            }
        }

        // When running directly from the project directory, the base directory may be the RID folder itself.
        // In that case, probe the parent folder so we also cover manual copies.
        var parent = Directory.GetParent(baseDirectory)?.FullName;
        if (!string.IsNullOrEmpty(parent))
        {
            directories.Add(parent);
        }

        return directories;
    }
}
