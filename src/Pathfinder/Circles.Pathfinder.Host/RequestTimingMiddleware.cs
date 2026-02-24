using System.Diagnostics;
using Prometheus;
using static Circles.Pathfinder.Tracing;

namespace Circles.Pathfinder.Host;

public sealed class RequestTimingMiddleware(RequestDelegate next, ILogger<RequestTimingMiddleware> log)
{
    private static readonly Histogram RequestDuration =
        Metrics.CreateHistogram(
            "circles_http_request_duration_seconds",
            "Total HTTP request duration",
            new HistogramConfiguration
            {
                LabelNames = new[] { "route", "status" },
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

        // Use the path template for low-cardinality labels
        var route = path.HasValue ? path.Value! : "unknown";
        RequestDuration.WithLabels(route, statusCode.ToString()).Observe(sw.Elapsed.TotalSeconds);

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
