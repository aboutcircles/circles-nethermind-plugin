namespace Circles.Index.CirclesV2.MultiAffiliateGroupRegistry;

/// <summary>
/// Detects whether a transaction's calldata is a call to the registry's deployer-only
/// <c>initialize(address[],address[])</c> bulk-seed.
///
/// Kept Nethermind-free (operates only on <see cref="ReadOnlyMemory{T}"/>) so it is unit-testable —
/// the plugin references Nethermind as reference-only assemblies, so any type that touches Nethermind
/// runtime types (like the LogParser) cannot be loaded in a test. The LogParser delegates here.
/// </summary>
public static class AffiliateGroupSeedDetector
{
    // initialize(address[],address[]) selector — verified with `cast sig`. The deployer is an EOA
    // calling the (non-proxy, constructor-deployed) registry directly, so a seed-emitted Added rides a
    // tx whose first 4 calldata bytes are this selector; addAffiliateGroup() (or a Safe-wrapped call)
    // carries a different selector and is therefore not misclassified as a seed.
    private static readonly byte[] InitializeSelector = [0x73, 0xcf, 0x25, 0xf8];

    public static bool IsInitializeCalldata(ReadOnlyMemory<byte> calldata) =>
        calldata.Length >= 4 && calldata.Span[..4].SequenceEqual(InitializeSelector);
}
