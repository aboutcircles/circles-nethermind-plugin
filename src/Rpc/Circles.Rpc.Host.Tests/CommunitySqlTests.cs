using System.Globalization;
using System.Reflection;
using Npgsql;
using NUnit.Framework;
using Testcontainers.PostgreSql;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// DB-level tests for the community (multi-affiliate-group) feature, run against a real Postgres
/// (Testcontainers). The underlying DB objects keep the contract-derived "affiliate" name.
///
/// Validates the parts that carry the logic:
///  • the <c>V_CrcV2_AffiliateGroupMembers</c> view (loaded verbatim from its embedded resource, so the
///    latest-event-wins / re-add / remove semantics are tested with zero drift), and
///  • the trusted-subset filter and the <c>membershipFee</c> jsonb fee aggregation that the RPC methods in
///    <c>CirclesRpcModule.Community.cs</c> apply on top of the view.
///
/// These do NOT construct <see cref="CirclesRpcModule"/> — that needs the Nethermind runtime, which is
/// absent in CI (see CirclesRpcModuleTests). The SQL below mirrors the method bodies; the view is the
/// production resource.
/// </summary>
[TestFixture]
[Category("RequiresDocker")]
public class CommunitySqlTests
{
    private const string Avatar = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AvatarB = "0xbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"; // self-adds G1,G2 then seeded with G3
    private const string G1 = "0x1111111111111111111111111111111111111111"; // trusts A, fee 10%
    private const string G2 = "0x2222222222222222222222222222222222222222"; // NOT trusted, fee 30%
    private const string G3 = "0x3333333333333333333333333333333333333333"; // trusts A, no fee
    private const string G4 = "0x4444444444444444444444444444444444444444"; // added then removed → gone

