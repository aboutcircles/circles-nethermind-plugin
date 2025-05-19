namespace Circles.Index.Profiles;

internal sealed class HttpGatewayPool : IDisposable
{
    private readonly HttpClient[] _clients;
    private int _counter;

    public HttpGatewayPool(IEnumerable<string> gateways)
    {
        var list = new List<HttpClient>();
        foreach (string gw in gateways)
        {
            var c = new HttpClient { BaseAddress = new Uri(gw, UriKind.Absolute), Timeout = TimeSpan.FromSeconds(1) };
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            list.Add(c);
        }

        if (list.Count == 0) throw new ArgumentException("No gateway urls provided");
        _clients = list.ToArray();
    }

    public HttpClient Next() => _clients[Interlocked.Increment(ref _counter) % _clients.Length];

    public void Dispose()
    {
        foreach (var c in _clients) c.Dispose();
    }
}