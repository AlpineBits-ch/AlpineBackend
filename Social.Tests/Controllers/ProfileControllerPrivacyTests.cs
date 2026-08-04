using System.Security.Claims;
using Identity.Contracts.Bus.Response;
using Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Api.Controllers;
using Social.Api.Dtos.Response;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Controllers;

/// <summary>
/// The privacy gates as they are actually reachable: through <c>GET /api/v1/profiles/{id}</c> and
/// <c>GET /api/v1/profiles/by-user/{id}</c>. Covers T0-5 (Hidden), T2-17 (mutual friends) and T0-3
/// (a blocked reader gets the minimal projection, not a 404).
/// </summary>
[TestFixture]
public class ProfileControllerPrivacyTests
{
    private TestSocialContext _context = null!;
    private FakeMessageBus _bus = null!;
    private FakeDistributedCache _cache = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _bus = new FakeMessageBus();
        _cache = new FakeDistributedCache();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Set before <see cref="MakeController"/> to give the projection a Guild answer;
    /// left null the resolver has none registered, so the bus throws and the fail-closed path runs.
    /// </summary>
    private ISharedGuildResolver? _sharedGuilds;

    /// <summary>Set before <see cref="MakeController"/> to give the projection an Identity answer for
    /// birthday/connections; left null the restrictive stand-in is used, which reports neither.
    /// </summary>
    private IIdentityProfileFactsResolver? _identityFacts;

