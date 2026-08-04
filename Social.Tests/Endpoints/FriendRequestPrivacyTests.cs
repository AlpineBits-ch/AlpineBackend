using System.Security.Claims;
using Identity.Contracts.Bus.Response;
using Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Social.Api.Dtos.Request;
using Social.Api.Dtos.Response;
using Social.Api.Endpoints;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Endpoints;

/// <summary>Shared assertions so the "every refusal looks the same" property is asserted the same
/// way everywhere it is claimed.</summary>
internal static class FriendRequestPrivacyAssertions
{
    public static void AssertPolicyRefusal(IResult result) => AssertRefusal(result, RefusalCodes.FriendRequestPolicy);

    public static void AssertRefusal(IResult result, string expectedCode)
    {
        Assert.That(result, Is.InstanceOf<JsonHttpResult<RefusalDto>>());
        var json = (JsonHttpResult<RefusalDto>)result;
        Assert.Multiple(() =>
        {
            Assert.That(json.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            Assert.That(json.Value!.Code, Is.EqualTo(expectedCode));
        });
    }
}

/// <summary>
/// Privacy enforcement on <c>POST /api/v1/relationships</c>: blocking (T0-3), the friend-request
/// policy table (T2-15) and username discoverability (T2-16).
///
/// <para>The negative cases are the point, and so is their <i>sameness</i>: several tests here
/// assert the identical 403 body for very different underlying reasons, because a caller able to
/// tell them apart has an enumeration oracle and a block detector.</para>
/// </summary>
[TestFixture]
public class FriendRequestPrivacyTests
{
    private TestSocialContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;
    private ISharedGuildResolver _sharedGuilds = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _bus = new FakeMessageBus();
        _sharedGuilds = new NoSharedGuildResolver();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static ClaimsPrincipal MakeUser(string userId) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private async Task<Profile> AddProfile(string userId, string userName)
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = userId, Username = userName });
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();
        return profile;
    }

    private async Task<Relationship> AddRelationship(Profile owner, Profile target, RelationshipStatus status)
    {
        var row = new Relationship
        {
            Id = Relationship.GenerateId(),
            OwnerId = owner.Id,
            TargetId = target.Id,
            Status = status,
        };
        _context.Relationships.Add(row);
        await _context.SaveChangesAsync();
        return row;
    }

    private PrivacySettingsCache PrivacyFor(params UserPrivacySettingsSummary[] settings) =>
        PrivacyTestHelpers.CacheReturning(_cache, _bus, settings);

    private Task<IResult> RequestAsync(string callerUserId, string targetUserName, PrivacySettingsCache privacy) =>
        FriendshipEndpoints.CreateAsync(
            new CreateFriendshipDto { UserName = targetUserName }, _context,
            PrivacyTestHelpers.Directory(_context, privacy), _sharedGuilds, MakeUser(callerUserId));

    // ── T2-15 policy table ───────────────────────────────────────────────────

    [Test]
    public async Task Everyone_AdmitsAStranger()
    {
        await AddProfile("user-a", "initiator");
        await AddProfile("user-b", "target");
        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.FriendRequestPolicy = FriendRequestPolicy.Everyone;

        var result = await RequestAsync("user-a", "target", PrivacyFor(settings));

        Assert.That(result, Is.InstanceOf<Ok>());
    }

    [Test]
    public async Task Nobody_RefusesEvenAMutualFriend()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        var mutual = await AddProfile("user-c", "mutual");
        await AddRelationship(initiator, mutual, RelationshipStatus.Friends);
        await AddRelationship(target, mutual, RelationshipStatus.Friends);

        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.FriendRequestPolicy = FriendRequestPolicy.Nobody;

        var result = await RequestAsync("user-a", "target", PrivacyFor(settings));

        FriendRequestPrivacyAssertions.AssertPolicyRefusal(result);
        Assert.That(_context.Relationships.Count(r => r.OwnerId == initiator.Id && r.TargetId == target.Id), Is.Zero);
    }

    [Test]
    public async Task FriendsOfFriends_AdmitsWhenAMutualFriendExists()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        var mutual = await AddProfile("user-c", "mutual");
        await AddRelationship(initiator, mutual, RelationshipStatus.Friends);
        await AddRelationship(target, mutual, RelationshipStatus.Friends);

        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.FriendRequestPolicy = FriendRequestPolicy.FriendsOfFriends;

        var result = await RequestAsync("user-a", "target", PrivacyFor(settings));

        Assert.That(result, Is.InstanceOf<Ok>());
    }

    [Test]
    public async Task FriendsOfFriends_RefusesWithNoMutualFriend()
    {
        await AddProfile("user-a", "initiator");
        await AddProfile("user-b", "target");

        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.FriendRequestPolicy = FriendRequestPolicy.FriendsOfFriends;

        var result = await RequestAsync("user-a", "target", PrivacyFor(settings));

        FriendRequestPrivacyAssertions.AssertPolicyRefusal(result);
    }

    [Test]
    public async Task FriendsOfFriends_APendingRequestIsNotAMutualFriend()
    {
        // Edge: a pending request is not a friendship, so it must not satisfy the policy - other-
        // wise anybody could manufacture "friend of a friend" by spraying requests at the target's
        // contacts.
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        var almost = await AddProfile("user-c", "almost");
        await AddRelationship(initiator, almost, RelationshipStatus.PendingOutgoing);
        await AddRelationship(target, almost, RelationshipStatus.Friends);

        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.FriendRequestPolicy = FriendRequestPolicy.FriendsOfFriends;

        var result = await RequestAsync("user-a", "target", PrivacyFor(settings));

        FriendRequestPrivacyAssertions.AssertPolicyRefusal(result);
    }

    [Test]
    public async Task ServerMembers_AdmitsWhenTheyShareAGuild()
    {
        await AddProfile("user-a", "initiator");
        await AddProfile("user-b", "target");
        _sharedGuilds = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-b", ["guld_1"]));

        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.FriendRequestPolicy = FriendRequestPolicy.ServerMembers;

        var result = await RequestAsync("user-a", "target", PrivacyFor(settings));

        Assert.That(result, Is.InstanceOf<Ok>());
    }

    [Test]
    public async Task ServerMembers_RefusesWhenTheyShareNoGuild()
    {
        // Guild omits a pair with nothing in common rather than returning an empty list, so the
        // absent entry is what "no shared guild" looks like on the wire.
        await AddProfile("user-a", "initiator");
        await AddProfile("user-b", "target");
        _sharedGuilds = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-someone-else", ["guld_1"]));

        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.FriendRequestPolicy = FriendRequestPolicy.ServerMembers;

        var result = await RequestAsync("user-a", "target", PrivacyFor(settings));

        FriendRequestPrivacyAssertions.AssertPolicyRefusal(result);
    }

    [Test]
    public async Task ServerMembers_RefusesWhenTheSharedGuildLookupFails()
    {
        // A Guild outage must not become "allow". No registered answer => FakeMessageBus throws,
        // which the resolver absorbs into "shares nothing".
        await AddProfile("user-a", "initiator");
        await AddProfile("user-b", "target");
        _sharedGuilds = PrivacyTestHelpers.SharedGuilds(_bus);

        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.FriendRequestPolicy = FriendRequestPolicy.ServerMembers;

        // The privacy lookup is answered (PrivacyFor registers it), so the only failing call is the
        // shared-guild one - the refusal has to be coming from there.
        var result = await RequestAsync("user-a", "target", PrivacyFor(settings));

        FriendRequestPrivacyAssertions.AssertPolicyRefusal(result);
    }

    // ── T2-16 discoverability ────────────────────────────────────────────────

    [Test]
    public async Task NonDiscoverableUsername_RefusesIdenticallyToAMissingUser()
    {
        await AddProfile("user-a", "initiator");
        await AddProfile("user-b", "target");

        var settings = PrivacyTestHelpers.Defaults("user-b");
        settings.DiscoverableByUsername = false;

        var hidden = await RequestAsync("user-a", "target", PrivacyFor(settings));
        var absent = await RequestAsync("user-a", "nobody-holds-this", PrivacyFor(settings));

        FriendRequestPrivacyAssertions.AssertPolicyRefusal(hidden);
        FriendRequestPrivacyAssertions.AssertPolicyRefusal(absent);

        var hiddenJson = (JsonHttpResult<RefusalDto>)hidden;
        var absentJson = (JsonHttpResult<RefusalDto>)absent;
        Assert.Multiple(() =>
        {
            Assert.That(hiddenJson.StatusCode, Is.EqualTo(absentJson.StatusCode));
            Assert.That(hiddenJson.Value!.Code, Is.EqualTo(absentJson.Value!.Code),
                "a non-discoverable user and a non-existent one must be indistinguishable");
        });
    }

    // ── T0-3 blocking ────────────────────────────────────────────────────────

    [Test]
    public async Task TargetHasBlockedInitiator_RefusesWithThePolicyCodeNotTheBlockCode()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        await AddRelationship(target, initiator, RelationshipStatus.Blocked);

        var result = await RequestAsync("user-a", "target", PrivacyFor(PrivacyTestHelpers.Defaults("user-b")));

        // Naming the block here would hand the blocked user a block detector, which is precisely
        // what "invisible to the blocked party" forbids.
        FriendRequestPrivacyAssertions.AssertRefusal(result, RefusalCodes.FriendRequestPolicy);
    }

    [Test]
    public async Task InitiatorHasBlockedTarget_IsToldSoBecauseTheyAreTheBlocker()
    {
        var initiator = await AddProfile("user-a", "initiator");
        var target = await AddProfile("user-b", "target");
        await AddRelationship(initiator, target, RelationshipStatus.Blocked);

        var result = await RequestAsync("user-a", "target", PrivacyFor(PrivacyTestHelpers.Defaults("user-b")));

        FriendRequestPrivacyAssertions.AssertRefusal(result, RefusalCodes.Blocked);
    }

    // ── fail-closed ──────────────────────────────────────────────────────────

    [Test]
    public async Task PrivacyLookupFailure_RefusesRatherThanFallingBackToEveryone()
    {
        await AddProfile("user-a", "initiator");
        await AddProfile("user-b", "target");

        // No registered bus answer: FakeMessageBus throws, standing in for Identity being down.
        var failing = PrivacyTestHelpers.FailingCache(_cache, _bus);

        var result = await RequestAsync("user-a", "target", failing);

        FriendRequestPrivacyAssertions.AssertPolicyRefusal(result);
    }
}
