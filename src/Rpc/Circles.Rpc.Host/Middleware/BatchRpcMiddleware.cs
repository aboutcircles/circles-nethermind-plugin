using System.Diagnostics;
using System.Text.Json;
using Circles.Rpc.Host.Dispatch;

namespace Circles.Rpc.Host.Middleware;

/// <summary>
/// Routes <c>POST /</c> JSON-RPC <em>arrays</em> through a batch handler:
/// circles_*/circlesV2_* dispatched locally one-at-a-time under the shared concurrency
/// semaphore, eth_*/net_*/web3_* forwarded in a single Nethermind sub-batch and
/// re-correlated by request id. Single (non-array) requests fall through to the next middleware.
/// </summary>
public static class BatchRpcMiddleware
{
    private const int MaxBatchBodySize = 1_048_576; // 1 MB
    private const int MaxBatchSize = 50; // Max items per batch

    public static WebApplication UseBatchJsonRpc(this WebApplication app)
    {
        var rpcSemaphore = app.Services.GetRequiredService<SemaphoreSlim>();
        var rpcRateLimiter = app.Services.GetRequiredService<RpcRateLimiter>();

        var jsonArrayStart = "["u8.ToArray();
        var jsonArraySep = ","u8.ToArray();
        var jsonArrayEnd = "]"u8.ToArray();
        // Match the case-insensitive options configured in BuilderSetup.cs for MapPost deserialization.
        // CamelCase naming ensures batch responses use lowercase property names (jsonrpc, result, error)
        // consistent with Nethermind's responses and the JSON-RPC 2.0 convention.
        var batchJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        app.Use(async (context, next) =>
        {
            if (context.Request.Method == "POST" && context.Request.Path == "/")
            {
                var contentType = context.Request.ContentType;
                if (contentType == null || !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

                context.Request.EnableBuffering();
                var buf = new byte[1];
                var bytesRead = await context.Request.Body.ReadAsync(buf, 0, 1);
                context.Request.Body.Position = 0;
                var firstByte = bytesRead > 0 ? buf[0] : -1;

                if (firstByte == '[') // JSON array = batch request
                {
                    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                    var nethermindClient = context.RequestServices.GetRequiredService<NethermindRpcClient>();
                    var rpcModule = context.RequestServices.GetRequiredService<CirclesRpcModule>();
                    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    var startTimestamp = Stopwatch.GetTimestamp();

                    // Enforce body size limit
                    if (context.Request.ContentLength > MaxBatchBodySize)
                    {
                        context.Response.StatusCode = 413;
                        context.Response.ContentType = "application/json";
                        await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                        {
                            Error = new JsonRpcError { Code = -32600, Message = $"Batch request too large (max {MaxBatchBodySize / 1024}KB)" }
                        }, batchJsonOptions);
                        return;
                    }

                    try
                    {
                        using var ms = new MemoryStream();
                        await context.Request.Body.CopyToAsync(ms);

                        if (ms.Length > MaxBatchBodySize)
                        {
                            context.Response.StatusCode = 413;
                            context.Response.ContentType = "application/json";
                            await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                            {
                                Error = new JsonRpcError { Code = -32600, Message = $"Batch request too large (max {MaxBatchBodySize / 1024}KB)" }
                            }, batchJsonOptions);
                            return;
                        }

                        ms.Position = 0;
                        using var doc = await JsonDocument.ParseAsync(ms);
                        var batchArray = doc.RootElement;

                        if (batchArray.ValueKind != JsonValueKind.Array)
                        {
                            await next();
                            return;
                        }

                        var batchLen = batchArray.GetArrayLength();

                        // Enforce batch size limit
                        if (batchLen > MaxBatchSize)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.ContentType = "application/json";
                            await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                            {
                                Error = new JsonRpcError { Code = -32600, Message = $"Batch too large: {batchLen} items (max {MaxBatchSize})" }
                            }, batchJsonOptions);
                            return;
                        }

                        // JSON-RPC 2.0 spec: empty array is invalid ("at least one value" required)
                        if (batchLen == 0)
                        {
                            context.Response.StatusCode = 400;
                            context.Response.ContentType = "application/json";
                            await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                            {
                                Error = JsonRpcError.InvalidRequest("empty batch")
                            }, batchJsonOptions);
                            return;
                        }

                        // Per-IP rate limit: batch costs N tokens (one per item).
                        // Checked before any processing to fail fast on abusive callers.
                        if (!rpcRateLimiter.TryAcquire(remoteIp, batchLen))
                        {
                            RpcMetrics.RateLimitedTotal.Inc();
                            logger.LogWarning("Rate limited batch ({BatchSize} items) from {RemoteIp}", batchLen, remoteIp);
                            context.Response.StatusCode = 429;
                            context.Response.ContentType = "application/json";
                            await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                            {
                                Error = JsonRpcError.RateLimited()
                            }, batchJsonOptions);
                            return;
                        }

                        RpcMetrics.BatchTotal.Inc();
                        RpcMetrics.BatchSize.Observe(batchLen);
                        logger.LogInformation("Batch JSON-RPC ({BatchSize} items) from {RemoteIp}", batchLen, remoteIp);

                        // Classify each request
                        var responses = new object?[batchLen];
                        var circlesItems = new List<(int Index, JsonRpcRequest Request)>();
                        var nethermindItems = new List<(int Index, JsonElement Raw)>();

                        int idx = 0;
                        foreach (var element in batchArray.EnumerateArray())
                        {
                            var method = element.TryGetProperty("method", out var methodProp)
                                ? methodProp.GetString() : null;
                            var id = element.TryGetProperty("id", out var idProp) ? idProp : JsonRpcId.Null;
                            var jsonrpc = element.TryGetProperty("jsonrpc", out var jsonrpcProp)
                                ? jsonrpcProp.GetString() : null;

                            if (jsonrpc != "2.0" || string.IsNullOrEmpty(method))
                            {
                                responses[idx] = new JsonRpcErrorResponse
                                {
                                    Id = id,
                                    Error = JsonRpcError.InvalidRequest()
                                };
                            }
                            else if (RpcDispatcher.IsCirclesMethod(method))
                            {
                                try
                                {
                                    var req = JsonSerializer.Deserialize<JsonRpcRequest>(
                                        element.GetRawText(), batchJsonOptions);
                                    if (req != null) circlesItems.Add((idx, req));
                                    else responses[idx] = new JsonRpcErrorResponse
                                    {
                                        Id = id,
                                        Error = JsonRpcError.InvalidRequest()
                                    };
                                }
                                catch (JsonException)
                                {
                                    responses[idx] = new JsonRpcErrorResponse
                                    {
                                        Id = id,
                                        Error = JsonRpcError.InvalidRequest("malformed item")
                                    };
                                }
                            }
                            else if (RpcDispatcher.IsProxyAllowed(method))
                            {
                                nethermindItems.Add((idx, element.Clone()));
                            }
                            else
                            {
                                responses[idx] = new JsonRpcErrorResponse
                                {
                                    Id = id,
                                    Error = JsonRpcError.MethodNotFound(method)
                                };
                            }
                            idx++;
                        }

                        // Process circles items sequentially with semaphore
                        var circlesTask = Task.Run(async () =>
                        {
                            foreach (var (i, req) in circlesItems)
                            {
                                if (!await rpcSemaphore.WaitAsync(0))
                                {
                                    RpcMetrics.RejectedTotal.Inc();
                                    responses[i] = new JsonRpcErrorResponse
                                    {
                                        Id = JsonRpcId.CoerceId(req.Id),
                                        Error = JsonRpcError.ServerBusy()
                                    };
                                    continue;
                                }
                                try
                                {
                                    responses[i] = await RpcDispatcher.DispatchSingleRequest(
                                        req, rpcModule, nethermindClient, logger, remoteIp);
                                }
                                finally
                                {
                                    rpcSemaphore.Release();
                                }
                            }
                        });

                        // Proxy Nethermind items in a single batch call.
                        // JSON-RPC 2.0 batch responses are unordered — correlate by id, not position.
                        var nethermindTask = Task.Run(async () =>
                        {
                            if (nethermindItems.Count == 0) return;

                            try
                            {
                                var subBatch = nethermindItems.Select(x => x.Raw).ToArray();
                                var subBatchBytes = JsonSerializer.SerializeToUtf8Bytes(subBatch);
                                var result = await nethermindClient.ForwardRawRequest(subBatchBytes);

                                if (result.ValueKind == JsonValueKind.Array)
                                {
                                    // Build id→response lookup from Nethermind's response array.
                                    // JSON-RPC ids can be string, number, or null — use raw JSON text as key.
                                    var responseById = new Dictionary<string, JsonElement>();
                                    foreach (var resp in result.EnumerateArray())
                                    {
                                        var idKey = resp.TryGetProperty("id", out var respId)
                                            ? respId.GetRawText() : "null";
                                        responseById.TryAdd(idKey, resp); // first wins on duplicate ids
                                    }

                                    // Match each sent request to its response by id
                                    foreach (var (ni, raw) in nethermindItems)
                                    {
                                        var reqIdKey = raw.TryGetProperty("id", out var reqId)
                                            ? reqId.GetRawText() : "null";

                                        if (responseById.TryGetValue(reqIdKey, out var matched))
                                            responses[ni] = matched;
                                        else
                                        {
                                            // No response for this id — notification (no id) or dropped
                                            var fallbackId = raw.TryGetProperty("id", out var fbId)
                                                ? fbId.Clone() : JsonRpcId.Null;
                                            responses[ni] = new JsonRpcErrorResponse
                                            {
                                                Id = fallbackId,
                                                Error = JsonRpcError.Internal("Missing proxy response")
                                            };
                                        }
                                    }
                                }
                                else
                                {
                                    // Nethermind returned a non-array (single error object)
                                    logger.LogWarning("Nethermind returned non-array for batch of {Count} items", nethermindItems.Count);
                                    foreach (var (ni, raw) in nethermindItems)
                                    {
                                        var errId = raw.TryGetProperty("id", out var errIdProp) ? errIdProp.Clone() : JsonRpcId.Null;
                                        responses[ni] = new JsonRpcErrorResponse
                                        {
                                            Id = errId,
                                            Error = JsonRpcError.Internal("Unexpected proxy response")
                                        };
                                    }
                                }
                            }
                            catch (Exception proxyEx)
                            {
                                logger.LogError(proxyEx, "Failed to proxy Nethermind batch ({Count} items) from {RemoteIp}",
                                    nethermindItems.Count, remoteIp);
                                foreach (var (i, raw) in nethermindItems)
                                {
                                    var id = raw.TryGetProperty("id", out var idProp) ? idProp.Clone() : JsonRpcId.Null;
                                    responses[i] = new JsonRpcErrorResponse
                                    {
                                        Id = id,
                                        Error = JsonRpcError.Internal("Proxy error")
                                    };
                                }
                            }
                        });

                        await Task.WhenAll(circlesTask, nethermindTask);

                        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                        logger.LogInformation(
                            "Batch JSON-RPC completed in {ElapsedMs} ms ({CirclesCount} circles, {NethermindCount} proxied) from {RemoteIp}",
                            elapsed.TotalMilliseconds, circlesItems.Count, nethermindItems.Count, remoteIp);

                        // Sweep for null slots (shouldn't happen, but safety net)
                        for (int k = 0; k < responses.Length; k++)
                        {
                            responses[k] ??= new JsonRpcErrorResponse
                            {
                                Error = JsonRpcError.Internal("Internal error: no response generated")
                            };
                        }

                        // Buffer once, flush once. Each item serialized by runtime type — System.Text.Json
                        // on object[] uses declared type and produces empty {} for JsonRpcResponse/JsonRpcErrorResponse.
                        context.Response.ContentType = "application/json";
                        using var responseBuffer = new MemoryStream();
                        responseBuffer.Write(jsonArrayStart);
                        for (int k = 0; k < responses.Length; k++)
                        {
                            if (k > 0) responseBuffer.Write(jsonArraySep);
                            var item = responses[k]!;
                            if (item is JsonElement je)
                                JsonSerializer.Serialize(responseBuffer, je);
                            else
                                JsonSerializer.Serialize(responseBuffer, item, item.GetType(), batchJsonOptions);
                        }
                        responseBuffer.Write(jsonArrayEnd);
                        responseBuffer.Position = 0;
                        await responseBuffer.CopyToAsync(context.Response.Body);
                    }
                    catch (JsonException jsonEx)
                    {
                        logger.LogWarning(jsonEx, "Malformed batch JSON-RPC request from {RemoteIp}", remoteIp);
                        RpcMetrics.ErrorsTotal.WithLabels("batch", "parse_error").Inc();
                        context.Response.StatusCode = 400;
                        context.Response.ContentType = "application/json";
                        await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                        {
                            Error = JsonRpcError.ParseError()
                        }, batchJsonOptions);
                    }
                    catch (Exception ex)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                        RpcMetrics.ErrorsTotal.WithLabels("batch", "internal_error").Inc();
                        logger.LogError(ex, "Batch handler error from {RemoteIp} after {ElapsedMs} ms",
                            remoteIp, elapsed.TotalMilliseconds);

                        context.Response.StatusCode = 500;
                        context.Response.ContentType = "application/json";
                        await JsonSerializer.SerializeAsync(context.Response.Body, new JsonRpcErrorResponse
                        {
                            Error = JsonRpcError.Internal("Internal batch error")
                        }, batchJsonOptions);
                    }
                    return;
                }
            }

            await next();
        });

        return app;
    }
}
