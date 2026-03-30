using Circles.Cache.Service.Caches;
using Circles.Common;
using FluentAssertions;
using Xunit;

namespace Circles.Cache.Service.Tests;

/// <summary>
/// Tests for the shared <see cref="CirclesInvariants"/> predicates.
/// Verifies that the single source of truth for registration, trust, balance,
/// and group validity behaves correctly and consistently.
/// </summary>
public class CirclesInvariantsTests
{
    private const string Human1 = "0xaaaa";
    private const string Human2 = "0xbbbb";
    private const string Org1 = "0xcccc";
    private const string Group1 = "0xdddd";
    private const string Group2 = "0xeeee"; // non-router group
    private const string Unregistered = "0xffff";
    private const string Wrapper1 = "0x1111";
    private const string RouterGroup1Mint = "0xcdfc5135aec0afbf102c108e7f5c8a88c6112842";

    private static readonly HashSet<string> Avatars = new() { Human1, Human2, Org1 };
    private static readonly HashSet<string> Groups = new() { Group1, Group2 };
    private static readonly HashSet<string> RouterGroups = new() { Group1 };

    private static readonly IRegistrationSet Registrations =
        new HashSetRegistrationSet(
            new HashSet<string>(Avatars.Concat(Groups)),
            Groups);

    // --- IsRegisteredAvatar ---

    [Theory]
    [InlineData(Human1, true)]
    [InlineData(Human2, true)]
    [InlineData(Org1, true)]
    [InlineData(Group1, true)]
    [InlineData(Group2, true)]
    [InlineData(Unregistered, false)]
    [InlineData("", false)]
    public void IsRegisteredAvatar_CorrectForAllTypes(string address, bool expected)
    {
        CirclesInvariants.IsRegisteredAvatar(address, Registrations).Should().Be(expected);
    }

    // --- IsValidTrustEdge ---

