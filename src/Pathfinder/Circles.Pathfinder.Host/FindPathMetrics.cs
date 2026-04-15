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

    // Consent: safety net triggered — gap indicator (should always be 0)
    public static readonly Counter ConsentSafetyNetTriggeredTotal =
        Metrics.CreateCounter(
            "circles_consent_safetynet_triggered_total",
            "Times ValidateConsentedFlow safety net removed edges (indicates path-level filter gap)");

    // Path audit: Hub.sol rule violations detected in pathfinder output (should always be 0)
    public static readonly Counter PathAuditViolationsTotal =
        Metrics.CreateCounter(
            "circles_path_audit_violations_total",
            "Hub.sol rule violations detected in pathfinder output (non-blocking, alert-only)",
            new CounterConfiguration { LabelNames = new[] { "rule" } });

    // Canary: validator threw an unexpected exception (non-zero = validator bug, not pathfinder bug)
    public static readonly Counter CanaryValidatorExceptionTotal =
        Metrics.CreateCounter(
            "circles_canary_validator_exception_total",
            "Times HubContractValidator threw an unexpected exception");
}
