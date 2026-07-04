using Circles.Pathfinder.Data;
using Circles.Pathfinder.Graphs;
using Circles.Pathfinder.Host;
using Circles.Pathfinder.Host.State;
using Circles.Pathfinder.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Circles.Pathfinder.Tests;

/// <summary>
/// Execution-level tests for <see cref="NetworkStateUpdaterService.IncrementalRefreshBalancesMatView"/>
/// against a real PostgreSQL (Testcontainers). These are the tests that actually run the incremental
/// SQL — the structural string-match tests cannot catch a query that is syntactically fine but wrong
/// against a live database (e.g. the earlier attempt that DML'd a materialized view).
///
/// Every scenario asserts the maintained table is byte-for-byte identical to a full recompute of the
/// balances aggregation, so any divergence in the incremental delta logic fails the test.
///
/// Docker-gated: skipped when no Docker endpoint is detectable (set RUN_PATHFINDER_INTEGRATION_TESTS
/// to force on/off). Not parallelizable — reads a process-wide metric counter around the refresh call.
/// </summary>
[TestFixture]
public class IncrementalBalanceRefreshIntegrationTests
{
    private const string RouterAddr = "0xdc287474114cc0551a81ddc2eb51783fbf34802f";
    private const string Zero = "0x0000000000000000000000000000000000000000";
    private const string Alice = "0xaa00000000000000000000000000000000000001";
    private const string Bob = "0xbb00000000000000000000000000000000000002";
    private const string Carol = "0xcc00000000000000000000000000000000000003";
    private const string TokenA = "0x1100000000000000000000000000000000000001";
    private const string TokenB = "0x2200000000000000000000000000000000000002";

    private static readonly bool DockerEnabled = ComputeDockerEnabled();
    private static readonly string BalancesSql = LoadBalancesSql();

    private PostgreSqlContainer? _pg;
    private string _connStr = null!;

    private static bool ComputeDockerEnabled()
    {
        var env = Environment.GetEnvironmentVariable("RUN_PATHFINDER_INTEGRATION_TESTS");
        if (string.Equals(env, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(env, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists("/var/run/docker.sock")
               || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOCKER_HOST"))
               || File.Exists(Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "run", "docker.sock"));
    }

    /// <summary>Locates the production balances SQL by walking up from the test output dir to the repo.</summary>
    private static string LoadBalancesSql()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "Index", "Circles.Index.CirclesViews", "queries", "V_CrcV2_BalancesByAccountAndToken.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate V_CrcV2_BalancesByAccountAndToken.sql walking up from {AppContext.BaseDirectory}");
    }

    [OneTimeSetUp]
    public async Task StartContainer()
    {
        // Host.Settings/Common.Settings constructors require these; harmless dummies for the test.
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION_STRING",
            Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING") ?? "Host=localhost;Database=test;Username=test;Password=test");
        Environment.SetEnvironmentVariable("NETHERMIND_RPC_URL",
            Environment.GetEnvironmentVariable("NETHERMIND_RPC_URL") ?? "http://localhost:8545");

        if (!DockerEnabled) return;
        _pg = new PostgreSqlBuilder("postgres:15-alpine")
            .WithDatabase("circles_test").WithUsername("test").WithPassword("test").Build();
        await _pg.StartAsync();
        _connStr = _pg.GetConnectionString();
    }

    [OneTimeTearDown]
    public async Task StopContainer()
    {
        if (_pg != null) await _pg.DisposeAsync();
    }

    [SetUp]
    public void ResetSchema()
    {
        if (!DockerEnabled) Assert.Ignore("Docker not available — skipping Postgres integration test.");
        using var conn = Open();
        Exec(conn, """
            DROP VIEW  IF EXISTS public."V_CrcV2_BalancesByAccountAndToken";
            DROP TABLE IF EXISTS "M_CrcV2_BalancesByAccountAndToken" CASCADE;
            DROP TABLE IF EXISTS "CrcV2_TransferSingle";
            DROP TABLE IF EXISTS "CrcV2_TransferBatch";
            CREATE TABLE "CrcV2_TransferSingle" (
                "blockNumber" bigint NOT NULL, "transactionIndex" bigint NOT NULL, "logIndex" bigint NOT NULL,
                "timestamp" bigint NOT NULL, "from" text NOT NULL, "to" text NOT NULL,
                "id" numeric NOT NULL, "value" numeric NOT NULL, "tokenAddress" text NOT NULL,
                PRIMARY KEY ("blockNumber","transactionIndex","logIndex"));
            CREATE TABLE "CrcV2_TransferBatch" (
                "blockNumber" bigint NOT NULL, "transactionIndex" bigint NOT NULL, "logIndex" bigint NOT NULL,
                "batchIndex" bigint NOT NULL, "timestamp" bigint NOT NULL, "from" text NOT NULL, "to" text NOT NULL,
                "id" numeric NOT NULL, "value" numeric NOT NULL, "tokenAddress" text NOT NULL,
                PRIMARY KEY ("blockNumber","transactionIndex","logIndex","batchIndex"));
            """);
    }

