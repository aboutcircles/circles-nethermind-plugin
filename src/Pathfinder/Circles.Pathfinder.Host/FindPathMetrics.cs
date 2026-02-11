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
}
