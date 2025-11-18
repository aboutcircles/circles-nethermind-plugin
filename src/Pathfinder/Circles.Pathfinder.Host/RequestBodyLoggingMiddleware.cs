using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Circles.Pathfinder.Host;

public class RequestBodyLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestBodyLoggingMiddleware> _logger;

    public RequestBodyLoggingMiddleware(RequestDelegate next, ILogger<RequestBodyLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only log for POST /findPath endpoint and only in debug mode
        if (context.Request.Method == "POST" && 
            context.Request.Path.StartsWithSegments("/findPath") &&
            _logger.IsEnabled(LogLevel.Debug))
        {
            // Enable buffering so we can read the request body multiple times
            context.Request.EnableBuffering();
            
            // Read and log the raw request body
            context.Request.Body.Position = 0;
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync();
            
            _logger.LogDebug($"Raw request body: {rawBody}");
            
            // Reset the position so model binding can read it
            context.Request.Body.Position = 0;
        }

        await _next(context);
    }
}