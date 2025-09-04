using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

// Circles.Index.ApiTester - Nethermind RPC API smoke tester for Circles RPC module
// Usage:
//   Circles.Index.ApiTester --http=https://rpc.circlesubi.network --ws=wss://https://rpc.circlesubi.network
// If only --http is provided, tests run over HTTP only. If --ws is provided too, the suite runs over WS as well.
// The tester calls every method exposed by ICirclesRpcModule and reports whether a JSON-RPC response (result or error) is received.

var argsDict = ParseArgs(args);
var httpUrl = argsDict.TryGetValue("http", out var h) ? h : null;
var wsUrl = argsDict.TryGetValue("ws", out var w) ? w : null;

if (string.IsNullOrWhiteSpace(httpUrl) && string.IsNullOrWhiteSpace(wsUrl))
{
    Console.WriteLine("Circles.Index.ApiTester - Smoke Test");
    Console.WriteLine("Provide --http and optionally --ws URLs.");
    Console.WriteLine(
        "Example: Circles.Index.ApiTester --http=https://rpc.circlesubi.network --ws=wss://https://rpc.circlesubi.network");
    return;
}

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false
};

var httpClient = new HttpClient();

// Context data gathered during tests to make some calls more realistic
var zero = "0x0000000000000000000000000000000000000000";
var exampleAddr1 = "0x42cedde51198d1773590311e2a340dc06b24cb37";
var exampleAddr2 = "0xde374ece6fa50e781e81aac78e811b33d16912c7";
string? exampleProfileCid = "QmQcMUpvF1ZxDWsrLmcqYP9AThkPbdBKJhGhkRFo9M1suf";
(string Namespace, string Table)? exampleTable = null;

var results = new List<(string Transport, string Method, bool Ok, string? Note)>();


static string Colorize(string text, string color)
{
    var hasText = !string.IsNullOrEmpty(text);
    if (!hasText)
    {
        return text;
    }

    return $"{color}{text}{Ansi.Reset}";
}

async Task<(bool Ok, string? Note, JsonDocument? Doc)> SendHttpAsync(string method, params object?[] parameters)
{
    if (string.IsNullOrWhiteSpace(httpUrl)) return (false, "HTTP URL not provided", null);
    var req = new RpcRequest(method, parameters);
    using var content =
        new StringContent(JsonSerializer.Serialize(req, jsonOptions), Encoding.UTF8, "application/json");
    try
    {
        using var resp = await httpClient.PostAsync(httpUrl, content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            return (false, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}", null);
        }

        var doc = JsonDocument.Parse(text);
        return (true, Classify(doc), doc);
    }
    catch (Exception ex)
    {
        return (false, ex.Message, null);
    }
}

async Task<(bool Ok, string? Note, JsonDocument? Doc)> SendWsAsync(ClientWebSocket ws, string method,
    params object?[] parameters)
{
    var req = new RpcRequest(method, parameters);
    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(req, jsonOptions));

    try
    {
        await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None);
    }
    catch (Exception ex)
    {
        var note = $"WS send failed: {ex.Message}";
        return (false, note, null);
    }

    var buffer = new ArraySegment<byte>(new byte[1 << 16]);
    using var ms = new MemoryStream();

    try
    {
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);

            // If we get a close frame, stop reading and report a transport-level failure.
            var isClose = result.MessageType == WebSocketMessageType.Close;
            if (isClose)
            {
                var note = "WS receive: server closed connection";
                return (false, note, null);
            }

            ms.Write(buffer.Array!, buffer.Offset, result.Count);

            var isEndOfMessage = result.EndOfMessage;
            if (isEndOfMessage)
            {
                break;
            }
        }
    }
    catch (Exception ex)
    {
        var note = $"WS receive failed: {ex.Message}";
        return (false, note, null);
    }

    var txt = Encoding.UTF8.GetString(ms.ToArray());
    var isBlank = string.IsNullOrWhiteSpace(txt);
    if (isBlank) return (false, "Empty WS response", null);

    try
    {
        var doc = JsonDocument.Parse(txt);
        return (true, Classify(doc), doc);
    }
    catch (Exception ex)
    {
        var rawHead = txt[..Math.Min(200, txt.Length)];
        var note = ex.Message + $" Raw: {rawHead}";
        return (false, note, null);
    }
}

