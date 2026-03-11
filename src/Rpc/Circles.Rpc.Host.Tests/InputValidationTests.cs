using Circles.Rpc.Host;

namespace Circles.Rpc.Host.Tests;

/// <summary>
/// Tests for input validation edge cases across the RPC module.
/// These tests verify boundary conditions without requiring a database.
/// </summary>
[TestFixture]
public class InputValidationTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // Cursor validation (BUG-6: silent failure on malformed cursor)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void CursorDecode_ValidBase64ButSqlInjection_DoesNotParse()
    {
        // Attacker tries to inject SQL via cursor
        var malicious = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("1; DROP TABLE users;--:1:1"));
        var (block, tx, log) = CursorUtils.DecodeCursor(malicious);

        // long.Parse should fail on "1; DROP TABLE users;--"
        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    [Test]
    public void CursorDecode_FloatingPointValues_DoesNotParse()
    {
        var cursor = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("1.5:2.5:3.5"));
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.Null);
            Assert.That(tx, Is.Null);
            Assert.That(log, Is.Null);
        });
    }

    [Test]
    public void CursorDecode_ExtraColonSeparatedParts_StillParses()
    {
        // "1:2:3:extra:stuff" — has >= 3 parts, so should parse first 3
        var cursor = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("100:5:2:extra:stuff"));
        var (block, tx, log) = CursorUtils.DecodeCursor(cursor);

        Assert.Multiple(() =>
        {
            Assert.That(block, Is.EqualTo(100));
            Assert.That(tx, Is.EqualTo(5));
            Assert.That(log, Is.EqualTo(2));
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // BUG-1 regression: avatar type mapping
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void AvatarTypeMapping_DbFullNames_PassThrough()
    {
        // The V_CrcV2_Avatars view stores full event names.
        // These should pass through unchanged (already canonical).
        var fullNames = new[] { "CrcV2_RegisterHuman", "CrcV2_RegisterOrganization", "CrcV2_RegisterGroup" };

        foreach (var fullName in fullNames)
        {
            var result = CirclesRpcModule.NormalizeAvatarType(fullName);
            Assert.That(result, Is.EqualTo(fullName),
                $"Full DB name '{fullName}' should pass through unchanged");
        }
    }

    [Test]
    public void AvatarTypeMapping_LegacyShortNames_MapToFullNames()
    {
        // Legacy safety net: short names map to V2 full names.
        // Cache now stores full names, but mapping preserved for backward compatibility.
        var mappings = new Dictionary<string, string>
        {
            { "Human", "CrcV2_RegisterHuman" },
            { "Organization", "CrcV2_RegisterOrganization" },
            { "Group", "CrcV2_RegisterGroup" },
        };

        foreach (var (shortName, expectedFull) in mappings)
        {
            var result = CirclesRpcModule.NormalizeAvatarType(shortName);
            Assert.That(result, Is.EqualTo(expectedFull),
                $"Legacy short name '{shortName}' should normalize to '{expectedFull}'");
        }
    }

    [Test]
    public void AvatarTypeMapping_V1FullNames_PassThrough()
    {
        // V1 full type names should pass through unchanged.
        var v1Names = new[] { "CrcV1_Signup", "CrcV1_OrganizationSignup" };

        foreach (var name in v1Names)
        {
            var result = CirclesRpcModule.NormalizeAvatarType(name);
            Assert.That(result, Is.EqualTo(name),
                $"V1 full name '{name}' should pass through unchanged");
        }
    }

    [Test]
    public void AvatarTypeMapping_UnknownType_ReturnsPassthrough()
    {
        // Unknown types pass through (not null — NormalizeAvatarType returns the input or "Unknown")
        Assert.That(CirclesRpcModule.NormalizeAvatarType("SomethingElse"), Is.EqualTo("SomethingElse"));
        Assert.That(CirclesRpcModule.NormalizeAvatarType(null), Is.EqualTo("Unknown"));
        Assert.That(CirclesRpcModule.NormalizeAvatarType(""), Is.EqualTo(""));
    }
}
