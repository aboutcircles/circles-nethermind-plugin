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