    private ProfileController MakeController(string userId, params UserPrivacySettingsSummary[] settings)
    {
        var projection = new ProfileProjectionService(
            _context,
            PrivacyTestHelpers.CacheReturning(_cache, _bus, settings),
            _sharedGuilds ?? new NoSharedGuildResolver(),
            _identityFacts ?? new NoIdentityProfileFactsResolver());

        var controller = new ProfileController(_context, NullLogger<ProfileController>.Instance, _bus, projection)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")),
                },
            },
        };
        return controller;
    }

    private async Task<Profile> AddProfile(string userId, string userName, OnlineStatus status = OnlineStatus.Online)
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = userId, Username = userName });
        profile.OnlineStatus = status;
        profile.Bio = "bio";
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();
        return profile;
    }

    private async Task AddRelationship(Profile owner, Profile target, RelationshipStatus status)
    {
        _context.Relationships.Add(new Relationship
        {
            Id = Relationship.GenerateId(),
            OwnerId = owner.Id,
            TargetId = target.Id,
            Status = status,
        });
        await _context.SaveChangesAsync();
    }

    private static ProfileDto Body(IActionResult result) => (ProfileDto)((OkObjectResult)result).Value!;

    // ── T0-5 ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_HiddenSubject_RendersAsOfflineToAnotherUser()
    {
        await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject", OnlineStatus.Hidden);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).OnlineStatus, Is.EqualTo(OnlineStatus.Offline));
    }

    [Test]
    public async Task GetAsync_HiddenSubject_StillSeesHiddenWhenLookingAtThemselves()
    {
        var self = await AddProfile("user-1", "viewer", OnlineStatus.Hidden);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-1")).GetAsync(self.Id);

        Assert.That(Body(result).OnlineStatus, Is.EqualTo(OnlineStatus.Hidden));
    }

    [Test]
    public async Task GetByUserIdAsync_HiddenSubject_RendersAsOffline()
    {
        await AddProfile("user-1", "viewer");
        await AddProfile("user-2", "subject", OnlineStatus.Hidden);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetByUserIdAsync("user-2");

        Assert.That(Body(result).OnlineStatus, Is.EqualTo(OnlineStatus.Offline));
    }

    // ── T2-17 ────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_MutualFriendsVisibleToAFriend()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        var mutual = await AddProfile("user-3", "mutual");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        await AddRelationship(viewer, mutual, RelationshipStatus.Friends);
        await AddRelationship(subject, mutual, RelationshipStatus.Friends);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        var dto = Body(result);
        Assert.That(dto.MutualFriends, Is.Not.Null);
        Assert.That(dto.MutualFriends!.Single().UserName, Is.EqualTo("mutual"));
    }

    [Test]
    public async Task GetAsync_MutualFriendsAbsentForANonFriend()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        var mutual = await AddProfile("user-3", "mutual");
        await AddRelationship(viewer, mutual, RelationshipStatus.Friends);
        await AddRelationship(subject, mutual, RelationshipStatus.Friends);

        // MutualFriendsVisibility defaults to Friends, and the viewer is not one.
        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).MutualFriends, Is.Null);
    }

    [Test]
    public async Task GetAsync_MutualFriendsVisibilityNobody_HidesThemEvenFromAFriend()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        var mutual = await AddProfile("user-3", "mutual");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        await AddRelationship(viewer, mutual, RelationshipStatus.Friends);
        await AddRelationship(subject, mutual, RelationshipStatus.Friends);

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.MutualFriendsVisibility = Visibility.Nobody;

        var result = await MakeController("user-1", settings).GetAsync(subject.Id);

        Assert.That(Body(result).MutualFriends, Is.Null);
    }

    [Test]
    public async Task GetAsync_MutualServersVisibleToAFriend()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        _sharedGuilds = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-2", ["guld_1", "guld_2"]));

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        var dto = Body(result);
        Assert.That(dto.MutualServers, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(dto.MutualServers!.Select(s => s.GuildId), Is.EqualTo(new[] { "guld_1", "guld_2" }));
            Assert.That(dto.MutualServers!.All(s => s.Name is null), Is.True, "ids only - Guild does not project names here");
        });
    }

    [Test]
    public async Task GetAsync_MutualServersAbsentForANonFriend()
    {
        // MutualServersVisibility defaults to Friends. Guild deliberately does not gate on its
        // side, so if this projection did not, co-membership would leak to any authenticated
        // caller - the exact fact the setting exists to control.
        await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        _sharedGuilds = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-2", ["guld_1"]));

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).MutualServers, Is.Null);
    }

    [Test]
    public async Task GetAsync_MutualServersVisibilityEveryone_ExposesThemToAStranger()
    {
        await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        _sharedGuilds = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-2", ["guld_1"]));

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.MutualServersVisibility = Visibility.Everyone;

        var result = await MakeController("user-1", settings).GetAsync(subject.Id);

        Assert.That(Body(result).MutualServers!.Single().GuildId, Is.EqualTo("guld_1"));
    }

    [Test]
    public async Task GetAsync_MutualServersEmptyWhenTheGuildLookupFails()
    {
        // Visible, but Guild is unreachable - the field is present and empty rather than guessed.
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        _sharedGuilds = PrivacyTestHelpers.SharedGuilds(_bus);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).MutualServers, Is.Not.Null.And.Empty);
    }

    [Test]
    public async Task GetAsync_BlockedViewer_NeverGetsMutualServers()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(subject, viewer, RelationshipStatus.Blocked);
        _sharedGuilds = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-2", ["guld_1"]));

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).MutualServers, Is.Null);
    }

    [Test]
    public async Task GetAsync_PrivacyLookupFailure_HidesEveryGatedField()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);

        // No settings registered => FakeMessageBus throws => restrictive defaults.
        var result = await MakeController("user-1").GetAsync(subject.Id);

        var dto = Body(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.MutualFriends, Is.Null);
            Assert.That(dto.MutualServers, Is.Null);
            Assert.That(dto.Connections, Is.Null);
            Assert.That(dto.Birthday, Is.Null);
        });
    }

    // ── T2-17 birthday ───────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_BirthdayAbsentByDefaultEvenForAFriend()
    {
        // BirthdayVisibility ships as Nobody and must stay that way: a full date of birth is
        // identity-theft-grade and it is what the minor floors are computed from.
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-2", new DateOnly(1990, 3, 4)));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).Birthday, Is.Null);
    }

    [Test]
    public async Task GetAsync_BirthdayVisibleToAFriendOnceTheUserOptsIn()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-2", new DateOnly(1990, 3, 4)));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.BirthdayVisibility = Visibility.Friends;

        var result = await MakeController("user-1", settings).GetAsync(subject.Id);

        Assert.That(Body(result).Birthday, Is.EqualTo(new DateOnly(1990, 3, 4)));
    }

    [Test]
    public async Task GetAsync_BirthdayAbsentForAStrangerEvenWhenTheUserSharesItWithFriends()
    {
        await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-2", new DateOnly(1990, 3, 4)));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.BirthdayVisibility = Visibility.Friends;

        var result = await MakeController("user-1", settings).GetAsync(subject.Id);

        Assert.That(Body(result).Birthday, Is.Null, "a hidden field must be absent, not empty");
    }

    [Test]
    public async Task GetAsync_BirthdayAbsentForABlockedViewerWhateverTheSettingSays()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(subject, viewer, RelationshipStatus.Blocked);
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-2", new DateOnly(1990, 3, 4)));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.BirthdayVisibility = Visibility.Everyone;

        var result = await MakeController("user-1", settings).GetAsync(subject.Id);

        Assert.That(Body(result).Birthday, Is.Null);
    }

    [Test]
    public async Task GetAsync_BirthdayAbsentWhenTheIdentityLookupFails()
    {
        // Visible, but Identity is unreachable. No registered bus answer => the resolver absorbs the
        // throw and the field is simply omitted, rather than the read failing.
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.BirthdayVisibility = Visibility.Everyone;

        var result = await MakeController("user-1", settings).GetAsync(subject.Id);

        Assert.That(Body(result).Birthday, Is.Null);
    }

    [Test]
    public async Task GetByUserIdAsync_AppliesTheSameBirthdayGate()
    {
        // The two routes differ only in how the subject is addressed, so neither may be the lenient
        // one - a service or client that knows the user id must not get more than one that knows the
        // profile id.
        await AddProfile("user-1", "viewer");
        await AddProfile("user-2", "subject");
        PrivacyTestHelpers.RegisterBirthdays(_bus, ("user-2", new DateOnly(1990, 3, 4)));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.BirthdayVisibility = Visibility.Friends;

        var result = await MakeController("user-1", settings).GetByUserIdAsync("user-2");

        Assert.That(Body(result).Birthday, Is.Null);
    }

    // ── T2-17 connections ────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_ConnectionsVisibleToAFriend()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        PrivacyTestHelpers.RegisterConnections(_bus, "user-2", PrivacyTestHelpers.Steam("76561198000000000"));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        var dto = Body(result);
        Assert.That(dto.Connections, Is.Not.Null.And.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(dto.Connections![0].Type, Is.EqualTo("steam"));
            Assert.That(dto.Connections![0].ExternalId, Is.EqualTo("76561198000000000"));
        });
    }

    [Test]
    public async Task GetAsync_SteamIdNeverReachesAStrangersProjection()
    {
        // ConnectionsVisibility defaults to Friends. A raw SteamID64 is a stable cross-platform
        // correlation handle, so the stranger case is the one that matters.
        await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        PrivacyTestHelpers.RegisterConnections(_bus, "user-2", PrivacyTestHelpers.Steam("76561198000000000"));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).Connections, Is.Null);
    }

    [Test]
    public async Task GetAsync_ConnectionsAbsentForABlockedViewerWhateverTheSettingSays()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(subject, viewer, RelationshipStatus.Blocked);
        PrivacyTestHelpers.RegisterConnections(_bus, "user-2", PrivacyTestHelpers.Steam("76561198000000000"));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.ConnectionsVisibility = Visibility.Everyone;

        var result = await MakeController("user-1", settings).GetAsync(subject.Id);

        Assert.That(Body(result).Connections, Is.Null);
    }

    [Test]
    public async Task GetAsync_ConnectionsVisibilityNobody_HidesThemEvenFromAFriend()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        PrivacyTestHelpers.RegisterConnections(_bus, "user-2", PrivacyTestHelpers.Steam("76561198000000000"));
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var settings = PrivacyTestHelpers.Defaults("user-2");
        settings.ConnectionsVisibility = Visibility.Nobody;

        var result = await MakeController("user-1", settings).GetAsync(subject.Id);

        Assert.That(Body(result).Connections, Is.Null);
    }

    [Test]
    public async Task GetAsync_ConnectionsEmptyWhenTheIdentityLookupFails()
    {
        // Visible, but Identity is unreachable - present and empty rather than guessed, matching how
        // an unreachable Guild is handled for mutualServers.
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Friends);
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).Connections, Is.Not.Null.And.Empty);
    }

    [Test]
    public async Task GetAsync_HiddenFieldsAreNeverFetchedAtAll()
    {
        // Gating the query as well as the projection is what makes a hidden field free, and it means
        // a bug in the projector cannot leak data the service never loaded. Nothing is registered on
        // the bus, so a call would throw inside the resolver and be swallowed - what is asserted is
        // that no Identity request was issued at all.
        await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        _identityFacts = PrivacyTestHelpers.IdentityFacts(_bus);

        // Stranger + shipped defaults: connections are Friends-only, birthday is Nobody.
        await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(_bus.LastInvoked, Is.Not.InstanceOf<Identity.Contracts.Bus.Request.GetUserBirthdaysRequest>());
        Assert.That(_bus.LastInvoked, Is.Not.InstanceOf<Identity.Contracts.Bus.Request.GetUserConnectionsRequest>());
    }

    // ── T0-3 ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task GetAsync_BlockedViewer_GetsTheMinimalProjectionNotA404()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject", OnlineStatus.Online);
        await AddRelationship(subject, viewer, RelationshipStatus.Blocked);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        var dto = Body(result);
        Assert.Multiple(() =>
        {
            Assert.That(dto.UserName, Is.EqualTo("subject"));
            Assert.That(dto.Bio, Is.Null);
            Assert.That(dto.OnlineStatus, Is.EqualTo(OnlineStatus.Offline));
            Assert.That(dto.MutualFriends, Is.Null);
        });
    }

    [Test]
    public async Task GetAsync_BlockerReadingTheBlockedUser_AlsoGetsTheMinimalProjection()
    {
        // Symmetric on the read path even though the block itself is not: once either party has
        // blocked, neither gets the rich view of the other.
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");
        await AddRelationship(viewer, subject, RelationshipStatus.Blocked);

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).Bio, Is.Null);
    }

    [Test]
    public async Task GetAsync_UnblockedPair_GoesBackToTheOrdinaryProjection()
    {
        var viewer = await AddProfile("user-1", "viewer");
        var subject = await AddProfile("user-2", "subject");

        var result = await MakeController("user-1", PrivacyTestHelpers.Defaults("user-2")).GetAsync(subject.Id);

        Assert.That(Body(result).Bio, Is.EqualTo("bio"));
    }
}