    [Fact]
    public void IsValidTrustEdge_BothRegistered_NotExpired_NonGroup_ReturnsTrue()
    {
        CirclesInvariants.IsValidTrustEdge(Human1, Human2, 9999, 1000, Registrations)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTrustEdge_ExpiredTrust_ReturnsFalse()
    {
        CirclesInvariants.IsValidTrustEdge(Human1, Human2, 500, 1000, Registrations)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTrustEdge_ZeroExpiry_TreatedAsUntrust()
    {
        // Hub.sol: expiryTime=0 is explicit untrust (or never-set default)
        CirclesInvariants.IsValidTrustEdge(Human1, Human2, 0, 1000, Registrations)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTrustEdge_UnregisteredTruster_ReturnsFalse()
    {
        CirclesInvariants.IsValidTrustEdge(Unregistered, Human2, 9999, 1000, Registrations)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTrustEdge_UnregisteredTrustee_ReturnsFalse()
    {
        CirclesInvariants.IsValidTrustEdge(Human1, Unregistered, 9999, 1000, Registrations)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTrustEdge_GroupAsTruster_ReturnsFalse()
    {
        // Group trusters are excluded — handled separately as group trust edges
        CirclesInvariants.IsValidTrustEdge(Group1, Human1, 9999, 1000, Registrations)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidTrustEdge_GroupAsTrustee_ReturnsTrue()
    {
        // Groups CAN be trustees (someone can trust a group's token)
        CirclesInvariants.IsValidTrustEdge(Human1, Group1, 9999, 1000, Registrations)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidTrustEdge_NonRouterGroupAsTrustee_StillReturnsTrue()
    {
        // Non-router groups are still registered avatars — they can be trustees
        // BUG FIX: Old code checked routerGroups only, missing non-router groups
        CirclesInvariants.IsValidTrustEdge(Human1, Group2, 9999, 1000, Registrations)
            .Should().BeTrue();
    }

    // --- IsValidGroupTrustEdge ---

    [Fact]
    public void IsValidGroupTrustEdge_RouterGroup_RegisteredTrustee_ReturnsTrue()
    {
        CirclesInvariants.IsValidGroupTrustEdge(Group1, Human1, 9999, 1000, Registrations, RouterGroups)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidGroupTrustEdge_NonRouterGroup_ReturnsFalse()
    {
        CirclesInvariants.IsValidGroupTrustEdge(Group2, Human1, 9999, 1000, Registrations, RouterGroups)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidGroupTrustEdge_UnregisteredTrustee_ReturnsFalse()
    {
        CirclesInvariants.IsValidGroupTrustEdge(Group1, Unregistered, 9999, 1000, Registrations, RouterGroups)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidGroupTrustEdge_Expired_ReturnsFalse()
    {
        CirclesInvariants.IsValidGroupTrustEdge(Group1, Human1, 500, 1000, Registrations, RouterGroups)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidGroupTrustEdge_ZeroExpiry_TreatedAsUntrust()
    {
        // Hub.sol: expiryTime=0 is explicit untrust / never-set
        CirclesInvariants.IsValidGroupTrustEdge(Group1, Human1, 0, 1000, Registrations, RouterGroups)
            .Should().BeFalse();
    }

    // --- IsValidBalance ---

    [Fact]
    public void IsValidBalance_NativeToken_BothRegistered_ReturnsTrue()
    {
        // Native ERC1155: tokenAddress = token owner
        CirclesInvariants.IsValidBalance(Human1, Human2, Registrations, null)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidBalance_NativeToken_UnregisteredAccount_ReturnsFalse()
    {
        CirclesInvariants.IsValidBalance(Unregistered, Human1, Registrations, null)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidBalance_NativeToken_UnregisteredTokenOwner_ReturnsFalse()
    {
        CirclesInvariants.IsValidBalance(Human1, Unregistered, Registrations, null)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidBalance_WrapperToken_RegisteredUnderlying_ReturnsTrue()
    {
        var wrapperLookup = new TestWrapperLookup(new Dictionary<string, string> { [Wrapper1] = Human2 });

        CirclesInvariants.IsValidBalance(Human1, Wrapper1, Registrations, wrapperLookup)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidBalance_WrapperToken_UnregisteredUnderlying_ReturnsFalse()
    {
        var wrapperLookup = new TestWrapperLookup(new Dictionary<string, string> { [Wrapper1] = Unregistered });

        CirclesInvariants.IsValidBalance(Human1, Wrapper1, Registrations, wrapperLookup)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidBalance_GroupToken_ReturnsTrue()
    {
        // Groups can be token owners (group token held by a human)
        CirclesInvariants.IsValidBalance(Human1, Group1, Registrations, null)
            .Should().BeTrue();
    }

    // --- IsValidWrapperMapping ---

    [Fact]
    public void IsValidWrapperMapping_RegisteredAvatar_ReturnsTrue()
    {
        CirclesInvariants.IsValidWrapperMapping(Human1, Registrations).Should().BeTrue();
    }

    [Fact]
    public void IsValidWrapperMapping_UnregisteredAvatar_ReturnsFalse()
    {
        CirclesInvariants.IsValidWrapperMapping(Unregistered, Registrations).Should().BeFalse();
    }

    // --- IsValidGroupMembership ---

    [Fact]
    public void IsValidGroupMembership_RegisteredGroupAndMember_ReturnsTrue()
    {
        CirclesInvariants.IsValidGroupMembership(Group1, Human1, 9999, 1000, Registrations)
            .Should().BeTrue();
    }

    [Fact]
    public void IsValidGroupMembership_UnregisteredMember_ReturnsFalse()
    {
        CirclesInvariants.IsValidGroupMembership(Group1, Unregistered, 9999, 1000, Registrations)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidGroupMembership_NonGroupAddress_ReturnsFalse()
    {
        // Human address as "group" — not a group
        CirclesInvariants.IsValidGroupMembership(Human1, Human2, 9999, 1000, Registrations)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidGroupMembership_ZeroExpiry_TreatedAsRevoked()
    {
        // Hub.sol: expiryTime=0 is revoked / never-set
        CirclesInvariants.IsValidGroupMembership(Group1, Human1, 0, 1000, Registrations)
            .Should().BeFalse();
    }

    [Fact]
    public void IsValidGroupMembership_Expired_ReturnsFalse()
    {
        CirclesInvariants.IsValidGroupMembership(Group1, Human1, 500, 1000, Registrations)
            .Should().BeFalse();
    }

    // --- IsValidConsentedFlowFlag ---

    [Fact]
    public void IsValidConsentedFlowFlag_RegisteredAvatar_ReturnsTrue()
    {
        CirclesInvariants.IsValidConsentedFlowFlag(Human1, Registrations).Should().BeTrue();
    }

    [Fact]
    public void IsValidConsentedFlowFlag_UnregisteredAvatar_ReturnsFalse()
    {
        CirclesInvariants.IsValidConsentedFlowFlag(Unregistered, Registrations).Should().BeFalse();
    }

    // --- CacheRegistrationSet adapter ---

    [Fact]
    public void CacheRegistrationSet_IncludesHumansOrgsAndGroups()
    {
        var caches = new CacheContainer(rollbackCapacity: 4);
        caches.V2Avatars.Add(1, Human1, ("Human", 100));
        caches.V2Avatars.Add(1, Org1, ("Organization", 100));
        caches.Groups.Add(1, Group1, ("TestGroup", RouterGroup1Mint, "TG"));

        var registrations = new CacheRegistrationSet(caches);

        registrations.IsRegistered(Human1).Should().BeTrue("humans are in V2Avatars");
        registrations.IsRegistered(Org1).Should().BeTrue("orgs are in V2Avatars");
        registrations.IsRegistered(Group1).Should().BeTrue("groups are in Groups cache");
        registrations.IsRegistered(Unregistered).Should().BeFalse();

        registrations.IsGroup(Group1).Should().BeTrue();
        registrations.IsGroup(Human1).Should().BeFalse();
    }

    // --- CacheWrapperLookup adapter ---

    [Fact]
    public void CacheWrapperLookup_ResolvesWrapperToAvatar()
    {
        var caches = new CacheContainer(rollbackCapacity: 4);
        caches.UpsertWrapper(1, Wrapper1, Human1, 0);

        var lookup = new CacheWrapperLookup(caches);

        lookup.TryGetUnderlyingAvatar(Wrapper1, out var avatar).Should().BeTrue();
        avatar.Should().Be(Human1.ToLowerInvariant());
    }

    [Fact]
    public void CacheWrapperLookup_ReturnsFalseForNonWrapper()
    {
        var caches = new CacheContainer(rollbackCapacity: 4);
        var lookup = new CacheWrapperLookup(caches);

        lookup.TryGetUnderlyingAvatar(Human1, out _).Should().BeFalse();
    }

    // --- Helper ---

    private class TestWrapperLookup : IWrapperLookup
    {
        private readonly Dictionary<string, string> _mappings;
        public TestWrapperLookup(Dictionary<string, string> mappings) => _mappings = mappings;

        public bool TryGetUnderlyingAvatar(string tokenAddress, out string underlyingAvatar)
        {
            if (_mappings.TryGetValue(tokenAddress, out var avatar))
            {
                underlyingAvatar = avatar;
                return true;
            }
            underlyingAvatar = string.Empty;
            return false;
        }
    }
}