    // ── Scenarios ────────────────────────────────────────────────────────────

    [Test]
    public void Refresh_AppliesDelta_AndMatchesFullRecompute()
    {
        using var conn = Open();
        // Bootstrap: block 1 mints 100 tokenA to Alice.
        Single(conn, block: 1, from: Zero, to: Alice, token: TokenA, value: "100");
        ApplyBalancesSchema(conn);
        Assert.That(Watermark(conn), Is.EqualTo(1), "watermark should start at the bootstrap tip");

        // Delta: Alice sends 30 to Bob (block 2); a fresh mint of 50 tokenB to Carol (block 3).
        Single(conn, block: 2, from: Alice, to: Bob, token: TokenA, value: "30");
        Single(conn, block: 3, from: Zero, to: Carol, token: TokenB, value: "50");

        Refresh(conn);

        Assert.That(Mismatches(conn), Is.Zero, "table must equal a full recompute after the incremental delta");
        Assert.That(Balance(conn, Alice, TokenA), Is.EqualTo(70m));
        Assert.That(Balance(conn, Bob, TokenA), Is.EqualTo(30m));
        Assert.That(Balance(conn, Carol, TokenB), Is.EqualTo(50m));
        Assert.That(Watermark(conn), Is.EqualTo(3), "watermark must advance to the newest processed block");
    }

    [Test]
    public void Refresh_ZeroBalance_DeletesRow()
    {
        using var conn = Open();
        Single(conn, block: 1, from: Zero, to: Alice, token: TokenA, value: "100");
        ApplyBalancesSchema(conn);

        // Alice spends her entire balance to Bob → Alice hits 0 and must be removed.
        Single(conn, block: 2, from: Alice, to: Bob, token: TokenA, value: "100");
        Refresh(conn);

        Assert.That(Mismatches(conn), Is.Zero);
        Assert.That(RowCount(conn, Alice, TokenA), Is.Zero, "zeroed account/token must be deleted, not kept at 0");
        Assert.That(Balance(conn, Bob, TokenA), Is.EqualTo(100m));
    }

    [Test]
    public void Refresh_FirstBoot_EmptyTable_BuildsEverything()
    {
        using var conn = Open();
        // No transfers at bootstrap → empty table, watermark 0.
        ApplyBalancesSchema(conn);
        Assert.That(Watermark(conn), Is.Zero);
        Assert.That(TotalRows(conn), Is.Zero);

        // All history arrives after bootstrap; the watermark-0 delta must cover it all.
        Single(conn, block: 5, from: Zero, to: Alice, token: TokenA, value: "100");
        Single(conn, block: 6, from: Alice, to: Bob, token: TokenA, value: "40");
        Refresh(conn);

        Assert.That(Mismatches(conn), Is.Zero, "first-boot delta (watermark 0) must equal a full recompute");
        Assert.That(Balance(conn, Alice, TokenA), Is.EqualTo(60m));
        Assert.That(Balance(conn, Bob, TokenA), Is.EqualTo(40m));
    }

    [Test]
    public void Refresh_NoNewTransfers_IsNoOp()
    {
        using var conn = Open();
        Single(conn, block: 1, from: Zero, to: Alice, token: TokenA, value: "100");
        ApplyBalancesSchema(conn);
        Refresh(conn); // fold in whatever exists (nothing past watermark)

        var upsertsBefore = UpsertCount();
        var deletesBefore = DeleteCount();
        var wmBefore = Watermark(conn);

        Refresh(conn); // second cycle, no new transfers

        Assert.That(Mismatches(conn), Is.Zero);
        Assert.That(Watermark(conn), Is.EqualTo(wmBefore), "watermark must not move without new data");
        Assert.That(UpsertCount() - upsertsBefore, Is.Zero, "idle cycle must upsert nothing");
        Assert.That(DeleteCount() - deletesBefore, Is.Zero, "idle cycle must delete nothing");
    }

