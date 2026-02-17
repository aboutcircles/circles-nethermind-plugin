using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Circles.Rpc.Host;
using Moq;

namespace Circles.Rpc.Host.Tests;

[TestFixture]
public class NethermindRpcClientTests
{
    [Test]
    public async Task EthCall_SendsExpectedPayloadAndParsesResult()
    {
        HttpRequestMessage? capturedRequest = null;
        var rpcClient = CreateClient(message =>
        {
            capturedRequest = message;
            return JsonResponse("{\"jsonrpc\":\"2.0\",\"result\":\"0xdeadbeef\"}");
        });

        var result = await rpcClient.EthCall("0xabc", "0x123", "latest");

        Assert.That(result, Is.EqualTo("0xdeadbeef"));
        Assert.That(capturedRequest, Is.Not.Null);

        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"method\":\"eth_call\""));
        Assert.That(body, Does.Contain("0xabc"));
        Assert.That(body, Does.Contain("latest"));
    }

    [Test]
    public void EthCall_WithMalformedResult_Throws()
    {
        var rpcClient = CreateClient(_ => JsonResponse("{\"jsonrpc\":\"2.0\",\"result\":\"1234\"}"));

        Assert.That(
            async () => await rpcClient.EthCall("0xabc", "0x123", null),
            Throws.Exception);
    }

    [Test]
    public async Task IsSynced_ReturnsTrueWhenClientIsNotSyncing()
    {
        var rpcClient = CreateClient(_ => JsonResponse("{\"jsonrpc\":\"2.0\",\"result\":false}"));

        var result = await rpcClient.IsSynced();
        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsSynced_ReturnsFalseWhenClientIsSyncing()
    {
        var syncingPayload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            result = new
            {
                startingBlock = "0x1",
                currentBlock = "0x2",
                highestBlock = "0x3"
            }
        });

        var rpcClient = CreateClient(_ => JsonResponse(syncingPayload));

        var result = await rpcClient.IsSynced();
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task GetLatestBlockNumber_ParsesHexBlockNumber()
    {
        var rpcClient = CreateClient(_ => JsonResponse("{\"jsonrpc\":\"2.0\",\"result\":\"0x10\"}"));

        var blockNumber = await rpcClient.GetLatestBlockNumber();
        Assert.That(blockNumber, Is.EqualTo(16));
    }

    // ── ForwardRpcRequest tests ──────────────────────────────────────────────

    [Test]
    public async Task ForwardRpcRequest_SendsCorrectPayloadAndReturnsVerbatimResponse()
    {
        HttpRequestMessage? capturedRequest = null;
        var expectedResponse = "{\"jsonrpc\":\"2.0\",\"id\":42,\"result\":\"0xabc123\"}";
        var rpcClient = CreateClient(message =>
        {
            capturedRequest = message;
            return JsonResponse(expectedResponse);
        });

        var @params = JsonDocument.Parse("[\"0x1\"]").RootElement;
        var id = JsonDocument.Parse("42").RootElement;
        var result = await rpcClient.ForwardRpcRequest("eth_getBlockByNumber", id, @params);

        // Verify payload sent to Nethermind
        Assert.That(capturedRequest, Is.Not.Null);
        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"method\":\"eth_getBlockByNumber\""));
        Assert.That(body, Does.Contain("\"id\":42"));
        Assert.That(body, Does.Contain("\"jsonrpc\":\"2.0\""));

        // Verify response returned as-is
        Assert.That(result.GetProperty("result").GetString(), Is.EqualTo("0xabc123"));
        Assert.That(result.GetProperty("id").GetInt32(), Is.EqualTo(42));
    }

    [Test]
    public async Task ForwardRpcRequest_WithStringId_PreservesIdType()
    {
        HttpRequestMessage? capturedRequest = null;
        var expectedResponse = "{\"jsonrpc\":\"2.0\",\"id\":\"req-abc\",\"result\":\"0x1\"}";
        var rpcClient = CreateClient(message =>
        {
            capturedRequest = message;
            return JsonResponse(expectedResponse);
        });

        var @params = JsonDocument.Parse("[]").RootElement;
        var id = JsonDocument.Parse("\"req-abc\"").RootElement;
        var result = await rpcClient.ForwardRpcRequest("eth_blockNumber", id, @params);

        // Verify string ID forwarded correctly
        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        Assert.That(body, Does.Contain("\"id\":\"req-abc\""));

        // Verify string ID in response
        Assert.That(result.GetProperty("id").GetString(), Is.EqualTo("req-abc"));
    }

    [Test]
    public async Task ForwardRpcRequest_PreservesNethermindErrorResponse()
    {
        var errorResponse = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32602,\"message\":\"Invalid params\"}}";
        var rpcClient = CreateClient(_ => JsonResponse(errorResponse));

        var @params = JsonDocument.Parse("[]").RootElement;
        var id = JsonDocument.Parse("1").RootElement;
        var result = await rpcClient.ForwardRpcRequest("eth_getBalance", id, @params);

        Assert.That(result.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(-32602));
        Assert.That(result.GetProperty("error").GetProperty("message").GetString(), Is.EqualTo("Invalid params"));
    }

    [Test]
    public void ForwardRpcRequest_ThrowsOnHttpError()
    {
        var rpcClient = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Internal Server Error")
            });

        var @params = JsonDocument.Parse("[]").RootElement;
        var id = JsonDocument.Parse("1").RootElement;
        Assert.That(
            async () => await rpcClient.ForwardRpcRequest("eth_blockNumber", id, @params),
            Throws.TypeOf<HttpRequestException>());
    }

    // ── ForwardRawRequest tests ──────────────────────────────────────────────

    [Test]
    public async Task ForwardRawRequest_ForwardsBatchAndReturnsBatchResponse()
    {
        HttpRequestMessage? capturedRequest = null;
        var batchResponse = "[{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"0x1\"},{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":\"100\"}]";
        var rpcClient = CreateClient(message =>
        {
            capturedRequest = message;
            return JsonResponse(batchResponse);
        });

        var batch = Encoding.UTF8.GetBytes(
            "[{\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[],\"id\":1}," +
            "{\"jsonrpc\":\"2.0\",\"method\":\"net_version\",\"params\":[],\"id\":2}]");

        var result = await rpcClient.ForwardRawRequest(batch);

        // Verify body was forwarded
        Assert.That(capturedRequest, Is.Not.Null);
        Assert.That(capturedRequest!.Content!.Headers.ContentType!.MediaType, Is.EqualTo("application/json"));
        var sentBody = await capturedRequest.Content.ReadAsByteArrayAsync();
        Assert.That(sentBody, Is.EqualTo(batch));

        // Verify batch response (JSON array)
        Assert.That(result.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(result.GetArrayLength(), Is.EqualTo(2));
    }

    [Test]
    public void ForwardRawRequest_ThrowsOnHttpError()
    {
        var rpcClient = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("Bad Gateway")
            });

        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[],\"id\":1}");
        Assert.That(
            async () => await rpcClient.ForwardRawRequest(body),
            Throws.TypeOf<HttpRequestException>());
    }

    private static NethermindRpcClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHttpMessageHandler(handler)));

        return new NethermindRpcClient(factoryMock.Object, "http://localhost:8545");
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
