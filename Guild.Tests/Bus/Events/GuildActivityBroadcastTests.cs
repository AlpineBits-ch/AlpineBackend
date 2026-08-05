using System.Text.Json;
using Guild.Application.Bus.Events.Realtime;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Events;
using Social.Contracts.Dtos;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Bus.Events;

/// <summary>The <c>UserActivityChanged</c> fan-out.</summary>
[TestFixture]
public class GuildActivityBroadcastTests
{
    private const string GuildId = "gild-1";
    private const string SubjectUserId = "user-subject";
    private const string SubjectMemberId = "memb-subject";
    private const string PeerUserId = "user-peer";
    private const string PeerMemberId = "memb-peer";

    private TestGuildContext _context = null!;
    private FakeHubContext _hub = null!;
    private FakeDistributedCache _cache = null!;
    private FakeInvokingMessageBus _bus = null!;
    private GuildLifecycleHandler _handler = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _hub = new FakeHubContext();
        _cache = new FakeDistributedCache();
        _bus = new FakeInvokingMessageBus();
        _handler = new GuildLifecycleHandler();

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = "owner-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        AddMember(SubjectMemberId, SubjectUserId);
        AddMember(PeerMemberId, PeerUserId);

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private void AddMember(string memberId, string userId) =>
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId.ToUpperInvariant(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

    private static MemberPresenceState Present(
        string memberId, string userId, OnlineStatus status, params ActivityDto[] activities) => new()
    {
        MemberId = memberId,
        UserId = userId,
        Status = status.ToString(),
        Activities = activities.Length > 0 ? activities : null,
        HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    private static ActivityDto Playing(string name = "Overwatch", string? appId = "1", long? startedAt = null) => new()
    {
        Type = "Playing", Source = "Rpc", Name = name, ApplicationId = appId, StartedAt = startedAt,
    };

    private GuildHydrateService HydrateWith(params MemberPresenceState[] present) =>
        new(RedisTestFactory.CreateWithPresence(present), NullLogger<GuildHydrateService>.Instance);

    private Task ActivityChangedAsync(ActivityDto[] activities, MemberPresenceState[] present, bool shareActivity = true) =>
        _handler.Handle(
            new UserActivityChanged { UserId = SubjectUserId, Activities = activities },
            _context, HydrateWith(present), _hub,
            PrivacyTestFactory.Blocks(_bus, _cache),
            PrivacyTestFactory.Privacy(_bus, _cache, Settings(shareActivity)));

    private static Identity.Contracts.Bus.Response.UserPrivacySettingsSummary Settings(bool shareActivity)
    {
        var settings = PrivacyTestFactory.Permissive(SubjectUserId);
        settings.ShareActivity = shareActivity;
        return settings;
    }

    /// <summary>The Activities array of the payload sent to <paramref name="userId"/>, or null if
    /// nothing was sent to them at all.</summary>
    private JsonElement? ActivitiesSentTo(string userId)
    {
        var clients = (FakeHubClients)_hub.Clients;

        var send = clients.SentToUsers
            .Where(s => s.Method == "guild.PresenceChanged" && s.UserIds.Contains(userId))
            .Select(s => s.Args)
            .SingleOrDefault();

        if (send is null) return null;

        return JsonSerializer.SerializeToElement(send[0]).GetProperty("Activities");
    }

    // ── The rule that matters most ──────────────────────────────────────────────────────────

    [Test]
    public async Task ActivityChange_ForAMemberWithNoLivePresence_BroadcastsNothing()
    {
        // Nobody present at all: the subject is not connected.
        await ActivityChangedAsync([Playing()], present: []);

        Assert.That(((FakeHubClients)_hub.Clients).SentToUsers, Is.Empty);
    }

    [Test]
    public async Task ActivityChange_PreservesAChosenHiddenStatus()
    {
        await ActivityChangedAsync([Playing()],
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Hidden),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        var payload = JsonSerializer.SerializeToElement(
            ((FakeHubClients)_hub.Clients).SentToUsers
                .Single(s => s.UserIds.Contains(SubjectUserId)).Args[0]);

        Assert.That(payload.GetProperty("Status").GetString(), Is.EqualTo(nameof(OnlineStatus.Hidden)),
            "an activity write is not a status change");
    }

    // ── Normal fan-out ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ActivityChange_ReachesPeers()
    {
        await ActivityChangedAsync([Playing()],
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        var activities = ActivitiesSentTo(PeerUserId);

        Assert.That(activities, Is.Not.Null);
        Assert.That(activities!.Value.GetArrayLength(), Is.EqualTo(1));
        Assert.That(activities.Value[0].GetProperty("Name").GetString(), Is.EqualTo("Overwatch"));
    }

    [Test]
    public async Task ActivityChange_StampsAStartTimeWhenNoneWasSupplied()
    {
        await ActivityChangedAsync([Playing(startedAt: null)],
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        var startedAt = ActivitiesSentTo(PeerUserId)!.Value[0].GetProperty("StartedAt").GetInt64();

        Assert.That(startedAt, Is.GreaterThan(0), "server receive time is the fallback no client can lie about");
    }

    [Test]
    public async Task ActivityChange_CarriesForwardAnExistingStartTime()
    {
        await ActivityChangedAsync([Playing(startedAt: null)],
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online, Playing(startedAt: 1_000L)),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        var startedAt = ActivitiesSentTo(PeerUserId)!.Value[0].GetProperty("StartedAt").GetInt64();

        Assert.That(startedAt, Is.EqualTo(1_000L));
    }

    // ── Privacy ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ActivityChange_ShareActivityOff_ReachesPeersWithNoActivities()
    {
        await ActivityChangedAsync([Playing()],
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)],
            shareActivity: false);

        Assert.That(ActivitiesSentTo(PeerUserId)!.Value.GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task ActivityChange_ShareActivityOff_SubjectStillSeesTheirOwn()
    {
        await ActivityChangedAsync([Playing()],
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)],
            shareActivity: false);

        Assert.That(ActivitiesSentTo(SubjectUserId)!.Value.GetArrayLength(), Is.EqualTo(1),
            "a client that cannot read its own activity back cannot render the setting that hides it");
    }

    [Test]
    public async Task ActivityChange_HiddenStatus_WithholdsActivitiesFromPeers()
    {
        await ActivityChangedAsync([Playing()],
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Hidden),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        Assert.That(ActivitiesSentTo(PeerUserId)!.Value.GetArrayLength(), Is.Zero);
    }

    // ── Clearing ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ActivityCleared_ReachesPeersAsAnEmptyList()
    {
        await ActivityChangedAsync([],
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online, Playing(startedAt: 1_000L)),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        Assert.That(ActivitiesSentTo(PeerUserId)!.Value.GetArrayLength(), Is.Zero);
    }
}
