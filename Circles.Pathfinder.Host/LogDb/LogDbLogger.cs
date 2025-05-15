using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Circles.Pathfinder.DTOs;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Pathfinder.Host.LogDb;

public sealed class PathLogDb : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly Channel<ILogItem> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker; // single writer thread

    public PathLogDb()
    {
        string? cs = new Settings().LogDbConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
        {
            return; // logging disabled
        }

        _dataSource = NpgsqlDataSource.Create(cs);

        // small-ish bounded buffer → natural back-pressure if DB stalls
        _queue = Channel.CreateBounded<ILogItem>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        // kick off the background writer
        _worker = Task.Run(() => WriterLoopAsync(_cts.Token), _cts.Token);

        // migrations run synchronously at startup
        int v = GetCurrentDbVersionAsync().GetAwaiter().GetResult();
        ApplyMigrationsAsync(v).GetAwaiter().GetResult();
    }

    public ValueTask LogRequest(Guid id, FlowRequest flowRequest)
        => _dataSource is null
            ? ValueTask.CompletedTask
            : _queue.Writer.WriteAsync(new RequestLogItem(id, flowRequest));

    public ValueTask LogResponse(Guid requestId, MaxFlowResponse? response, bool success,
        string? errorMessage = null)
        => _dataSource is null
            ? ValueTask.CompletedTask
            : _queue.Writer.WriteAsync(new ResponseLogItem(requestId, response, success, errorMessage));

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        while (await _queue.Reader.WaitToReadAsync(ct))
        {
            while (_queue.Reader.TryRead(out ILogItem item))
            {
                try
                {
                    await item.WriteAsync(_dataSource!, ct);
                }
                catch (Exception ex)
                {
                    // Swallow → log somewhere else; we must not kill the loop.
                    Console.Error.WriteLine($"[log-db] {ex}");
                }
            }
        }
    }

    private interface ILogItem
    {
        Task WriteAsync(NpgsqlDataSource ds, CancellationToken ct);
    }

    private sealed class RequestLogItem(Guid id, FlowRequest req) : ILogItem
    {
        private const string Sql = @"
insert into request
(id, source, sink, target_flow, to_tokens, from_tokens, exclude_to_tokens, exclude_from_tokens, with_wrap)
values (@id, @source, @sink, @target_flow, @to_tokens, @from_tokens, @exclude_to_tokens, @exclude_from_tokens, @with_wrap);";

        public async Task WriteAsync(NpgsqlDataSource ds, CancellationToken ct)
        {
            await using var cmd = ds.CreateCommand(Sql);

            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("source", ToDb(req.Source));
            cmd.Parameters.AddWithValue("sink", ToDb(req.Sink));
            AddNumeric(cmd, "target_flow", ParseBigInt(req.TargetFlow));
            cmd.Parameters.AddWithValue("to_tokens", ToDb(req.ToTokens));
            cmd.Parameters.AddWithValue("from_tokens", ToDb(req.FromTokens));
            cmd.Parameters.AddWithValue("exclude_to_tokens", ToDb(req.ExcludedToTokens));
            cmd.Parameters.AddWithValue("exclude_from_tokens", ToDb(req.ExcludedFromTokens));
            cmd.Parameters.AddWithValue("with_wrap", ToDb(req.WithWrap));

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class ResponseLogItem(Guid reqId, MaxFlowResponse? resp, bool ok, string? err) : ILogItem
    {
        private readonly Guid _id = Guid.NewGuid();

        private const string Sql = @"
insert into response
(id, request_id, actual_flow, success, error_message, result)
values (@id, @request_id, @actual_flow, @success, @error_message, @result::jsonb);";

        public async Task WriteAsync(NpgsqlDataSource ds, CancellationToken ct)
        {
            await using var cmd = ds.CreateCommand(Sql);

            cmd.Parameters.AddWithValue("id", _id);
            cmd.Parameters.AddWithValue("request_id", reqId);
            AddNumeric(cmd, "actual_flow", ParseBigInt(resp?.MaxFlow));
            cmd.Parameters.AddWithValue("success", ok);
            cmd.Parameters.AddWithValue("error_message", ToDb(err));
            cmd.Parameters.AddWithValue("result", ToDb(resp is null ? null : JsonSerializer.Serialize(resp)));

            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static object ToDb(object? val) => val ?? DBNull.Value;

    private static BigInteger? ParseBigInt(string? s)
        => string.IsNullOrWhiteSpace(s)
            ? null
            : BigInteger.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var bi)
                ? bi
                : null;

    private static void AddNumeric(NpgsqlCommand cmd, string name, BigInteger? v)
    {
        var p = cmd.Parameters.Add(name, NpgsqlDbType.Numeric);
        p.Value = (object?)v ?? DBNull.Value;
    }

    private async Task ApplyMigrationsAsync(int lastVersion)
    {
        await using NpgsqlConnection connection = await _dataSource!.OpenConnectionAsync();

        SortedDictionary<int, string> scripts = GetAllSqlFromResources();
        foreach (KeyValuePair<int, string> script in scripts)
        {
            bool migrationAlreadyApplied = script.Key <= lastVersion;
            if (migrationAlreadyApplied)
            {
                continue;
            }

            using Stream? scriptStream = typeof(PathLogDb).Assembly.GetManifestResourceStream(script.Value);
            if (scriptStream is null)
            {
                throw new InvalidOperationException($"Could not find resource {script.Value}");
            }

            using var reader = new StreamReader(scriptStream);
            string sql = await reader.ReadToEndAsync();

            await using var tx = await connection.BeginTransactionAsync();
            try
            {
                await using var migrationCmd = new NpgsqlCommand(sql, connection, tx);
                await migrationCmd.ExecuteNonQueryAsync();

                await using var insertCmd =
                    new NpgsqlCommand("INSERT INTO _applied_migrations (id) VALUES (@id)", connection, tx);
                insertCmd.Parameters.AddWithValue("id", script.Key);
                await insertCmd.ExecuteNonQueryAsync();

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
    }

    private async Task<int> GetCurrentDbVersionAsync()
    {
        await using NpgsqlConnection connection = await _dataSource!.OpenConnectionAsync();

        const string checkTableSql =
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '_applied_migrations')";

        await using var checkTableCmd = new NpgsqlCommand(checkTableSql, connection);
        bool dbInitialised = (bool)(await checkTableCmd.ExecuteScalarAsync() ?? false);

        if (!dbInitialised)
        {
            return -1;
        }

        await using var cmd =
            new NpgsqlCommand("SELECT id FROM _applied_migrations ORDER BY id DESC LIMIT 1", connection);

        object? result = await cmd.ExecuteScalarAsync();
        return result is null ? -1 : (int)result;
    }

    private static SortedDictionary<int, string> GetAllSqlFromResources()
    {
        var dict = new SortedDictionary<int, string>();
        string[] resources = typeof(PathLogDb).Assembly.GetManifestResourceNames();

        foreach (string res in resources)
        {
            Match m = Regex.Match(res, @".*?(\d+)_.*?\.sql$");
            if (!m.Success)
            {
                continue;
            }

            dict[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = res;
        }

        return dict;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is null)
            return;

        _queue.Writer.Complete();
        _cts.Cancel();

        try
        {
            await _worker;
        }
        catch
        {
            /* ignore */
        }

        await _dataSource.DisposeAsync();
        _cts.Dispose();
    }
}