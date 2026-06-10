using System.Diagnostics;

namespace Circles.Rpc.Host.Middleware;

public static class HistoricalSessionTaggingMiddleware
{
    public static WebApplication UseHistoricalSessionTagging(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey(CirclesRpcModule.MaxBlockNumberHeader))
                Activity.Current?.SetTag("session.historical", "true");
            await next();
        });
        return app;
    }
}