string Classify(JsonDocument doc)
{
    var root = doc.RootElement;

    var hasError = root.TryGetProperty("error", out _);
    if (hasError)
    {
        return "error";
    }

    var hasResult = root.TryGetProperty("result", out _);
    if (hasResult)
    {
        return "result";
    }

    return "unknown";
}


static string ToPrettyJson(JsonElement element)
{
    using var stream = new MemoryStream();
    var writerOptions = new JsonWriterOptions { Indented = true };
    using (var writer = new Utf8JsonWriter(stream, writerOptions))
    {
        element.WriteTo(writer);
    }

    var json = Encoding.UTF8.GetString(stream.ToArray());
    return json;
}

static string WrapAndTruncate(string text, int maxLines = 7, int wrapWidth = 72)
{
    var wrappedLines = new List<string>();
    foreach (var line in text.Split('\n'))
    {
        if (line.Length <= wrapWidth)
        {
            wrappedLines.Add(line);
        }
        else
        {
            for (int i = 0; i < line.Length; i += wrapWidth)
            {
                var chunk = line.Substring(i, Math.Min(wrapWidth, line.Length - i));
                wrappedLines.Add(chunk);
            }
        }
    }

    if (wrappedLines.Count > maxLines)
    {
        return string.Join('\n', wrappedLines.Take(maxLines)) + "\n… (truncated)";
    }

    return string.Join('\n', wrappedLines);
}

static string ExtractPayloadPreview(JsonDocument doc, int maxLines = 7, int wrapWidth = 72)
{
    var root = doc.RootElement;
    JsonElement? element = null;

    if (root.TryGetProperty("result", out var resultEl))
    {
        element = resultEl;
    }
    else if (root.TryGetProperty("error", out var errorEl))
    {
        element = errorEl;
    }

    if (element == null)
    {
        return WrapAndTruncate(root.GetRawText(), maxLines, wrapWidth);
    }

    var json = ToPrettyJson(element.Value);
    return WrapAndTruncate(json, maxLines, wrapWidth);
}

async Task TryHttp(string method, params object?[] parameters)
{
    var (transportOk, note, doc) = await SendHttpAsync(method, parameters);

    var success = transportOk && string.Equals(note, "result", StringComparison.OrdinalIgnoreCase);
    results.Add(("HTTP", method, success, note));

    var statusText = success ? "✔ OK" : "✖ FAIL";
    var statusColored = success ? Colorize(statusText, Ansi.Green) : Colorize(statusText, Ansi.Red);
    var noteColored = Colorize($"({note})", Ansi.Gray);

    Console.WriteLine($"{Colorize("[HTTP]", Ansi.Cyan)} {Colorize(method, Ansi.Bold)}: {statusColored} {noteColored}");

    if (doc != null)
    {
        var payload = ExtractPayloadPreview(doc!);
        var isError = string.Equals(note, "error", StringComparison.OrdinalIgnoreCase);
        var header = isError
            ? Colorize("[HTTP] → error payload:", Ansi.Yellow)
            : Colorize("[HTTP] → result/other payload:", Ansi.Gray);
        Console.WriteLine(header);
        Console.WriteLine(payload);
    }
}

async Task<(bool Ok, string? Note, JsonDocument? Doc)> TryHttpCapture(string method, params object?[] parameters)
{
    var res = await SendHttpAsync(method, parameters);

    var success = res.Ok && string.Equals(res.Note, "result", StringComparison.OrdinalIgnoreCase);
    results.Add(("HTTP", method, success, res.Note));

    var statusText = success ? "✔ OK" : "✖ FAIL";
    var statusColored = success ? Colorize(statusText, Ansi.Green) : Colorize(statusText, Ansi.Red);
    var noteColored = Colorize($"({res.Note})", Ansi.Gray);

    Console.WriteLine($"{Colorize("[HTTP]", Ansi.Cyan)} {Colorize(method, Ansi.Bold)}: {statusColored} {noteColored}");

    if (res.Doc != null)
    {
        var payload = ExtractPayloadPreview(res.Doc!);
        var isError = string.Equals(res.Note, "error", StringComparison.OrdinalIgnoreCase);
        var header = isError
            ? Colorize("[HTTP] → error payload:", Ansi.Yellow)
            : Colorize("[HTTP] → result/other payload:", Ansi.Gray);
        Console.WriteLine(header);
        Console.WriteLine(payload);
    }

    return res;
}


