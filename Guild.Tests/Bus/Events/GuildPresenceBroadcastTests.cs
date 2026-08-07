using Echo.Voice.Testing;
using System.Text.Json;
using Echo.Realtime.Caching;
using Guild.Application.Bus.Events.Realtime;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Events;
using OnlineStatus = Guild.Application.Dtos.Response.OnlineStatus;

namespace Guild.Tests.Bus.Events;

/// <summary>
/// Covers the <c>guild.PresenceChanged</c> fan-out in <see cref="GuildLifecycleHandler"/> - the
/// second of the two sites privacy spec T0-5 names.
/// </summary>
[TestFixture]
public class GuildPresenceBroadcastTests
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

    private static MemberPresenceState Present(string memberId, string userId, OnlineStatus status) => new()
    {
        MemberId = memberId, UserId = userId, Status = status.ToString(),
        HeartbeatTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    private GuildHydrateService HydrateWith(params MemberPresenceState[] present) =>
        new(RedisTestFactory.CreateWithPresence(present), NullLogger<GuildHydrateService>.Instance);

    private Task StatusChangedAsync(
        OnlineStatus status, MemberPresenceState[] present, params (string Blocker, string Blocked)[] blocks) =>
        _handler.Handle(
            new UserStatusChanged { UserId = SubjectUserId, Status = status.ToString() },
            _context, HydrateWith(present), _hub,
            PrivacyTestFactory.Blocks(_bus, _cache, blocks),
            PrivacyTestFactory.Privacy(_bus, _cache, PrivacyTestFactory.Permissive(SubjectUserId)));

    /// <summary>The Status field of the payload sent to <paramref name="userId"/>.</summary>
    private string? StatusSentTo(string userId)
    {
        var clients = (FakeHubClients)_hub.Clients;

        var send = clients.SentToUsers
            .Where(s => s.Method == "guild.PresenceChanged" && s.UserIds.Contains(userId))
            .Select(s => s.Args)
            .SingleOrDefault();

        if (send is null) return null;

        var payload = JsonSerializer.SerializeToElement(send[0]);
        return payload.GetProperty("Status").GetString();
    }

    // ── Normal ────────────────────────────────────────────────────────────────

    [Test]
    public async Task StatusChange_ToHidden_ReachesPeersAsOffline()
    {
        await StatusChangedAsync(OnlineStatus.Hidden,
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Hidden),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        Assert.That(StatusSentTo(PeerUserId), Is.EqualTo(nameof(OnlineStatus.Offline)));
    }

    [Test]
    public async Task StatusChange_ToHidden_StillReachesTheSubjectAsHidden()
    {
        await StatusChangedAsync(OnlineStatus.Hidden,
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Hidden),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        Assert.That(StatusSentTo(SubjectUserId), Is.EqualTo(nameof(OnlineStatus.Hidden)));
    }

    [TestCase(OnlineStatus.Online)]
    [TestCase(OnlineStatus.Idle)]
    [TestCase(OnlineStatus.DoNotDisturb)]
    public async Task StatusChange_ToAnythingElse_IsBroadcastUnchanged(OnlineStatus status)
    {
        await StatusChangedAsync(status,
            [Present(SubjectMemberId, SubjectUserId, status),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        Assert.That(StatusSentTo(PeerUserId), Is.EqualTo(status.ToString()));
    }

    [Test]
    public async Task Connect_BroadcastsOnlineToPeers()
    {
        await _handler.Handle(
            new Echo.Realtime.UserConnected(SubjectUserId),
            _context,
            HydrateWith(Present(PeerMemberId, PeerUserId, OnlineStatus.Online)),
            _hub,
            PrivacyTestFactory.Blocks(_bus, _cache),
            PrivacyTestFactory.Privacy(_bus, _cache, PrivacyTestFactory.Permissive(SubjectUserId)));

        Assert.That(StatusSentTo(PeerUserId), Is.EqualTo(nameof(OnlineStatus.Online)));
    }

    // ── Edge ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task StatusChange_WithNobodyElseOnline_StillTellsTheSubject()
    {
        await StatusChangedAsync(OnlineStatus.Hidden,
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Hidden)]);

        Assert.That(StatusSentTo(SubjectUserId), Is.EqualTo(nameof(OnlineStatus.Hidden)));
    }

    [Test]
    public async Task StatusChange_SendsTheSubjectAndThePeersSeparately()
    {
        // The split is what allows the two payloads to differ at all - a single send could only
        // ever carry one Status, and it used to carry the raw one.
        await StatusChangedAsync(OnlineStatus.Hidden,
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Hidden),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        var sends = ((FakeHubClients)_hub.Clients).SentToUsers
            .Where(s => s.Method == "guild.PresenceChanged")
            .ToList();

        Assert.That(sends, Has.Count.EqualTo(2));
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task StatusChange_NoPayloadEverCarriesHiddenToAThirdParty()
    {
        await StatusChangedAsync(OnlineStatus.Hidden,
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Hidden),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)]);

        var leaked = ((FakeHubClients)_hub.Clients).SentToUsers
            .Where(s => s.Method == "guild.PresenceChanged")
            .Where(s => s.UserIds.Any(id => id != SubjectUserId))
            .Select(s => JsonSerializer.SerializeToElement(s.Args[0]).GetProperty("Status").GetString())
            .ToList();

        Assert.That(leaked, Has.None.EqualTo(nameof(OnlineStatus.Hidden)));
    }

    [Test]
    public async Task StatusChange_ToAPeerWhoBlockedTheSubject_IsNotDeliveredAtAll()
    {
        await StatusChangedAsync(OnlineStatus.Online,
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)],
            (PeerUserId, SubjectUserId));

        Assert.That(StatusSentTo(PeerUserId), Is.Null);
    }

    [Test]
    public async Task StatusChange_ToAPeerTheSubjectBlocked_IsAlsoNotDelivered()
    {
        // Bidirectional: no presence event flows between a blocked pair, whichever way it points.
        await StatusChangedAsync(OnlineStatus.Online,
            [Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online),
             Present(PeerMemberId, PeerUserId, OnlineStatus.Online)],
            (SubjectUserId, PeerUserId));

        Assert.That(StatusSentTo(PeerUserId), Is.Null);
    }

    [Test]
    public async Task StatusChange_WhenSocialIsUnreachable_ReachesNoPeerAtAll()
    {
        // Fail closed.
        _bus.ClearResponses();

        await _handler.Handle(
            new UserStatusChanged { UserId = SubjectUserId, Status = nameof(OnlineStatus.Online) },
            _context,
            HydrateWith(Present(SubjectMemberId, SubjectUserId, OnlineStatus.Online),
                        Present(PeerMemberId, PeerUserId, OnlineStatus.Online)),
            _hub,
            new BlockCache(_cache, _bus, NullLogger<BlockCache>.Instance),
            // Left unreachable along with everything else: this test is about failing closed, and
            // an unresolvable privacy record means activity is withheld, not published.
            PrivacyTestFactory.UnreachablePrivacy(_bus, _cache));

        Assert.Multiple(() =>
        {
            Assert.That(StatusSentTo(PeerUserId), Is.Null);
            Assert.That(StatusSentTo(SubjectUserId), Is.EqualTo(nameof(OnlineStatus.Online)));
        });
    }

    [Test]
    public async Task Disconnect_ToABlockedPeer_IsNotDelivered()
    {
        var voiceStore = VoiceTestHarness.StoreFor(_cache, new FakeDistributedLockService());

        await _handler.Handle(
            new Echo.Realtime.UserDisconnected(SubjectUserId, "device-1"),
            _context,
            HydrateWith(Present(PeerMemberId, PeerUserId, OnlineStatus.Online)),
            _cache, voiceStore, _hub, new FakeMessageBus(),
            PrivacyTestFactory.Blocks(_bus, _cache, (PeerUserId, SubjectUserId)));

        Assert.That(StatusSentTo(PeerUserId), Is.Null);
    }
}