    // Mirrors LoadAvatarCommunityRowsAsync in CirclesRpcModule.Community.cs. {0} is the trusted-only join.
    private const string GroupRowsSql = @"
        SELECT m.""affiliateGroup"", g.name AS group_name,
               f.payload->'membershipCriteria'->>'membershipFee' AS membership_fee, m.""timestamp""
        FROM ""V_CrcV2_AffiliateGroupMembers"" m
        {0}
        LEFT JOIN ""CrcV2_RegisterGroup"" g ON g.""group"" = m.""affiliateGroup""
        LEFT JOIN ""V_CrcV2_Avatars"" a ON a.avatar = m.""affiliateGroup""
        LEFT JOIN ipfs_files f ON f.metadata_digest = a.""cidV0Digest""
        WHERE m.avatar = @avatar
        ORDER BY m.""timestamp"" DESC, m.""affiliateGroup""";

    // Mirrors GetCommunityMembersInternal in CirclesRpcModule.Community.cs: a MATERIALIZED page
    // (filter + order + limit off the view) enriched with names scoped to that page's avatars. {0} is
    // the trusted-only join. (LIMIT is a constant here; the method binds @limit.)
    private const string MembersSql = @"
        WITH page AS MATERIALIZED (
            SELECT m.""blockNumber"", m.""timestamp"", m.""transactionIndex"", m.""logIndex"", m.avatar
            FROM ""V_CrcV2_AffiliateGroupMembers"" m
            {0}
            WHERE m.""affiliateGroup"" = @group
            ORDER BY m.""blockNumber"" DESC, m.""transactionIndex"" DESC, m.""logIndex"" DESC
            LIMIT 1000
        ),
        names AS (
            SELECT a.avatar, f.payload->>'name' AS avatar_name
            FROM ""V_CrcV2_Avatars"" a
            LEFT JOIN ipfs_files f ON f.metadata_digest = a.""cidV0Digest""
            WHERE a.avatar = ANY(ARRAY(SELECT avatar FROM page))
        )
        SELECT p.avatar, n.avatar_name
        FROM page p
        LEFT JOIN names n ON n.avatar = p.avatar
        ORDER BY p.""blockNumber"" DESC, p.""transactionIndex"" DESC, p.""logIndex"" DESC";

    private const string TrustJoin =
        @"INNER JOIN ""V_CrcV2_TrustRelations"" t ON t.truster = m.""affiliateGroup"" AND t.trustee = m.avatar";

    private const string TrustJoinMembers = TrustJoin; // same predicate, group side fixed by WHERE

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
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaAndSeedSql()
                + LoadViewSql("V_CrcV2_AffiliateGroupMembers")
                + LoadViewSql("V_CrcV2_AffiliateGroupSeedConflicts");
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_dataSource != null) await _dataSource.DisposeAsync();
        if (_postgres != null) await _postgres.DisposeAsync();
    }

    /// <summary>The view itself: A's current wishlist is {G1,G2,G3} (re-added G2 wins; removed G4 is gone).</summary>
    [Test]
    public async Task View_ReflectsLatestEventWins_ReAddPresent_RemoveAbsent()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        var groups = await SelectGroupAddresses(trustedOnly: false);
        Assert.That(groups, Is.EquivalentTo(new[] { G1, G2, G3 }),
            "Wishlist = latest-event-wins per (avatar,group): G2 re-added is present, G4 removed is absent.");
    }

    /// <summary>Trusted subset only keeps groups that currently trust the avatar: {G1,G3} (not G2).</summary>
    [Test]
    public async Task TrustedSubset_KeepsOnlyGroupsThatTrustTheAvatar()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        var groups = await SelectGroupAddresses(trustedOnly: true);
        Assert.That(groups, Is.EquivalentTo(new[] { G1, G3 }),
            "Confirmed membership requires the group to trust the avatar; G2 (untrusted) is excluded.");
    }

    /// <summary>totalFeePercentage sums membershipFee (percent) over the set; a missing fee contributes 0.</summary>
    [Test]
    public async Task FeeSum_WishlistIs40_TrustedIs10_NullFeeCountsAsZero()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        var (wishlistRows, wishlistTotal) = await SelectGroupRows(trustedOnly: false);
        var (_, trustedTotal) = await SelectGroupRows(trustedOnly: true);

        Assert.Multiple(() =>
        {
            // Wishlist: G1 10 + G2 30 + G3 null(→0) = 40
            Assert.That(wishlistTotal, Is.EqualTo(40m));
            Assert.That(FeeOf(wishlistRows, G3), Is.Null, "G3 has no membershipCriteria in its profile → null.");
            Assert.That(FeeOf(wishlistRows, G1), Is.EqualTo(10m));
            // Trusted: G1 10 + G3 null(→0) = 10
            Assert.That(trustedTotal, Is.EqualTo(10m));
        });
    }

    /// <summary>Per-group members: G1's wishlist + trusted both contain A (name resolved from profile);
    /// G2's wishlist contains A but its trusted set is empty (G2 does not trust A).</summary>
    [Test]
    public async Task Members_WishlistVsTrusted_PerGroup()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        var g1Wishlist = await SelectMembers(G1, trustedOnly: false);
        var g1Trusted = await SelectMembers(G1, trustedOnly: true);
        var g2Wishlist = await SelectMembers(G2, trustedOnly: false);
        var g2Trusted = await SelectMembers(G2, trustedOnly: true);

        Assert.Multiple(() =>
        {
            Assert.That(g1Wishlist.Select(m => m.Avatar), Is.EquivalentTo(new[] { Avatar }));
            Assert.That(g1Wishlist[0].Name, Is.EqualTo("alice"), "Member name resolves from the avatar's profile.");
            Assert.That(g1Trusted.Select(m => m.Avatar), Is.EquivalentTo(new[] { Avatar }));
            Assert.That(g2Wishlist.Select(m => m.Avatar), Is.EquivalentTo(new[] { Avatar }));
            Assert.That(g2Trusted, Is.Empty, "G2 does not trust A → empty trusted-members set.");
        });
    }

    /// <summary>An initialize() seed overwrites the avatar's list to the seeded group; pre-seed
    /// self-adds are dropped (matches the on-chain linked list, even though no Removed was emitted).</summary>
    [Test]
    public async Task View_SeedReset_CollapsesAvatarToSeededGroupOnly()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        var groups = await SelectGroupAddresses(trustedOnly: false, avatar: AvatarB);
        Assert.That(groups, Is.EquivalentTo(new[] { G3 }),
            "B self-added G1+G2, then was seeded with G3; the seed resets the list to {G3}.");
    }

    /// <summary>The safety-net view flags an avatar seeded over prior willingness (and not a clean avatar).</summary>
    [Test]
    public async Task SeedConflicts_FlagsAvatarSeededOverPriorWillingness()
    {
        if (_dataSource == null) Assert.Ignore("No data source (container unavailable).");

        var conflicts = await SelectSeedConflicts();

        var b = conflicts.Single(c => c.Avatar == AvatarB);
        Assert.Multiple(() =>
        {
            Assert.That(b.SeedBlock, Is.EqualTo(60));
            Assert.That(b.PriorEvents, Is.EqualTo(2), "B had 2 self-adds (G1,G2) before the seed.");
            Assert.That(conflicts.Any(c => c.Avatar == Avatar), Is.False,
                "A was never seeded → must not be flagged as a conflict.");
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<List<string>> SelectGroupAddresses(bool trustedOnly, string? avatar = null)
    {
        var (rows, _) = await SelectGroupRows(trustedOnly, avatar);
        return rows.Select(r => r.Group).ToList();
    }

    private async Task<(List<(string Group, decimal? Fee)> Rows, decimal Total)> SelectGroupRows(bool trustedOnly, string? avatar = null)
    {
        var sql = string.Format(GroupRowsSql, trustedOnly ? TrustJoin : "");
        await using var conn = await _dataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("avatar", avatar ?? Avatar);

        var rows = new List<(string, decimal?)>();
        decimal total = 0m;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var group = reader.GetString(0);
            var feeRaw = reader.IsDBNull(2) ? null : reader.GetString(2);
            decimal? fee = feeRaw != null &&
                           decimal.TryParse(feeRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var f)
                ? f
                : null;
            total += fee ?? 0m;
            rows.Add((group, fee));
        }
        return (rows, total);
    }

    private async Task<List<(string Avatar, string? Name)>> SelectMembers(string group, bool trustedOnly)
    {
        var sql = string.Format(MembersSql, trustedOnly ? TrustJoinMembers : "");
        await using var conn = await _dataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("group", group);

        var rows = new List<(string, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    private async Task<List<(string Avatar, long SeedBlock, int PriorEvents)>> SelectSeedConflicts()
    {
        await using var conn = await _dataSource!.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT avatar, ""seedBlock"", ""priorEventCount"" FROM ""V_CrcV2_AffiliateGroupSeedConflicts""", conn);
        var rows = new List<(string, long, int)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
        return rows;
    }

    private static decimal? FeeOf(IEnumerable<(string Group, decimal? Fee)> rows, string group) =>
        rows.First(r => r.Group == group).Fee;

    private static string LoadViewSql(string viewName)
    {
        var assembly = typeof(Circles.Index.CirclesViews.DatabaseSchema).Assembly;
        var resource = $"Circles.Index.CirclesViews.queries.{viewName}.sql";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded view SQL not found: {resource}");
        using var reader = new StreamReader(stream);
        return "\n" + reader.ReadToEnd() + ";\n";
    }

    private static string SchemaAndSeedSql() => $@"
        CREATE TABLE public.""CrcV2_AffiliateGroupAdded"" (
            ""blockNumber"" bigint, ""timestamp"" bigint, ""transactionIndex"" int, ""logIndex"" int,
            ""transactionHash"" text, emitter text, ""affiliateGroup"" text, avatar text, ""isSeed"" boolean);
        CREATE TABLE public.""CrcV2_AffiliateGroupRemoved"" (LIKE public.""CrcV2_AffiliateGroupAdded"");
        CREATE TABLE public.""CrcV2_RegisterGroup"" (""group"" text, name text, symbol text);
        CREATE TABLE public.""V_CrcV2_Avatars"" (avatar text, name text, type text, ""cidV0Digest"" bytea);
        CREATE TABLE public.ipfs_files (cid text, metadata_digest bytea, payload jsonb);
        CREATE TABLE public.""V_CrcV2_TrustRelations"" (truster text, trustee text);

        -- A's events (all self-adds, isSeed=false): add G1,G2,G3,G4; remove G4; remove then re-add G2.
        -- B's events: self-add G1,G2 then a deployer initialize() SEED of G3 (isSeed=true) that overwrites.
        INSERT INTO public.""CrcV2_AffiliateGroupAdded""
            (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",emitter,""affiliateGroup"",avatar,""isSeed"") VALUES
            (10,1000,0,0,'0x01','0xreg','{G1}','{Avatar}',false),
            (11,1100,0,0,'0x02','0xreg','{G2}','{Avatar}',false),
            (12,1200,0,0,'0x03','0xreg','{G3}','{Avatar}',false),
            (13,1300,0,0,'0x04','0xreg','{G4}','{Avatar}',false),
            (16,1600,0,0,'0x07','0xreg','{G2}','{Avatar}',false),
            (50,5000,0,0,'0x10','0xreg','{G1}','{AvatarB}',false),
            (51,5100,0,0,'0x11','0xreg','{G2}','{AvatarB}',false),
            (60,6000,0,0,'0x12','0xdeployer','{G3}','{AvatarB}',true);
        INSERT INTO public.""CrcV2_AffiliateGroupRemoved""
            (""blockNumber"",""timestamp"",""transactionIndex"",""logIndex"",""transactionHash"",emitter,""affiliateGroup"",avatar) VALUES
            (14,1400,0,0,'0x05','0xreg','{G4}','{Avatar}'),
            (15,1500,0,0,'0x06','0xreg','{G2}','{Avatar}');

        INSERT INTO public.""CrcV2_RegisterGroup"" (""group"",name,symbol) VALUES
            ('{G1}','Group One','g1'),('{G2}','Group Two','g2'),('{G3}','Group Three','g3');

        INSERT INTO public.""V_CrcV2_Avatars"" (avatar,name,type,""cidV0Digest"") VALUES
            ('{Avatar}','alice','CrcV2_RegisterHuman',decode('aa','hex')),
            ('{G1}','Group One','CrcV2_RegisterGroup',decode('d1','hex')),
            ('{G2}','Group Two','CrcV2_RegisterGroup',decode('d2','hex')),
            ('{G3}','Group Three','CrcV2_RegisterGroup',decode('d3','hex'));

        -- membershipFee is nested under membershipCriteria and is a percent in [0,100] (real schema).
        -- G3 carries no membershipCriteria → fee resolves to null → contributes 0.
        INSERT INTO public.ipfs_files (cid,metadata_digest,payload) VALUES
            ('ca',decode('aa','hex'),'{{""name"":""alice""}}'::jsonb),
            ('c1',decode('d1','hex'),'{{""name"":""Group One"",""membershipCriteria"":{{""membershipFee"":10}}}}'::jsonb),
            ('c2',decode('d2','hex'),'{{""name"":""Group Two"",""membershipCriteria"":{{""membershipFee"":30}}}}'::jsonb),
            ('c3',decode('d3','hex'),'{{""name"":""Group Three""}}'::jsonb);

        -- Only G1 and G3 trust A.
        INSERT INTO public.""V_CrcV2_TrustRelations"" (truster,trustee) VALUES
            ('{G1}','{Avatar}'),('{G3}','{Avatar}');
    ";
}
