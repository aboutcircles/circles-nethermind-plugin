using Circles.Rpc.Host;
using Npgsql;
using NUnit.Framework;
using Testcontainers.PostgreSql;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Pure-logic guard for the inertness-deciding header parse. Needs no database, so it runs in EVERY
/// environment (including Docker-less runners where the Testcontainers fixture below would skip) —
/// this is the part that decides whether a request is pinned at all, so it must never be skippable.
/// </summary>
[TestFixture]
public class BlockPinHeaderParsingTests
{
    [TestCase("123", ExpectedResult = 123L, TestName = "Positive value pins")]
    [TestCase("46400000", ExpectedResult = 46400000L, TestName = "Large positive value pins")]
    [TestCase("  789  ", ExpectedResult = 789L, TestName = "Surrounding whitespace tolerated")]
    [TestCase(null, ExpectedResult = null, TestName = "Null (no header) is no pin")]
    [TestCase("", ExpectedResult = null, TestName = "Empty is no pin")]
    [TestCase("   ", ExpectedResult = null, TestName = "Whitespace is no pin")]
    [TestCase("abc", ExpectedResult = null, TestName = "Non-numeric is no pin")]
    [TestCase("12.5", ExpectedResult = null, TestName = "Non-integer is no pin")]
    [TestCase("0", ExpectedResult = null, TestName = "Zero is no pin (not a degenerate block-0 pin)")]
    [TestCase("-5", ExpectedResult = null, TestName = "Negative is no pin")]
    public long? ParseMaxBlockHeaderValue_HonorsPositivity(string? raw)
        => ConnectionPinning.ParseMaxBlockHeaderValue(raw);
}

/// <summary>
/// Guards the header-gated block-pinning seam (<see cref="ConnectionPinning.ApplyAsync"/>) that the
/// test environment relies on. Proves the properties that matter for both correctness and prod safety:
///   - INERTNESS: with no block (every production request) reads hit the public schema and no session
///     state is set — byte-identical to pre-pinning behavior.
///   - ROUTING: with a block, unqualified view names resolve to the pinned-schema twin and the GUC
///     the pinned views read carries the block number.
///   - NO LEAK: a pinned connection returned to the pool does not leak its search_path/GUC to the next
///     caller that reuses the SAME physical connection (asserted via backend ProcessId).
///   - GRACEFUL: a search_path naming a missing schema (production has no pinned schema) is not an
///     error; unqualified names fall through to public.
///   - NON-POSITIVE: 0 / negative blocks are a no-op, never a degenerate empty pin.
///
/// Unlike <see cref="CirclesRpcModuleTests"/>, this fixture never constructs the RPC module, so it runs
/// without the Nethermind runtime — it touches only <see cref="ConnectionPinning"/> and Npgsql.
/// </summary>
[TestFixture]
[Category("RequiresDocker")]
public class BlockPinningRoutingTests
{
    private PostgreSqlContainer? _postgres;
    private NpgsqlDataSource? _dataSource;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        try
        {
            // postgres:15-alpine matches the project's "PostgreSQL 15+" requirement and the sibling fixtures.
            _postgres = new PostgreSqlBuilder("postgres:15-alpine").Build();
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Docker/Postgres test container unavailable: {ex.Message}");
            return;
        }

        // Max Pool Size = 1 forces the no-leak test to reuse the same physical connection.
        var csb = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { MaxPoolSize = 1 };
        _dataSource = NpgsqlDataSource.Create(csb.ConnectionString);

