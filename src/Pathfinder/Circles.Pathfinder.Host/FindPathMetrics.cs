using Prometheus;

namespace Circles.Pathfinder.Host;

internal class FindPathMetrics
{
    // A gauge for the number of concurrent /findPath requests in progress.
    public static readonly Gauge InFlightRequestsGauge =
        Metrics.CreateGauge(
            "circles_findpath_inflight_requests",
            "Number of concurrent /findPath requests currently being processed."
        );

    // A counter for the number of times we reject /findPath requests due to concurrency.
    public static readonly Counter RejectedRequestsCounter =
        Metrics.CreateCounter(
            "circles_findpath_rejected_requests_total",
            "Number of /findPath requests rejected due to concurrency limit."
        );

    // O1: Solver outcome counter — labels: status={success, error, timeout, bad_input}
    public static readonly Counter SolverStatusTotal =
        Metrics.CreateCounter(
            "circles_solver_status_total",
            "Solver outcome counts by status",
            new CounterConfiguration { LabelNames = new[] { "status" } });

    // Consent: paths dropped due to consent intermediary exclusion or validation
    public static readonly Counter ConsentPathsDroppedTotal =
        Metrics.CreateCounter(
            "circles_consent_paths_dropped_total",
            "Paths dropped due to consented flow rules (intermediary exclusion or validation)");

    // Path audit: Hub.sol rule violations detected in pathfinder output (should always be 0).
    // The "simulated" label is "true" when the request injected what-if state
    // (simulatedBalances/Trusts/ConsentedAvatars). A simulated source is expected to fail
    // structural rules like AvatarRegistration (the frontend previews paths for hypothetical,
    // not-yet-registered users), so alerting excludes simulated="true" — mirroring the canary's
    // category=simulation exclusion. Real-traffic violations stay simulated="false" and page.
    public static readonly Counter PathAuditViolationsTotal =
        Metrics.CreateCounter(
            "circles_path_audit_violations_total",
            "Hub.sol rule violations detected in pathfinder output",
            new CounterConfiguration { LabelNames = new[] { "rule", "simulated" } });

    // Path audit: counts when the safety net replaced a violating response with the empty path.
    // Should track 1:1 with PathAuditViolationsTotal — divergence indicates a code path that
    // detects but does not block. Counter exists per rule so we can tell which rule triggered;
    // "simulated" mirrors PathAuditViolationsTotal.
    public static readonly Counter PathAuditBlockedTotal =
        Metrics.CreateCounter(
            "circles_path_audit_blocked_total",
            "Pathfinder responses replaced with empty path because audit detected a Hub.sol violation",
            new CounterConfiguration { LabelNames = new[] { "rule", "simulated" } });

    // Canary: validator threw an unexpected exception (non-zero = validator bug, not pathfinder bug)
    public static readonly Counter CanaryValidatorExceptionTotal =
        Metrics.CreateCounter(
            "circles_canary_validator_exception_total",
            "Times HubContractValidator threw an unexpected exception");

    // Path audit: warning-severity violations detected (observe-only, response NOT replaced).
    // Diagnostic counter for rules whose root cause is still under investigation
    // (e.g. HolderBalanceAvailable). Alertable in the same way as PathAuditViolationsTotal,
    // but without an associated PathAuditBlockedTotal increment.
    public static readonly Counter PathAuditWarningsTotal =
        Metrics.CreateCounter(
            "circles_path_audit_warnings_total",
            "Pathfinder warning-severity validator violations (observe-only, never block response)",
            new CounterConfiguration { LabelNames = new[] { "rule" } });
}
