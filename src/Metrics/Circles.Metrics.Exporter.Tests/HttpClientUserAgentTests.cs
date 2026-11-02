using System.Net;
using System.Text;
using Circles.Metrics.Exporter.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Circles.Metrics.Exporter.Tests;

/// <summary>
/// CoinGecko answers 403 to a request that carries no User-Agent header. The services
/// are registered in the same order as Program.cs, so the test sends through the client
/// PriceService actually receives at runtime.
/// </summary>
[TestFixture]
public class HttpClientUserAgentTests
{
    [Test]
    public async Task CoinGeckoRequestCarriesUserAgent()
    {
        var handler = new RecordingHandler("""{"gnosis":{"usd":123.45}}""");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CoinGecko:ApiKey"] = "CG-test" })
            .Build());
        services.AddHttpClientUserAgent();
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        services.AddHttpClient<PriceService>();
        services.AddSingleton<PriceService>();

        await using var provider = services.BuildServiceProvider();
        var (price, source) = await provider.GetRequiredService<PriceService>().GetGnoPriceUsdAsync();

        Assert.That(source, Is.EqualTo(PriceService.PriceSource.CoinGeckoLive));
        Assert.That(price, Is.EqualTo(123.45));
        Assert.That(handler.UserAgents, Is.EqualTo(new[] { HttpClientSetup.UserAgent }));
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
