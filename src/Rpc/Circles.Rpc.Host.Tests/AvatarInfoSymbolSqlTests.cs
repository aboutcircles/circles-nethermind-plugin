using Npgsql;
using NUnit.Framework;
using Testcontainers.PostgreSql;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Regression guard for the block-pinned <c>circles_getAvatarInfo(Batch)</c> 500.
///
/// The DB-fallback path (cache miss / cache failure / block-pinned read) ran
/// <see cref="CirclesRpcModule.V2AvatarInfoSql"/> and mapped column 3 into <c>AvatarInfo.Symbol</c>.
/// The buggy version sourced column 3 from <c>"CrcV2_RegisterShortName"."shortName"</c> — a
/// <c>numeric</c> column — and read it with <c>reader.GetString(3)</c>, which throws
/// <see cref="InvalidCastException"/> for any avatar that has a registered short name. Head traffic is
/// served by the cache, so the crash was dormant until PR #449 made block-pinned requests bypass the
/// cache and fall to this path, 500-ing for every such avatar (surfaced first by the SDK e2e suite).
///
/// The fix sources <c>Symbol</c> from <c>"CrcV2_RegisterGroup".symbol</c> (a <c>text</c> column),
/// matching the cache path: a group's registration symbol (e.g. "gCRC"), and "" for non-groups.
///
/// This fixture runs the EXACT production SQL constant (no drift) against a real Postgres and
/// reproduces the failure shape: both seeded avatars carry a numeric short name larger than
/// <see cref="long.MaxValue"/>, so the old join would have crashed on the very rows asserted here.
/// </summary>
[TestFixture]
[Category("RequiresDocker")]
public class AvatarInfoSymbolSqlTests
{
    // A real V2 short name is a uint > 2^63 once base58-decoded; numeric, not text. The score group
    // 0x93ed…321f has shortName 635142688640204903967 (≈6.35e20) — well past long.MaxValue (~9.2e18),
    // so reading it as a CLR string via GetString throws. Both seeded avatars use such values.
    private const string GroupAddress = "0x93ed5a96347927ff6ff6b790f8cf5258240c321f";
    private const string GroupShortName = "635142688640204903967";
    private const string GroupSymbol = "gCRC";
    private const string HumanAddress = "0x0004df58332be821ebd0a2f498c211873e3b8f2c";
    private const string HumanShortName = "987654321098765432109"; // also > long.MaxValue

    // A group whose RegisterGroup row exists but has a NULL symbol — exercises the IsDBNull(3) branch
    // on an actual group row (the seeded gCRC group never hits it).
    private const string NullSymbolGroupAddress = "0x1111111111111111111111111111111111111111";
    // An address that is in the request but absent from V_CrcV2_Avatars (unknown / V1-only) — the
    // batch surface must simply omit it, never crash.
    private const string UnknownAddress = "0x2222222222222222222222222222222222222222";

    private PostgreSqlContainer? _postgres;
    private NpgsqlDataSource? _dataSource;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        try
        {
            _postgres = new PostgreSqlBuilder("postgres:15-alpine").Build();
            await _postgres.StartAsync();
        }
        catch (Exception ex)
        {
            Assert.Ignore($"Docker/Postgres test container unavailable: {ex.Message}");
            return;
        }

