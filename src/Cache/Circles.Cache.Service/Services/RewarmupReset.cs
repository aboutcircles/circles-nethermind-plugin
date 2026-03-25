namespace Circles.Cache.Service.Services;

internal static class RewarmupReset
{
    /// <summary>
    /// Puts the service into a single canonical "needs re-warmup" state.
    /// The supplied callback must clear caches/ring-buffer for the caller.
    /// </summary>
    /// <remarks>
    /// Callers from NotificationListenerService must hold _notificationGate before
    /// calling this method to prevent TOCTOU races (handler reads WarmupComplete=true,
    /// then Trigger sets it false and clears caches while handler mutates them).
    /// The semaphore in HandleNotificationAsync ensures this invariant.
    /// </remarks>
    public static void Trigger(CacheServiceState state, Action clearCaches)
    {
        state.WarmupComplete = false;
        state.LastProcessedBlock = -1;
        state.CurrentBlockTimestamp = 0;

        // Always clear ring buffer as part of the canonical reset state,
        // independent of caller-specific cache clearing details.
        state.BlockRingBuffer.Clear();

        clearCaches();
    }
}
