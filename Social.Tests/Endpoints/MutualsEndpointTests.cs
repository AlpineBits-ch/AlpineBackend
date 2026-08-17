using System.Security.Claims;
using Domain;
using Guild.Contracts.Bus.Request;
using Guild.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Social.Api.Dtos.Response;
using Social.Api.Endpoints;
using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Endpoints;

/// <summary>
/// The mutual-friends and mutual-servers lists behind the full profile view: the visibility gate
/// they share with the profile projection, and the keyset paging over the friends list.
/// </summary>
[TestFixture]
public class MutualsEndpointTests
{
    private TestSocialContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeMessageBus _bus = null!;
    private string _originalInstanceUrl = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _bus = new FakeMessageBus();
        _originalInstanceUrl = AppEnvironment.Env.GeneralConfiguration.InstanceUrl;
        AppEnvironment.Env.GeneralConfiguration.InstanceUrl = "https://api.test";
    }

    [TearDown]
    public async Task TearDown()
    {
        AppEnvironment.Env.GeneralConfiguration.InstanceUrl = _originalInstanceUrl;
        await _context.DisposeAsync();
    }

    private static ClaimsPrincipal MakeUser(string userId) => new(
        new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private async Task<Profile> AddProfile(string userId, string userName, OnlineStatus status = OnlineStatus.Online)
    {
        var profile = Profile.Create(new CreateProfileParams { UserId = userId, Username = userName });
        profile.OnlineStatus = status;
        _context.Profiles.Add(profile);
        await _context.SaveChangesAsync();
        return profile;
    }

    /// <summary>
    /// One direction only, which is all the mutual queries read. The stamp is set before Add
    /// because <c>TimestampUpdater</c> fills it on insert only when it is still default.
    /// </summary>
    private async Task Befriend(Profile owner, Profile target, DateTimeOffset? updatedAt = null)
    {
        _context.Relationships.Add(new Relationship
        {
            Id = $"rlsp_{owner.Id}_{target.Id}",
            OwnerId = owner.Id,
            TargetId = target.Id,
            Status = RelationshipStatus.Friends,
            UpdatedAt = updatedAt ?? default,
        });
        await _context.SaveChangesAsync();
    }

    private Task<IResult> FriendsAsync(
        string callerUserId, string subjectProfileId, UserPrivacySettings settings, int? limit = null, string? cursor = null)
        => MutualsEndpoint.MutualFriendsAsync(
            subjectProfileId, _context, PrivacyTestHelpers.CacheReturning(_cache, _bus, settings.Summary),
            MakeUser(callerUserId), limit, cursor);

    private Task<IResult> ServersAsync(
        string callerUserId, string subjectProfileId, UserPrivacySettings settings, ISharedGuildResolver resolver)
        => MutualsEndpoint.MutualServersAsync(
            subjectProfileId, _context, PrivacyTestHelpers.CacheReturning(_cache, _bus, settings.Summary),
            resolver, MakeUser(callerUserId));

    /// <summary>Wraps the settings summary so a test states only the field it is about.</summary>
    private sealed class UserPrivacySettings
    {
        public required Identity.Contracts.Bus.Response.UserPrivacySettingsSummary Summary { get; init; }

        public static UserPrivacySettings For(string userId, Visibility friends, Visibility servers)
        {
            var summary = PrivacyTestHelpers.Defaults(userId);
            summary.MutualFriendsVisibility = friends;
            summary.MutualServersVisibility = servers;
            return new UserPrivacySettings { Summary = summary };
        }
    }

    private static T Body<T>(IResult result)
    {
        Assert.That(result, Is.InstanceOf<Ok<T>>(), $"expected an Ok<{typeof(T).Name}>, got {result.GetType().Name}");
        return ((Ok<T>)result).Value!;
    }

    // ── the gate ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Friends_RefusesWhenTheSubjectShowsMutualsToNobody()
    {
        var viewer = await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");
        await Befriend(viewer, subject);
        await Befriend(subject, viewer);

        var result = await FriendsAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Nobody, Visibility.Friends));

        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task Friends_RefusesAStrangerWhenTheSettingIsFriendsOnly()
    {
        await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");

        var result = await FriendsAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Friends, Visibility.Friends));

        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task Friends_RefusesABlockedViewerEvenWhenTheSettingIsEveryone()
    {
        var viewer = await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");

        _context.Relationships.Add(new Relationship
        {
            Id = "rlsp_block",
            OwnerId = subject.Id,
            TargetId = viewer.Id,
            Status = RelationshipStatus.Blocked,
        });
        await _context.SaveChangesAsync();

        var result = await FriendsAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Everyone, Visibility.Everyone));

        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task Servers_RefusesWhenTheSubjectShowsServersToNobody()
    {
        await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");

        var result = await ServersAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Everyone, Visibility.Nobody),
            PrivacyTestHelpers.SharedGuilds(_bus));

        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status403Forbidden));
    }

    [Test]
    public async Task Friends_AnswersAnEmptyPageForASelfView()
    {
        var viewer = await AddProfile("user-viewer", "viewer");

        var result = await FriendsAsync("user-viewer", viewer.Id,
            UserPrivacySettings.For("user-viewer", Visibility.Nobody, Visibility.Nobody));

        Assert.That(Body<MutualFriendsPageDto>(result).Items, Is.Empty);
    }

    [Test]
    public async Task Friends_IsNotFoundForAProfileThatDoesNotExist()
    {
        await AddProfile("user-viewer", "viewer");

        var result = await FriendsAsync("user-viewer", "prfl_missing",
            UserPrivacySettings.For("user-subject", Visibility.Everyone, Visibility.Everyone));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    // ── the list ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Friends_ReturnsOnlyThePeopleBothSidesAreFriendsWith()
    {
        var viewer = await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");
        var shared = await AddProfile("user-shared", "shared");
        var viewerOnly = await AddProfile("user-vo", "viewer-only");
        var subjectOnly = await AddProfile("user-so", "subject-only");

        await Befriend(viewer, subject);
        await Befriend(subject, viewer);
        await Befriend(viewer, shared);
        await Befriend(subject, shared);
        await Befriend(viewer, viewerOnly);
        await Befriend(subject, subjectOnly);

        var result = await FriendsAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Friends, Visibility.Friends));

        var items = Body<MutualFriendsPageDto>(result).Items;
        Assert.Multiple(() =>
        {
            Assert.That(items.Select(i => i.UserId), Is.EquivalentTo(new[] { "user-shared" }));
            Assert.That(items[0].AvatarUrl,
                Is.EqualTo($"https://api.test/api/v1/social/profiles/{shared.Id}/avatar"));
        });
    }

    [Test]
    public async Task Friends_ProjectsHiddenAsOffline()
    {
        var viewer = await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");
        var shared = await AddProfile("user-shared", "shared", OnlineStatus.Hidden);

        await Befriend(viewer, subject);
        await Befriend(subject, viewer);
        await Befriend(viewer, shared);
        await Befriend(subject, shared);

        var result = await FriendsAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Friends, Visibility.Friends));

        Assert.That(Body<MutualFriendsPageDto>(result).Items[0].OnlineStatus, Is.EqualTo(OnlineStatus.Offline));
    }

    [Test]
    public async Task Friends_PagesEachRowExactlyOnceAndTerminates()
    {
        var viewer = await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");
        await Befriend(viewer, subject);
        await Befriend(subject, viewer);

        // Distinct timestamps so the keyset has a total order to walk.
        for (var i = 0; i < 5; i++)
        {
            var mutual = await AddProfile($"user-m{i}", $"mutual-{i}");
            var stamp = new DateTimeOffset(2026, 1, 1 + i, 0, 0, 0, TimeSpan.Zero);
            await Befriend(viewer, mutual);
            await Befriend(subject, mutual, stamp);
        }

        var settings = UserPrivacySettings.For("user-subject", Visibility.Friends, Visibility.Friends);
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = Body<MutualFriendsPageDto>(await FriendsAsync("user-viewer", subject.Id, settings, 2, cursor));
            seen.AddRange(page.Items.Select(i => i.UserId));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.Unique);
            Assert.That(seen, Is.EquivalentTo(new[] { "user-m0", "user-m1", "user-m2", "user-m3", "user-m4" }));
            Assert.That(cursor, Is.Null, "paging must terminate rather than hand back a cursor forever");
        });
    }

    [Test]
    public async Task Friends_RejectsAMalformedCursor()
    {
        var viewer = await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");
        await Befriend(viewer, subject);
        await Befriend(subject, viewer);

        var result = await FriendsAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Friends, Visibility.Friends), cursor: "not-base64!!");

        Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task Servers_CarriesTheNamesGuildNowSends()
    {
        await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");

        _bus.RespondWith<GetSharedGuildsRequest, GetSharedGuildsResponse>(new GetSharedGuildsResponse
        {
            Shared =
            [
                new SharedGuildsSummary
                {
                    OtherUserId = "user-subject",
                    GuildIds = ["gld_a", "gld_b"],
                    Guilds =
                    [
                        new SharedGuildEntry { Id = "gld_a", Name = "Alpha" },
                        new SharedGuildEntry { Id = "gld_b", Name = "Beta" },
                    ],
                },
            ],
        });

        var result = await ServersAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Everyone, Visibility.Everyone),
            PrivacyTestHelpers.SharedGuilds(_bus));

        var page = Body<MutualServersPageDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(page.Items.Select(i => i.Name), Is.EqualTo(new[] { "Alpha", "Beta" }));
            Assert.That(page.NextCursor, Is.Null);
        });
    }

    [Test]
    public async Task Servers_StillAnswersWhenGuildSendsIdsOnly()
    {
        await AddProfile("user-viewer", "viewer");
        var subject = await AddProfile("user-subject", "subject");

        var resolver = PrivacyTestHelpers.SharedGuildsReturning(_bus, ("user-subject", ["gld_a"]));

        var result = await ServersAsync("user-viewer", subject.Id,
            UserPrivacySettings.For("user-subject", Visibility.Everyone, Visibility.Everyone), resolver);

        var page = Body<MutualServersPageDto>(result);
        Assert.Multiple(() =>
        {
            Assert.That(page.Items.Select(i => i.GuildId), Is.EqualTo(new[] { "gld_a" }));
            Assert.That(page.Items[0].Name, Is.Null);
        });
    }

    private static int? StatusOf(IResult result) => result switch
    {
        IStatusCodeHttpResult status => status.StatusCode,
        _ => null,
    };
}
