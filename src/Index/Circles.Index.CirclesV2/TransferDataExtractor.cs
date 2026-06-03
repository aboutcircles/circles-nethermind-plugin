namespace Circles.Index.CirclesV2;

/// <summary>
/// Gate + extraction for the 'data' bytes that ERC-1155 transfers carry in calldata but not in the
/// TransferSingle/TransferBatch event. Kept free of Nethermind types (operates on the parsed event
/// list, raw calldata, and primitives) so it can be unit-tested without a Nethermind host — the
/// LogParser type itself transitively depends on reference-only Nethermind assemblies.
/// </summary>
public static class TransferDataExtractor
{
    /// <summary>
    /// Yields a <see cref="TransferData"/> for each non-empty data blob found in the transaction
    /// calldata, but only when the transaction emitted an ERC-1155 transfer event. The transfer
    /// event gate ensures unrelated calldata with a colliding 4-byte selector is ignored, and the
    /// transfer value is intentionally not consulted — so 0-value transfers carrying data (the
    /// sanctioned way to annotate ERC20/gCRC transfers) are indexed.
    /// </summary>
    /// <param name="events">Events parsed from the transaction's logs; the gate fires only if one is a TransferSingle/TransferBatch.</param>
    /// <param name="calldata">Raw transaction input (may be a Safe/ERC-4337 wrapper — it is unwrapped before parsing).</param>
    /// <param name="blockNumber">Block number stamped onto emitted events.</param>
    /// <param name="timestamp">Block timestamp stamped onto emitted events.</param>
    /// <param name="transactionIndex">Transaction index stamped onto emitted events.</param>
    /// <param name="transactionHash">Transaction hash stamped onto emitted events.</param>
    /// <param name="startingLogIndex">Synthetic log index for the first emitted event; subsequent events decrement from it.</param>
    /// <returns>One <see cref="TransferData"/> per non-empty data blob, or empty if the gate does not fire.</returns>
    public static IEnumerable<TransferData> Extract(
        IReadOnlyList<IIndexedEventV2> events,
        byte[] calldata,
        long blockNumber,
        long timestamp,
        int transactionIndex,
        string transactionHash,
        int startingLogIndex)
    {
        if (calldata.Length <= 4)
            yield break;

        var hasTransferEvents = events.Any(e => e is TransferSingle or TransferBatch);
        if (!hasTransferEvents)
            yield break;

        foreach (var transferData in ParseTransferDataFromCalldata(
                     blockNumber, timestamp, transactionIndex, transactionHash, calldata, startingLogIndex))
        {
            yield return transferData;
        }
    }

    /// <summary>
    /// Extracts TransferData events from transaction calldata.
    /// Handles safeTransferFrom, safeBatchTransferFrom, and operateFlowMatrix calls.
    /// Returns a list because iterators can't use ref parameters.
    /// </summary>
    private static List<TransferData> ParseTransferDataFromCalldata(
        long blockNumber,
        long timestamp,
        int transactionIndex,
        string transactionHash,
        byte[] calldata,
        int startingLogIndex)
    {
        var results = new List<TransferData>();

        try
        {
            // Use CalldataUnwrapper to handle ERC-4337 and Safe wrapper contracts
            // before parsing the inner Hub calldata for transfer data.
            // Note: UnwrapAndParse uses yield return, so exceptions are thrown during
            // enumeration, not during the initial call. The try-catch must wrap the foreach.
            var parsedData = CalldataUnwrapper.UnwrapAndParse(calldata);

            int logIndex = startingLogIndex;
            foreach (var (from, to, data) in parsedData)
            {
                // Skip empty data - transfers are still auditable via TransferSingle/TransferBatch
                if (data.Length == 0)
                    continue;

                results.Add(new TransferData(
                    blockNumber,
                    timestamp,
                    transactionIndex,
                    logIndex--,  // negative index for synthetic events
                    transactionHash,
                    "",  // emitter - empty for calldata-derived events
                    from,
                    to,
                    data
                ));
            }
        }
        catch (Exception)
        {
            // Expected for transactions that have TransferSingle/TransferBatch events but
            // aren't calling Circles Hub (selector collisions, other ERC-1155 contracts).
            // Silently skip - the transfer events are still indexed from logs.
        }

        return results;
    }
}