async Task TryWs(ClientWebSocket ws, string method, params object?[] parameters)
{
    var (transportOk, note, doc) = await SendWsAsync(ws, method, parameters);

    var success = transportOk && string.Equals(note, "result", StringComparison.OrdinalIgnoreCase);
    results.Add(("WS", method, success, note));

    var statusText = success ? "✔ OK" : "✖ FAIL";
    var statusColored = success ? Colorize(statusText, Ansi.Green) : Colorize(statusText, Ansi.Red);
    var noteColored = Colorize($"({note})", Ansi.Gray);

    Console.WriteLine($"{Colorize("[WS]", Ansi.Magenta)} {Colorize(method, Ansi.Bold)}: {statusColored} {noteColored}");

    if (doc != null)
    {
        var payload = ExtractPayloadPreview(doc!);
        var isError = string.Equals(note, "error", StringComparison.OrdinalIgnoreCase);
        var header = isError
            ? Colorize("[WS] → error payload:", Ansi.Yellow)
            : Colorize("[WS] → result/other payload:", Ansi.Gray);
        Console.WriteLine(header);
        Console.WriteLine(payload);
    }
}


async Task RunOverHttp()
{
    if (string.IsNullOrWhiteSpace(httpUrl)) return;
    Console.WriteLine($"[HTTP] Smoke testing Circles RPC at {httpUrl}");

    // 1) circles_health
    await TryHttp("circles_health");

    // 2) circles_tables (capture a table for later query)
    var tablesResp = await TryHttpCapture("circles_tables");
    if (tablesResp.Doc != null)
    {
        try
        {
            var root = tablesResp.Doc.RootElement;
            var res = root.GetProperty("result");
            if (res.ValueKind == JsonValueKind.Array && res.GetArrayLength() > 0)
            {
                var ns = res[0];
                var nsName = ns.GetProperty("namespace").GetString();
                var tables = ns.GetProperty("tables");
                if (tables.ValueKind == JsonValueKind.Array && tables.GetArrayLength() > 0)
                {
                    exampleTable = (nsName ?? "", tables[0].GetProperty("table").GetString() ?? "");
                }
            }
        }
        catch
        {
            /* ignore */
        }
    }

    // 3) Basic address-dependent calls
    await TryHttp("circles_getTotalBalance", exampleAddr1, true);
    await TryHttp("circlesV2_getTotalBalance", exampleAddr1, true);
    await TryHttp("circles_getTrustRelations", exampleAddr1);
    await TryHttp("circles_getTokenBalances", exampleAddr1);
    await TryHttp("circles_getCommonTrust", exampleAddr1, exampleAddr2);

    // 4) events and snapshot
    await TryHttp("circles_events", null, 40000000, null, new[] { "CrcV1_Trust" }, null, false);
    await TryHttp("circles_getNetworkSnapshot");

    // 5) pathfinder minimal
    var flowRequest = new
    {
        source = exampleAddr1,
        sink = exampleAddr2,
        targetFlow = "1000000"
    };
    await TryHttp("circlesV2_findPath", flowRequest);

    // 6) profiles and avatars
    var cidResp = await TryHttpCapture("circles_getProfileCid", exampleAddr1);
    if (cidResp.Doc != null)
    {
        try
        {
            exampleProfileCid = cidResp.Doc.RootElement.GetProperty("result").GetString();
        }
        catch
        {
        }
    }

    await TryHttp("circles_getProfileCidBatch", new[] { new[] { exampleAddr1, exampleAddr2 } });

    await TryHttp("circles_getAvatarInfo", exampleAddr1);
    await TryHttp("circles_getAvatarInfoBatch", new[] { new[] { exampleAddr1, exampleAddr2 } });

    await TryHttp("circles_getProfileByCid", exampleProfileCid!);
    await TryHttp("circles_getProfileByCidBatch", new[] { new[] { exampleProfileCid! } });

    await TryHttp("circles_getProfileByAddress", exampleAddr1);
    await TryHttp("circles_getProfileByAddressBatch", new[] { new[] { exampleAddr1, exampleAddr2 } });

    // 7) token info
    await TryHttp("circles_getTokenInfo", exampleAddr1);
    await TryHttp("circles_getTokenInfoBatch", new[] { new[] { exampleAddr1, exampleAddr2 } });

    // 8) search profiles (free text)
    await TryHttp("circles_searchProfiles", "jaensen", 1, 0);

    // 9) query using discovered table if any
    if (exampleTable is not null)
    {
        var (ns, table) = exampleTable.Value;
        var selectDto = new
        {
            @namespace = ns,
            table,
            limit = 1
        };
        await TryHttp("circles_query", selectDto);
    }
    else
    {
        // Attempt a minimal query; likely to error, but still exercise the endpoint
        var selectDto = new { limit = 1 };
        await TryHttp("circles_query", selectDto);
    }
}