        _dataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());

        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        // Minimal shapes matching the columns the production query touches. "shortName" is numeric and
        // "symbol" is text — the same types as the real schema (verified on staging), which is what makes
        // the GetString-on-numeric distinction meaningful.
        cmd.CommandText = @"
            CREATE TABLE public.""V_CrcV2_Avatars"" (
                avatar text, name text, type text, ""cidV0Digest"" bytea);
            CREATE TABLE public.""CrcV2_RegisterGroup"" (
                ""group"" text, name text, symbol text);
            CREATE TABLE public.""CrcV2_RegisterShortName"" (
                avatar text, ""shortName"" numeric);

            INSERT INTO public.""V_CrcV2_Avatars"" (avatar, name, type, ""cidV0Digest"") VALUES
                ('" + GroupAddress + @"', 'Gnosis', 'CrcV2_RegisterGroup', decode('1b4e6979861b7e2832c37abd9703d287b807d8479f0f7d0c2a88330c427f9638','hex')),
                ('" + HumanAddress + @"', NULL, 'CrcV2_RegisterHuman', NULL),
                ('" + NullSymbolGroupAddress + @"', 'NoSym', 'CrcV2_RegisterGroup', NULL);

            INSERT INTO public.""CrcV2_RegisterGroup"" (""group"", name, symbol) VALUES
                ('" + GroupAddress + @"', 'Gnosis', '" + GroupSymbol + @"'),
                ('" + NullSymbolGroupAddress + @"', 'NoSym', NULL);

            -- BOTH avatars have a numeric short name: this is the exact data shape that crashed the old
            -- GetString-on-numeric mapping. The fix no longer reads this column at all.
            INSERT INTO public.""CrcV2_RegisterShortName"" (avatar, ""shortName"") VALUES
                ('" + GroupAddress + @"', " + GroupShortName + @"),
                ('" + HumanAddress + @"', " + HumanShortName + @");";
        await cmd.ExecuteNonQueryAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource != null) await _dataSource.DisposeAsync();
        if (_postgres != null) await _postgres.DisposeAsync();
    }

    /// <summary>
    /// Runs the production SQL and the production reader logic. The group resolves Symbol="gCRC" from
    /// the group's text symbol; the human resolves Symbol="" (no group row). Crucially, neither row
    /// throws — even though both have a numeric short name that the old join would have read as a string.
    /// </summary>
    [Test]
    public async Task V2AvatarInfoSql_SourcesSymbolFromGroupSymbol_AndNeverCrashesOnNumericShortName()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        await using var conn = await _dataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(CirclesRpcModule.V2AvatarInfoSql, conn);
        // UnknownAddress is requested but not seeded in V_CrcV2_Avatars → must simply be absent.
        cmd.Parameters.AddWithValue("addresses",
            new[] { GroupAddress, HumanAddress, NullSymbolGroupAddress, UnknownAddress });
        await using var reader = await cmd.ExecuteReaderAsync();

        var symbolByAvatar = new Dictionary<string, string>();
        var typeByAvatar = new Dictionary<string, string>();
        while (await reader.ReadAsync())
        {
            // Mirror FetchV2AvatarsAsync exactly: column order avatar(0), name(1), type(2), symbol(3),
            // cidV0Digest(4). GetString(3) on the text symbol column must not throw.
            var avatar = reader.GetString(0);
            typeByAvatar[avatar] = reader.GetString(2);
            symbolByAvatar[avatar] = reader.IsDBNull(3) ? "" : reader.GetString(3);
            if (!reader.IsDBNull(4))
            {
                // Mirrors the production cast; asserts column 4 really is the bytea digest.
                var digest = (byte[])reader.GetValue(4);
                Assert.That(digest, Has.Length.EqualTo(32), "cidV0Digest must be the 32-byte metadata digest.");
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(symbolByAvatar[GroupAddress], Is.EqualTo(GroupSymbol),
                "A group's Symbol must come from CrcV2_RegisterGroup.symbol (matches the head/cache path).");
            Assert.That(typeByAvatar[GroupAddress], Is.EqualTo("CrcV2_RegisterGroup"));
            Assert.That(symbolByAvatar[HumanAddress], Is.EqualTo(""),
                "A non-group avatar has no group symbol; Symbol must be empty, not the numeric short name.");
            Assert.That(typeByAvatar[HumanAddress], Is.EqualTo("CrcV2_RegisterHuman"));
            // A group row whose symbol column is NULL exercises the IsDBNull(3) branch → "".
            Assert.That(symbolByAvatar[NullSymbolGroupAddress], Is.EqualTo(""),
                "A group with a NULL symbol must map to empty string, not throw.");
            Assert.That(typeByAvatar[NullSymbolGroupAddress], Is.EqualTo("CrcV2_RegisterGroup"));
            // An address absent from V_CrcV2_Avatars must simply not appear (no row, no crash).
            Assert.That(symbolByAvatar.ContainsKey(UnknownAddress), Is.False,
                "An unknown address must be absent from the result, not present with a bogus value.");
        });
    }

    /// <summary>
    /// Locks in the root cause: reading the numeric <c>"shortName"</c> column with
    /// <see cref="NpgsqlDataReader.GetString"/> throws <see cref="InvalidCastException"/>. This is the
    /// exact failure the old query produced, and the reason Symbol must never be sourced from it.
    /// </summary>
    [Test]
    public async Task GetStringOnNumericShortName_Throws_DocumentingTheOldCrash()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        await using var conn = await _dataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT ""shortName"" FROM public.""CrcV2_RegisterShortName"" WHERE avatar = @a", conn);
        cmd.Parameters.AddWithValue("a", GroupAddress);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.That(await reader.ReadAsync(), Is.True);

        Assert.Throws<InvalidCastException>(() => reader.GetString(0),
            "Reading a numeric column as a string is the crash class the fix eliminates.");
    }
}
