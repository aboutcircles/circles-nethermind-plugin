using System.Diagnostics;
using Prometheus;
using static Circles.Pathfinder.Tracing;

namespace Circles.Pathfinder.Host;

public sealed class RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> log)
{
    // Header an upstream proxy may set to attribute traffic to a logical source. The
    // circles-test-environment session proxy sets it to "testenv" so its (deliberately slow,
    // near-head, block-pinned) time-travel calls can be excluded from the /findPath latency alerts
    // — they share this pathfinder process with real staging traffic and would otherwise page.
    private const string RequestSourceHeader = "X-Request-Source";

    // Only "testenv" is recognised; every other value (and absence) collapses to "default". The
    // header is client-settable on a public endpoint, so this allowlist bounds the `source` label
    // to two series and prevents cardinality abuse.
    private const string DefaultSource = "default";
    private const string TestEnvSource = "testenv";

    private static readonly Histogram RequestDuration =
        Metrics.CreateHistogram(
            "circles_http_request_duration_seconds",
            "Total HTTP request duration",
            new HistogramConfiguration
            {
                LabelNames = new[] { "route", "status", "source" },
                Buckets = Constants.RequestDurationBuckets
            });

    public async Task InvokeAsync(HttpContext ctx)
    {
        var sw = Stopwatch.StartNew();
        await next(ctx);
        sw.Stop();

        var elapsedMs = sw.Elapsed.TotalMilliseconds;
        var statusCode = ctx.Response.StatusCode;
        var method = ctx.Request.Method;
        var path = ctx.Request.Path;

        var traceId = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;

        // Raw request path as the route label. Safe here — the pathfinder's routes are fixed
        // (/findPath, /findMaxFlow, /snapshot, /health, …) with no path parameters, so this stays
        // low-cardinality without needing a route template.
        var route = path.HasValue ? path.Value! : "unknown";
        var source = ctx.Request.Headers.TryGetValue(RequestSourceHeader, out var sourceHeader)
                     && string.Equals(sourceHeader, TestEnvSource, StringComparison.OrdinalIgnoreCase)
            ? TestEnvSource
            : DefaultSource;
        RequestDuration.WithLabels(route, statusCode.ToString(), source).Observe(sw.Elapsed.TotalSeconds);

        using (log.BeginScope("traceId:{TraceId}", traceId))
        {
            log.LogInformation(
                "HTTP {Method} {Path} {Status} in {ElapsedMs:n1} ms",
                method, path, statusCode, elapsedMs);

            LatencyStats.Record(elapsedMs);

            var isSlow = elapsedMs > Constants.SlowRequestThresholdMs;
            if (isSlow)
            {
                log.LogWarning(
                    "SLOW REQUEST — {Method} {Path} took {ElapsedMs:n1} ms",
                    method, path, elapsedMs);
            }
        }
    }
}