        // A probe view named identically in BOTH schemas with different payloads, so the schema a
        // query resolves to is directly observable from the row it returns. The pinned schema name
        // is taken from the production constant so a rename can't silently desync this test.
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE SCHEMA IF NOT EXISTS ""{ConnectionPinning.PinnedSchema}"";
            CREATE TABLE public.""V_Probe"" (marker text);
            INSERT INTO public.""V_Probe"" VALUES ('public');
            CREATE TABLE ""{ConnectionPinning.PinnedSchema}"".""V_Probe"" (marker text);
            INSERT INTO ""{ConnectionPinning.PinnedSchema}"".""V_Probe"" VALUES ('pinned');";
        await cmd.ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }

        if (_postgres != null)
        {
            await _postgres.DisposeAsync();
        }
    }

    // Reads through an UNqualified name so the result reflects search_path resolution.
    private static async Task<string?> ReadProbeMarkerAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT marker FROM ""V_Probe"" LIMIT 1";
        // `as string` maps a SQL NULL (DBNull.Value) to C# null rather than throwing on the cast.
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task<string?> ReadGucAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        // missing_ok = true → returns SQL NULL (never set) instead of erroring; surfaced as DBNull.
        cmd.CommandText = "SELECT current_setting('circles.max_block_number', true)";
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Test]
    public async Task NoBlock_ReadsPublicSchema_AndSetsNothing()
    {
        await using var conn = await _dataSource!.OpenConnectionAsync();
        await ConnectionPinning.ApplyAsync(conn, null);

        Assert.That(await ReadProbeMarkerAsync(conn), Is.EqualTo("public"),
            "Without a block, unqualified names must resolve to the public schema.");
        Assert.That(string.IsNullOrEmpty(await ReadGucAsync(conn)), Is.True,
            "Without a block, the max_block_number GUC must be unset.");
    }

    [Test]
    public async Task WithBlock_RoutesToPinnedSchema_AndSetsGuc()
    {
        await using var conn = await _dataSource!.OpenConnectionAsync();
        await ConnectionPinning.ApplyAsync(conn, 12_345_678L);

        Assert.That(await ReadProbeMarkerAsync(conn), Is.EqualTo("pinned"),
            "With a block, unqualified names must resolve to the pinned-schema twin.");
        Assert.That(await ReadGucAsync(conn), Is.EqualTo("12345678"),
            "With a block, the pinned views' GUC must carry the block number.");
    }

    [Test]
    public async Task NonPositiveBlock_IsNoOp()
    {
        await using var conn = await _dataSource!.OpenConnectionAsync();
        await ConnectionPinning.ApplyAsync(conn, 0L);
        Assert.That(await ReadProbeMarkerAsync(conn), Is.EqualTo("public"), "block 0 must not pin");

        await ConnectionPinning.ApplyAsync(conn, -5L);
        Assert.That(await ReadProbeMarkerAsync(conn), Is.EqualTo("public"), "negative block must not pin");
        Assert.That(string.IsNullOrEmpty(await ReadGucAsync(conn)), Is.True,
            "A non-positive block must not set the GUC.");
    }

    [Test]
    public async Task Pin_DoesNotLeak_AcrossPooledConnections()
    {
        int pinnedBackendPid;

        // Pin, then return the (single, pooled) physical connection.
        await using (var pinned = await _dataSource!.OpenConnectionAsync())
        {
            pinnedBackendPid = pinned.ProcessID;
            await ConnectionPinning.ApplyAsync(pinned, 999L);
            Assert.That(await ReadProbeMarkerAsync(pinned), Is.EqualTo("pinned"));
        }

        // Reacquire with no block: it must come back clean.
        await using var reused = await _dataSource!.OpenConnectionAsync();

        // Guards against the test passing for the wrong reason: prove it's the SAME physical
        // backend connection, so a "clean" read genuinely demonstrates reset-on-return, not a
        // fresh connection that was never pinned.
        Assert.That(reused.ProcessID, Is.EqualTo(pinnedBackendPid),
            "MaxPoolSize=1 should hand back the same physical connection.");

        await ConnectionPinning.ApplyAsync(reused, null);
        Assert.That(await ReadProbeMarkerAsync(reused), Is.EqualTo("public"),
            "A pinned connection returned to the pool must not leak its search_path to the next caller.");
        Assert.That(string.IsNullOrEmpty(await ReadGucAsync(reused)), Is.True,
            "A pinned connection returned to the pool must not leak its GUC to the next caller.");
    }

    [Test]
    public async Task SearchPath_ToMissingSchema_DoesNotThrow_AndFallsThroughToPublic()
    {
        // The production safety property: prod has no pinned schema. Setting a search_path that names
        // a non-existent schema must be accepted, with unqualified names resolving from the next
        // existing entry (public). ApplyAsync issues exactly such a SET.
        await using var conn = await _dataSource!.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SET search_path = a_schema_that_does_not_exist, public";

        Assert.DoesNotThrowAsync(async () => await cmd.ExecuteNonQueryAsync());
        Assert.That(await ReadProbeMarkerAsync(conn), Is.EqualTo("public"),
            "Unqualified names must fall through to public when the leading search_path schema is absent.");
    }
}
