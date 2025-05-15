using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Circles.Pathfinder.DTOs;
using Npgsql;
using NpgsqlTypes;

namespace Circles.Pathfinder.Host.LogDb;

/// <summary>
/// If the LOGDB_CONNECTION_STRING is configured, logs requests and responses to a db.
/// </summary>
public class PathLogDb
{
    private readonly string? _connectionString;

    public PathLogDb()
    {
        _connectionString = new Settings().LogDbConnectionString;

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return; // logging disabled
        }

        var dbVersion = GetCurrentDbVersion();
        ApplyMigrations(dbVersion);
    }

    #region migration helpers (unchanged)

    private void ApplyMigrations(int lastVersion)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        var scripts = GetAllSqlFromResources();
        foreach (var script in scripts)
        {
            if (script.Key <= lastVersion)
            {
                continue;
            }

            var scriptStream = typeof(PathLogDb).Assembly.GetManifestResourceStream(script.Value);
            if (scriptStream == null)
            {
                throw new Exception($"Could not find resource {script.Value}");
            }

            using var reader = new StreamReader(scriptStream);
            var sql = reader.ReadToEnd();

            using var tx = connection.BeginTransaction();
            try
            {
                using var migrationCmd = new NpgsqlCommand(sql, connection, tx);
                migrationCmd.ExecuteNonQuery();

                using var insertCmd =
                    new NpgsqlCommand("INSERT INTO _applied_migrations (id) VALUES (@id)", connection, tx);
                insertCmd.Parameters.AddWithValue("id", script.Key);
                insertCmd.ExecuteNonQuery();

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    private int GetCurrentDbVersion()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();

        using var checkTableCmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '_applied_migrations')",
            connection);
        var dbInitialised = (bool)(checkTableCmd.ExecuteScalar() ?? false);
        if (!dbInitialised)
        {
            return -1;
        }

        using var cmd = new NpgsqlCommand("SELECT id FROM _applied_migrations ORDER BY id DESC LIMIT 1", connection);
        var result = cmd.ExecuteScalar();
        return result == null ? -1 : (int)result;
    }

    private SortedDictionary<int, string> GetAllSqlFromResources()
    {
        var dict = new SortedDictionary<int, string>();
        var resources = typeof(PathLogDb).Assembly.GetManifestResourceNames();

        foreach (var res in resources)
        {
            var m = Regex.Match(res, @".*?(\d+)_.*?\.sql$");
            if (!m.Success)
            {
                continue;
            }

            dict[int.Parse(m.Groups[1].Value)] = res;
        }

        return dict;
    }

    #endregion

    /// <summary>
    /// Persists an incoming <see cref="FlowRequest"/>  
    /// Returns the generated request-id so that the caller can tie the response back.
    /// </summary>
    public async Task LogRequest(Guid id, FlowRequest flowRequest)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            // logging disabled – supply dummy id
            return;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = @"
insert into request
(id, source, sink, target_flow, to_tokens, from_tokens, exclude_to_tokens, exclude_from_tokens, with_wrap)
values (@id, @source, @sink, @target_flow, @to_tokens, @from_tokens, @exclude_to_tokens, @exclude_from_tokens, @with_wrap);";

        await using var cmd = new NpgsqlCommand(sql, connection);

        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("source", ToDbValue(flowRequest.Source));
        cmd.Parameters.AddWithValue("sink", ToDbValue(flowRequest.Sink));
        AddNumericParameter(cmd, "target_flow", ParseBigIntegerOrNull(flowRequest.TargetFlow));
        cmd.Parameters.AddWithValue("to_tokens", ToDbValue(flowRequest.ToTokens));
        cmd.Parameters.AddWithValue("from_tokens", ToDbValue(flowRequest.FromTokens));
        cmd.Parameters.AddWithValue("exclude_to_tokens",
            ToDbValue(flowRequest.ExcludedToTokens));
        cmd.Parameters.AddWithValue("exclude_from_tokens",
            ToDbValue(flowRequest.ExcludedFromTokens));
        cmd.Parameters.AddWithValue("with_wrap", ToDbValue(flowRequest.WithWrap));

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Persists a <see cref="MaxFlowResponse"/> coupled to a previously stored request.
    /// </summary>
    public async Task LogResponse(Guid requestId, MaxFlowResponse? response, bool success, string? errorMessage = null)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        var id = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        const string sql = @"
insert into response
(id, request_id, actual_flow, success, error_message, result)
values (@id, @request_id, @actual_flow, @success, @error_message, @result::jsonb);";

        await using var cmd = new NpgsqlCommand(sql, connection);

        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("request_id", requestId);
        AddNumericParameter(cmd, "actual_flow", ParseBigIntegerOrNull(response?.MaxFlow));
        cmd.Parameters.AddWithValue("success", success);
        cmd.Parameters.AddWithValue("error_message",
            ToDbValue(errorMessage));
        cmd.Parameters.AddWithValue("result", ToDbValue(response == null ? null : JsonSerializer.Serialize(response)));

        await cmd.ExecuteNonQueryAsync();
    }

    #region helpers

    private static object ToDbValue(object? value) => value ?? DBNull.Value;

    private static BigInteger? ParseBigIntegerOrNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return BigInteger.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var bi) ? bi : null;
    }

    private static void AddNumericParameter(NpgsqlCommand cmd, string name, BigInteger? value)
    {
        var p = cmd.Parameters.Add(name, NpgsqlDbType.Numeric);
        p.Value = (object?)value ?? DBNull.Value;
    }

    #endregion
}