using System.Text.Json;

namespace Circles.Index.Profiles;

internal sealed class DownloadWorker
{
    private readonly QueueRepository _repo;
    private readonly HttpGatewayPool _gateways;
    private readonly int _batchSize;
    private readonly SemaphoreSlim _concurrency;
    private readonly CancellationToken _token;
    private readonly TimeSpan _maxBackoff = TimeSpan.FromHours(1);

    public DownloadWorker(QueueRepository repo, HttpGatewayPool gateways, int maxParallel, CancellationToken token)
    {
        _repo = repo;
        _gateways = gateways;
        _batchSize = Math.Max(1, maxParallel * 2);
        _concurrency = new SemaphoreSlim(maxParallel, maxParallel);
        _token = token;
    }

    public async Task RunAsync()
    {
        while (!_token.IsCancellationRequested)
        {
            var batch = await _repo.ReserveBatchAsync(_batchSize, _token);
            if (batch.Count == 0)
            {
                await Task.Delay(1000, _token);
                continue;
            }

            foreach (var item in batch)
            {
                await _concurrency.WaitAsync(_token);
                _ = ProcessAsync(item)
                    .ContinueWith(_ => _concurrency.Release(), _token);
            }
        }
    }

    private async Task ProcessAsync(QueueItem item)
    {
        try
        {
            HttpClient client = _gateways.Next();
            using var resp = await client.GetAsync($"/ipfs/{item.Cid}", _token);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(_token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: _token);
            await InsertPayloadAsync(item.Cid, doc, _token);
            await _repo.MarkCompletedAsync(item.Cid, _token);
        }
        catch (Exception ex) when (!_token.IsCancellationRequested)
        {
            int nextAttempt = item.AttemptCount + 1;
            TimeSpan backoff = CalcBackoff(nextAttempt);
            Console.WriteLine($"{DateTime.UtcNow:o}  {item.Cid}  failed ({nextAttempt}): {ex.Message}");
            await _repo.MarkFailedAsync(item.Cid, nextAttempt, backoff, ex.Message, _token);
        }
    }

    private async Task InsertPayloadAsync(string cid, JsonDocument doc, CancellationToken tok)
    {
        const string sql = "INSERT INTO ipfs_files (cid,payload) VALUES ($1,$2::jsonb) ON CONFLICT DO NOTHING;";
        await using var cmd = _repo.DataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(cid);
        cmd.Parameters.AddWithValue(doc.RootElement.GetRawText());
        await cmd.ExecuteNonQueryAsync(tok);
    }

    private TimeSpan CalcBackoff(int attempt)
    {
        double exp = Math.Min(attempt, 16);
        TimeSpan t = TimeSpan.FromSeconds(Math.Pow(2, exp));
        return t > _maxBackoff ? _maxBackoff : t;
    }
}