async Task RunOverWs()
{
    var wsUrlIsBlank = string.IsNullOrWhiteSpace(wsUrl);
    if (wsUrlIsBlank) return;

    Console.WriteLine($"[WS] Smoke testing Circles RPC at {wsUrl}");

    using var ws = new ClientWebSocket();

    try
    {
        await ws.ConnectAsync(new Uri(wsUrl!), CancellationToken.None);
    }
    catch (Exception ex)
    {
        var msg = $"WS connect failed: {ex.Message}";
        Console.WriteLine(Colorize($"[WS] {msg}", Ansi.Red));
        results.Add(("WS", "<connect>", false, msg));
        return;
    }

    // We'll run a subset first to verify connection, then the full set
    await TryWs(ws, "circles_health");

    // Run the same sequence as HTTP; we won't re-capture dynamic data to keep it simple
    await TryWs(ws, "circles_tables");
    await TryWs(ws, "circles_getTotalBalance", exampleAddr1, true);
    await TryWs(ws, "circlesV2_getTotalBalance", exampleAddr1, true);
    await TryWs(ws, "circles_getTrustRelations", exampleAddr1);
    await TryWs(ws, "circles_getTokenBalances", exampleAddr1);
    await TryWs(ws, "circles_getCommonTrust", exampleAddr1, exampleAddr2);

    await TryWs(ws, "circles_events", null, 0, 100, null, null, false);
    await TryWs(ws, "circles_getNetworkSnapshot");

    var flowRequest = new { source = exampleAddr1, sink = exampleAddr2, targetFlow = "1" };
    await TryWs(ws, "circlesV2_findPath", flowRequest);

    await TryWs(ws, "circles_getProfileCid", exampleAddr1);
    await TryWs(ws, "circles_getProfileCidBatch", new[] { new[] { exampleAddr1, exampleAddr2 } });
    await TryWs(ws, "circles_getAvatarInfo", exampleAddr1);
    await TryWs(ws, "circles_getAvatarInfoBatch", new[] { new[] { exampleAddr1, exampleAddr2 } });

    await TryWs(ws, "circles_getProfileByCid", exampleProfileCid!);
    await TryWs(ws, "circles_getProfileByCidBatch", new[] { new[] { exampleProfileCid! } });

    await TryWs(ws, "circles_getProfileByAddress", exampleAddr1);
    await TryWs(ws, "circles_getProfileByAddressBatch", new[] { new[] { exampleAddr1, exampleAddr2 } });

    await TryWs(ws, "circles_getTokenInfo", exampleAddr1);
    await TryWs(ws, "circles_getTokenInfoBatch", new[] { new[] { exampleAddr1, exampleAddr2 } });

    await TryWs(ws, "circles_searchProfiles", "alice", 1, 0);

    if (exampleTable is not null)
    {
        var (ns, table) = exampleTable.Value;
        var selectDto = new { @namespace = ns, table, limit = 1 };
        await TryWs(ws, "circles_query", selectDto);
    }
    else
    {
        await TryWs(ws, "circles_query", new { limit = 1 });
    }

    // --- Hardened close: never crash if the server misbehaves
    var canAttemptClose =
        ws.State == WebSocketState.Open ||
        ws.State == WebSocketState.CloseReceived ||
        ws.State == WebSocketState.CloseSent;

    if (canAttemptClose)
    {
        try
        {
            var isCloseReceived = ws.State == WebSocketState.CloseReceived;
            var isOpen = ws.State == WebSocketState.Open;

            // Prefer sending only our close frame if peer already sent theirs.
            if (isCloseReceived)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            else if (isOpen)
            {
                // This may throw if peer drops abruptly; we catch and record below.
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            // If CloseSent, do nothing; disposal will finish it.
        }
        catch (Exception ex)
        {
            var msg = $"WS close failed: {ex.Message}";
            Console.WriteLine(Colorize($"[WS] {msg}", Ansi.Yellow));
            results.Add(("WS", "<close>", false, msg));
            // Don't rethrow; we want the summary to print.
        }
    }
}

// Execute
try
{
    var hasHttp = !string.IsNullOrWhiteSpace(httpUrl);
    if (hasHttp)
    {
        await RunOverHttp();
    }
}
catch (Exception ex)
{
    var msg = $"HTTP run failed: {ex.Message}";
    Console.WriteLine(Colorize($"[HTTP] {msg}", Ansi.Red));
    results.Add(("HTTP", "<unhandled>", false, msg));
}

try
{
    var hasWs = !string.IsNullOrWhiteSpace(wsUrl);
    if (hasWs)
    {
        await RunOverWs();
    }
}
catch (Exception ex)
{
    var msg = $"WS run failed: {ex.Message}";
    Console.WriteLine(Colorize($"[WS] {msg}", Ansi.Red));
    results.Add(("WS", "<unhandled>", false, msg));
}

// Summary
Console.WriteLine();
Console.WriteLine(Colorize("==== Circles RPC Smoke Test Summary ====", Ansi.Bold));

static bool IsMetaEntry(string? method)
{
    var hasValue = !string.IsNullOrWhiteSpace(method);
    if (!hasValue)
    {
        return false;
    }

    return method![0] == '<'; // e.g. "<close>", "<connect>", "<unhandled>"
}

var byTransport = results.GroupBy(r => r.Transport);
foreach (var group in byTransport)
{
    var rpc = group.Where(e => !IsMetaEntry(e.Method)).ToList();
    var meta = group.Where(e => IsMetaEntry(e.Method)).ToList();

    var total = rpc.Count;
    var ok = rpc.Count(e => e.Ok);
    var fail = total - ok;

    var head = $"{group.Key}: {Colorize($"{ok}", Ansi.Green)}/{total} ok";
    if (fail > 0)
    {
        head += $"  {Colorize($"{fail} failed", Ansi.Red)}";
    }

    if (meta.Count > 0)
    {
        head += $"  {Colorize($"{meta.Count} transport issue(s)", Ansi.Yellow)}";
    }

    Console.WriteLine(head);

    // List RPC calls: failures first, then by method
    var orderedRpc = rpc.OrderBy(e => e.Ok) // false first
        .ThenBy(e => e.Method, StringComparer.Ordinal);
    foreach (var entry in orderedRpc)
    {
        var status = entry.Ok ? Colorize("✔ OK", Ansi.Green) : Colorize("✖ FAIL", Ansi.Red);
        var method = Colorize(entry.Method, Ansi.Cyan);
        var note = Colorize($"({entry.Note})", Ansi.Gray);
        Console.WriteLine($"  - {method}: {status} {note}");
    }

    // List meta/transport issues separately, if any
    if (meta.Count > 0)
    {
        Console.WriteLine(Colorize("  Transport issues:", Ansi.Yellow));
        foreach (var entry in meta)
        {
            var status = entry.Ok ? Colorize("✔ OK", Ansi.Green) : Colorize("✖ FAIL", Ansi.Red);
            var method = Colorize(entry.Method, Ansi.Cyan);
            var note = Colorize($"({entry.Note})", Ansi.Gray);
            Console.WriteLine($"    - {method}: {status} {note}");
        }
    }

    Console.WriteLine(); // blank line between transports
}

// Helpers
static Dictionary<string, string> ParseArgs(string[] args)
{
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var a in args)
    {
        if (a.StartsWith("--"))
        {
            var eq = a.IndexOf('=');
            if (eq > 2)
            {
                var key = a.Substring(2, eq - 2);
                var val = a.Substring(eq + 1);
                dict[key] = val;
            }
            else
            {
                dict[a.TrimStart('-')] = "true";
            }
        }
    }

    return dict;
}

record RpcRequest(string Method, object?[] Params)
{
    public string Jsonrpc { get; } = "2.0";
    public string Method { get; } = Method;
    public object?[] Params { get; } = Params;
    public long Id { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

static class Ansi
{
    public const string Reset = "\u001b[0m";
    public const string Bold = "\u001b[1m";
    public const string Dim = "\u001b[2m";
    public const string Red = "\u001b[31m";
    public const string Green = "\u001b[32m";
    public const string Yellow = "\u001b[33m";
    public const string Blue = "\u001b[34m";
    public const string Magenta = "\u001b[35m";
    public const string Cyan = "\u001b[36m";
    public const string Gray = "\u001b[90m";
}