    [Test]
    public void Refresh_TouchesOnlyChangedRows_NoWriteAmplification()
    {
        using var conn = Open();
        // Bootstrap 50 independent accounts (mints in early blocks) → 50 rows.
        for (var i = 0; i < 50; i++)
        {
            var acct = $"0xac00000000000000000000000000000000{i:x6}";
            Single(conn, block: 1, tx: i, from: Zero, to: acct, token: TokenA, value: "100");
        }
        ApplyBalancesSchema(conn);
        Assert.That(TotalRows(conn), Is.EqualTo(50));

        // Later transfers touch exactly two (account, token) pairs: Alice/TokenB and Bob/TokenB.
        Single(conn, block: 3, from: Zero, to: Alice, token: TokenB, value: "70"); // Alice/TokenB = 70
        Single(conn, block: 4, from: Alice, to: Bob, token: TokenB, value: "20");  // Alice → 50, Bob → 20

        var upsertsBefore = UpsertCount();
        Refresh(conn);
        var upserted = UpsertCount() - upsertsBefore;

        Assert.That(Mismatches(conn), Is.Zero);
        // Only Alice/TokenB and Bob/TokenB changed. With the old FULL OUTER JOIN this would rewrite
        // all ~52 rows; the delta-driven LEFT JOIN must touch just the changed pairs.
        Assert.That(upserted, Is.EqualTo(2),
            "incremental refresh must upsert only the (account, token) pairs that changed, not the whole table");
    }

    [Test]
    public void Refresh_IncludesBatchTransfers()
    {
        using var conn = Open();
        Single(conn, block: 1, from: Zero, to: Alice, token: TokenA, value: "100");
        ApplyBalancesSchema(conn);

        // A batch transfer (block 2) moving two tokens from Alice to Bob.
        Batch(conn, block: 2, batchIndex: 0, from: Alice, to: Bob, token: TokenA, value: "25");
        Batch(conn, block: 2, batchIndex: 1, from: Zero, to: Bob, token: TokenB, value: "10");
        Refresh(conn);

        Assert.That(Mismatches(conn), Is.Zero, "batch legs must be folded in identically to single legs");
        Assert.That(Balance(conn, Alice, TokenA), Is.EqualTo(75m));
        Assert.That(Balance(conn, Bob, TokenA), Is.EqualTo(25m));
        Assert.That(Balance(conn, Bob, TokenB), Is.EqualTo(10m));
    }

    [Test]
    public void Refresh_MultipleCycles_StayCorrect()
    {
        using var conn = Open();
        Single(conn, block: 1, from: Zero, to: Alice, token: TokenA, value: "100");
        ApplyBalancesSchema(conn);

        // Cycle 1
        Single(conn, block: 2, from: Alice, to: Bob, token: TokenA, value: "30");
        Refresh(conn);
        Assert.That(Mismatches(conn), Is.Zero);
        Assert.That(Watermark(conn), Is.EqualTo(2));

        // Cycle 2 — builds on the state left by cycle 1
        Single(conn, block: 3, from: Bob, to: Carol, token: TokenA, value: "30"); // Bob → 0 (deleted), Carol → 30
        Single(conn, block: 4, from: Zero, to: Alice, token: TokenA, value: "5");  // Alice 70 → 75
        Refresh(conn);

        Assert.That(Mismatches(conn), Is.Zero, "accumulated state across cycles must equal a full recompute");
        Assert.That(Balance(conn, Alice, TokenA), Is.EqualTo(75m));
        Assert.That(RowCount(conn, Bob, TokenA), Is.Zero);
        Assert.That(Balance(conn, Carol, TokenA), Is.EqualTo(30m));
        Assert.That(Watermark(conn), Is.EqualTo(4));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private NpgsqlConnection Open()
    {
        var conn = new NpgsqlConnection(_connStr);
        conn.Open();
        return conn;
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void ApplyBalancesSchema(NpgsqlConnection conn) => Exec(conn, BalancesSql);

    private static void Single(NpgsqlConnection conn, long block, string from, string to, string token, string value,
        long tx = 0, long log = 0)
        => Exec(conn, $"""
            INSERT INTO "CrcV2_TransferSingle" ("blockNumber","transactionIndex","logIndex","timestamp","from","to","id","value","tokenAddress")
            VALUES ({block},{tx},{log},{block * 10},'{from}','{to}',{TokenId(token)},{value},'{token}');
            """);

    private static void Batch(NpgsqlConnection conn, long block, long batchIndex, string from, string to, string token,
        string value, long tx = 0, long log = 0)
        => Exec(conn, $"""
            INSERT INTO "CrcV2_TransferBatch" ("blockNumber","transactionIndex","logIndex","batchIndex","timestamp","from","to","id","value","tokenAddress")
            VALUES ({block},{tx},{log},{batchIndex},{block * 10},'{from}','{to}',{TokenId(token)},{value},'{token}');
            """);

    // Deterministic token id per token address (both the refresh and the recompute read the same
    // (id, tokenAddress) from the transfer rows, so any stable mapping keeps them in agreement).
    private static string TokenId(string token) => token switch
    {
        TokenA => "1",
        TokenB => "2",
        _ => "3"
    };

    private long Watermark(NpgsqlConnection conn)
        => Scalar<long>(conn, "SELECT COALESCE(MAX(\"_maxBlock\"), 0) FROM \"M_CrcV2_BalancesByAccountAndToken\"");

    private long TotalRows(NpgsqlConnection conn)
        => Scalar<long>(conn, "SELECT COUNT(*) FROM \"M_CrcV2_BalancesByAccountAndToken\"");

    private long RowCount(NpgsqlConnection conn, string account, string token)
        => Scalar<long>(conn,
            $"SELECT COUNT(*) FROM \"M_CrcV2_BalancesByAccountAndToken\" WHERE account='{account}' AND \"tokenAddress\"='{token}'");

    private decimal Balance(NpgsqlConnection conn, string account, string token)
        => Scalar<decimal>(conn,
            $"SELECT \"totalBalance\" FROM \"M_CrcV2_BalancesByAccountAndToken\" WHERE account='{account}' AND \"tokenAddress\"='{token}'");

    private static T Scalar<T>(NpgsqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var o = cmd.ExecuteScalar();
        return o is T t ? t : (T)Convert.ChangeType(o!, typeof(T));
    }

    /// <summary>
    /// Symmetric difference between the maintained table and a from-scratch recompute of the balances
    /// aggregation. Zero rows ⇒ the incremental table is exactly correct (keys, totals, lastActivity, _maxBlock).
    /// </summary>
    private int Mismatches(NpgsqlConnection conn)
    {
        const string sql = """
            WITH tx AS (
                SELECT "blockNumber","timestamp","from" AS account,"tokenAddress",id,-value AS delta FROM "CrcV2_TransferSingle"
                UNION ALL SELECT "blockNumber","timestamp","to","tokenAddress",id, value FROM "CrcV2_TransferSingle"
                UNION ALL SELECT "blockNumber","timestamp","from","tokenAddress",id,-value FROM "CrcV2_TransferBatch"
                UNION ALL SELECT "blockNumber","timestamp","to","tokenAddress",id, value FROM "CrcV2_TransferBatch"
            ), agg AS (
                SELECT account, id, "tokenAddress", sum(delta) AS balance, max("timestamp") AS last_ts, max("blockNumber") AS max_block
                FROM tx GROUP BY account, id, "tokenAddress"
            ), recompute AS (
                SELECT account, id::text AS "tokenId", "tokenAddress", last_ts AS "lastActivity",
                       balance AS "totalBalance", max_block AS "_maxBlock"
                FROM agg
                WHERE account <> '0x0000000000000000000000000000000000000000' AND balance > 0
            ), diff AS (
                (SELECT account,"tokenId","tokenAddress","lastActivity","totalBalance","_maxBlock" FROM recompute
                 EXCEPT
                 SELECT account,"tokenId","tokenAddress","lastActivity","totalBalance","_maxBlock" FROM "M_CrcV2_BalancesByAccountAndToken")
                UNION ALL
                (SELECT account,"tokenId","tokenAddress","lastActivity","totalBalance","_maxBlock" FROM "M_CrcV2_BalancesByAccountAndToken"
                 EXCEPT
                 SELECT account,"tokenId","tokenAddress","lastActivity","totalBalance","_maxBlock" FROM recompute)
            )
            SELECT COUNT(*) FROM diff;
            """;
        return (int)Scalar<long>(conn, sql);
    }

    private void Refresh(NpgsqlConnection conn)
    {
        // A dedicated connection: the method opens its own transaction on the connection it is given.
        using var svcConn = Open();
        CreateService().IncrementalRefreshBalancesMatView(svcConn);
    }

    private static NetworkStateUpdaterService CreateService()
    {
        var settings = new Circles.Pathfinder.Host.Settings { FullRefreshIntervalBlocks = 200, IncrementalEnabled = true };
        var mockLoadGraph = new MockLoadGraph();
        var pool = new CapacityGraphPool(RouterAddr, mockLoadGraph);
        var loadGraph = new LoadGraph("Host=localhost;Database=dummy;Username=x;Password=x", new Circles.Pathfinder.Settings());
        return new NetworkStateUpdaterService(new NetworkState(), settings,
            NullLogger<NetworkStateUpdaterService>.Instance, pool, loadGraph);
    }

    private static double UpsertCount() => GraphUpdateMetrics.BalanceMatViewIncrementalRows.WithLabels("upsert").Value;
    private static double DeleteCount() => GraphUpdateMetrics.BalanceMatViewIncrementalRows.WithLabels("delete").Value;
}
