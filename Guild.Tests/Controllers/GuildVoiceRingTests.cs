using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Guild.Application.Bus.Events.Voice;
using Guild.Application.Controllers;
using Guild.Application.Dtos;
using Guild.Application.Models;
using Guild.Application.Services;
using Guild.Contracts;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Guild.Tests.Controllers;

/// <summary>
/// The ephemeral voice-channel ring, end to end: who may send one, who is told about it, and every
/// way it can stop being pending.
/// </summary>
[TestFixture]
public class GuildVoiceRingTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-voice";
    private const string SecondChannelId = "channel-voice-2";
    private const string TextChannelId = "channel-text";
    private const string PrivateChannelId = "channel-private";

    private const string Inviter = "user-inviter";
    private const string Target = "user-target";
    private const string Bystander = "user-bystander";
    private const string Stranger = "user-stranger";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeInvokingMessageBus _bus = null!;
    private FakeDistributedLockService _locks = null!;
    private TestClock _clock = null!;

    private VoiceRoomStore _rooms = null!;
    private VoiceRingStore _store = null!;
    private VoiceRingThrottle _throttle = null!;
    private VoiceRingService _rings = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeInvokingMessageBus();
        _locks = new FakeDistributedLockService();
        _clock = new TestClock(new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero));

        _bus.SetResponse<GetBlockRelationshipsRequest>(new GetBlockRelationshipsResponse());
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse
        {
            Profile = new ProfileDto { UserName = "Inviter", AvatarUrl = "https://cdn/a.png" },
        });

        _rooms = VoiceTestHarness.StoreFor(_cache, _locks);
        _store = new VoiceRingStore(_locks, _cache) { Clock = _clock };
        _throttle = new VoiceRingThrottle(_cache) { Clock = _clock };
        _rings = VoiceRingTestFactory.Create(
            _context, _cache, _locks, _hub, _bus, _store, _throttle, _clock);

        await SeedAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ══════════════════════════════════════════════════════════════════════════ Arrangement
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One guild, two voice channels, a text channel, and a private voice channel.
    /// </summary>
    private async Task SeedAsync()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "user-owner", Name = "guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        AddChannel(ChannelId, ChannelType.Voice, "General");
        AddChannel(SecondChannelId, ChannelType.Voice, "Gaming");
        AddChannel(TextChannelId, ChannelType.Text, "chat");
        AddChannel(PrivateChannelId, ChannelType.Voice, "Mods only");

        _context.Roles.Add(new Role
        {
            Id = "role-open", GuildId = GuildId, Name = "member",
            Permissions = Permissions.ViewChannel | Permissions.Connect | Permissions.Speak,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-none", GuildId = GuildId, Name = "nobody", Permissions = Permissions.None,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        AddMember("member-inviter", Inviter, "role-open");
        AddMember("member-target", Target, "role-open");
        AddMember("member-bystander", Bystander, "role-open");
        AddMember("member-stranger", Stranger, "role-none");

        await _context.SaveChangesAsync();
    }

    private void AddChannel(string id, ChannelType type, string name) =>
        _context.Channels.Add(new Channel
        {
            Id = id, GuildId = GuildId, Name = name, Description = "d", Type = type,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

    private void AddMember(string memberId, string userId, string roleId)
    {
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{userId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rm-{memberId}", RoleId = roleId, MemberId = memberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private GuildVoiceRingController Controller(string userId, string? deviceId = null)
    {
        var http = new DefaultHttpContext { User = TestPrincipal.Create(userId) };
        if (deviceId is not null) http.Request.Headers[DeviceIdentity.HeaderName] = deviceId;

        return new GuildVoiceRingController(
            _rings, _store, _context,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    /// <summary>Puts somebody in a voice room without going through the join endpoint, which would
    /// drag in media transport and entitlements this fixture has no interest in.</summary>
    private Task SitInAsync(string channelId, params string[] userIds) =>
        _rooms.MutateAsync(VoiceRoomKey.Channel(channelId), room =>
        {
            foreach (var userId in userIds)
            {
                if (room.Find(userId) is not null) continue;
                room.Participants.Add(new VoiceParticipant { UserId = userId, JoinedAt = DateTime.UtcNow });
            }
        }, GuildId);

    private async Task<VoiceRingDto> RingAsync(
        string channelId = ChannelId, string target = Target, string? deviceId = null)
    {
        var result = await Controller(Inviter, deviceId)
            .Ring(GuildId, channelId, new RingVoiceChannelDto(target), CancellationToken.None);
        return (VoiceRingDto)((OkObjectResult)result).Value!;
    }

    private static object? Field(object? payload, string name) =>
        payload?.GetType().GetProperty(name)?.GetValue(payload);

    private FakeHubClients Sent => (FakeHubClients)_hub.Clients;

    private object? PayloadOf(string method) =>
        Sent.SentMessages.LastOrDefault(m => m.Method == method).Args?.ElementAtOrDefault(0);

    private List<VoiceRingPushRequested> Pushes =>
        _bus.Published.OfType<VoiceRingPushRequested>().ToList();

    private List<VoiceRingForBots> BotEvents =>
        _bus.Published.OfType<VoiceRingForBots>().ToList();

    private List<VoiceRingDirectMessageRequested> DirectMessages =>
        _bus.Published.OfType<VoiceRingDirectMessageRequested>().ToList();

    // ══════════════════════════════════════════════════════════════════════════ Sending a ring
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Ring_CreatesAPendingRingAndTellsTheTarget()
    {
        await SitInAsync(ChannelId, Inviter);

        var ring = await RingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ring.Status, Is.EqualTo("Pending"));
            Assert.That(ring.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(ring.TargetUserId, Is.EqualTo(Target));
            Assert.That(ring.ExpiresInSeconds, Is.EqualTo((int)VoiceRing.Ttl.TotalSeconds));
            Assert.That(Sent.RecipientsOf(VoiceRingService.IncomingEvent), Is.EqualTo(new[] { Target }));
            Assert.That(Sent.RecipientsOf(VoiceRingService.SentEvent), Is.EqualTo(new[] { Inviter }),
                "the inviter's other windows must not offer to send an invitation that is already out");
        });
    }

    [Test]
    public async Task Ring_NamesTheChannelAndTheInviterInTheIncomingEvent()
    {
        await SitInAsync(ChannelId, Inviter, Bystander);

        await RingAsync();
        var payload = PayloadOf(VoiceRingService.IncomingEvent);

        Assert.Multiple(() =>
        {
            Assert.That(Field(payload, "channelName"), Is.EqualTo("General"));
            Assert.That(Field(payload, "inviterName"), Is.EqualTo("Inviter"));
            Assert.That(Field(payload, "participantUserIds"),
                Is.EquivalentTo(new[] { Inviter, Bystander }),
                "the card shows who is already in there, which the target may see anyway");
        });
    }

    [Test]
    public async Task Ring_SchedulesItsOwnExpiry()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        var scheduled = _bus.Published.OfType<VoiceRingTimeoutCheck>().SingleOrDefault();
        Assert.That(scheduled?.RingId, Is.EqualTo(ring.RingId));
    }

    [Test]
    public async Task Ring_IsFindableByTheTargetsCatchUpRead()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        var result = await Controller(Target).Pending(CancellationToken.None);
        var pending = (List<VoiceRingDto>)((OkObjectResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(pending.Select(p => p.RingId), Is.EqualTo(new[] { ring.RingId }),
                "the realtime event is never replayed, so this is the only path for a client that was offline");
            Assert.That(pending[0].ChannelName, Is.EqualTo("General"));
        });
    }

    [Test]
    public async Task Ring_PublishesTheOpeningBotEvent()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        var published = BotEvents.Single();
        Assert.Multiple(() =>
        {
            Assert.That(published.Status, Is.EqualTo("Pending"));
            Assert.That(published.RingId, Is.EqualTo(ring.RingId));
            Assert.That(published.Reason, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Refusals
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Ring_IsForbidden_WhenTheInviterIsNotInTheChannel()
    {
        var result = await Controller(Inviter)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidResult>());
            Assert.That(Sent.SentMessages, Is.Empty,
                "'come and join me in here' is a claim somebody outside the channel cannot make");
        });
    }

    [Test]
    public async Task Ring_IsRefused_AndSilent_WhenTheTargetCannotSeeTheChannel()
    {
        await SitInAsync(PrivateChannelId, Inviter);

        var result = await Controller(Inviter).Ring(
            GuildId, PrivateChannelId, new RingVoiceChannelDto(Stranger), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(403));
            Assert.That(((VoiceRingRefusalDto)((ObjectResult)result).Value!).Reason,
                Is.EqualTo("TargetCannotJoinChannel"));
            Assert.That(Sent.SentMessages, Is.Empty);
            Assert.That(Pushes, Is.Empty,
                "the push names the channel, so a ring must never reach somebody who cannot see it");
        });
    }

    [Test]
    public async Task Ring_IsNotFound_WhenTheTargetIsNotAMemberOfTheGuild()
    {
        await SitInAsync(ChannelId, Inviter);

        var result = await Controller(Inviter)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto("user-nobody"), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Ring_IsRefusedWithoutNamingIt_WhenTheTwoHaveBlockedEachOther()
    {
        await SitInAsync(ChannelId, Inviter);
        _bus.SetResponse<GetBlockRelationshipsRequest>(new GetBlockRelationshipsResponse
        {
            Blocks = [new BlockRelationship { BlockerId = Target, BlockedId = Inviter }],
        });

        var result = await Controller(Inviter)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(403));
            Assert.That(((VoiceRingRefusalDto)((ObjectResult)result).Value!).Reason, Is.EqualTo("Unavailable"),
                "naming the block would turn the endpoint into a block detector");
            Assert.That(Sent.SentMessages, Is.Empty);
        });
    }

    [Test]
    public async Task Ring_IsRejected_WhenTheChannelIsNotAVoiceChannel()
    {
        await SitInAsync(TextChannelId, Inviter);

        var result = await Controller(Inviter)
            .Ring(GuildId, TextChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Ring_IsNotFound_WhenTheChannelDoesNotExist()
    {
        var result = await Controller(Inviter)
            .Ring(GuildId, "channel-ghost", new RingVoiceChannelDto(Target), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Ring_IsRejected_WhenYouRingYourself()
    {
        await SitInAsync(ChannelId, Inviter);

        var result = await Controller(Inviter)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Inviter), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Repeats, supersession, and somebody who is already there
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Ring_Twice_ReturnsTheSameRingAndDoesNotBuzzTwice()
    {
        await SitInAsync(ChannelId, Inviter);

        var first = await RingAsync();
        var second = await RingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second.RingId, Is.EqualTo(first.RingId));
            Assert.That(Pushes.Count(p => !p.Cancel), Is.EqualTo(1),
                "holding the button down must not be a way to buzz somebody repeatedly");
            Assert.That(Sent.SentMessages.Count(m => m.Method == VoiceRingService.IncomingEvent), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Ring_IntoASecondChannel_SupersedesTheFirstInvitation()
    {
        await SitInAsync(ChannelId, Inviter);
        var first = await RingAsync();

        await SitInAsync(SecondChannelId, Inviter);
        var second = await RingAsync(SecondChannelId);

        var superseded = await _store.LoadAsync(first.RingId);
        Assert.Multiple(() =>
        {
            Assert.That(second.RingId, Is.Not.EqualTo(first.RingId));
            Assert.That(superseded!.Status, Is.EqualTo(VoiceRingStatus.Cancelled));
            Assert.That(superseded.Reason, Is.EqualTo(VoiceRingReason.Superseded),
                "one person may not hold you to two invitations at once");
        });
    }

    [Test]
    public async Task Ring_FromASecondInviter_CoexistsWithTheFirst()
    {
        await SitInAsync(ChannelId, Inviter, Bystander);
        await RingAsync();

        await Controller(Bystander)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        var pending = await _store.PendingForTargetAsync(Target);
        Assert.That(pending.Select(r => r.InviterId), Is.EquivalentTo(new[] { Inviter, Bystander }),
            "two people asking you into the same room are two invitations, not one");
    }

    [Test]
    public async Task Ring_IsAConflict_WhenTheTargetIsAlreadyInTheChannel()
    {
        await SitInAsync(ChannelId, Inviter, Target);

        var result = await Controller(Inviter)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(409));
    }

    [Test]
    public async Task Ring_RefundsTheBudget_WhenTheTargetTurnedOutToBeAlreadyInTheChannel()
    {
        await SitInAsync(ChannelId, Inviter, Target);

        for (var i = 0; i < VoiceRingThrottle.MaxPerInviter + 2; i++)
        {
            await Controller(Inviter)
                .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);
        }

        // Nothing was ever sent, so nothing should have been charged: the next genuine ring must
        // still go through.
        await SitInAsync(SecondChannelId, Inviter);
        var result = await Controller(Inviter)
            .Ring(GuildId, SecondChannelId, new RingVoiceChannelDto(Bystander), CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>());
    }

    // ══════════════════════════════════════════════════════════════════════════ Answering
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Accept_ResolvesTheRingAndTellsBothSides()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        var result = await Controller(Target, "phone").Accept(ring.RingId, CancellationToken.None);
        var accepted = (VoiceRingDto)((OkObjectResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Status, Is.EqualTo("Accepted"));
            Assert.That(accepted.ResolvedByDeviceId, Is.EqualTo("phone"));
            Assert.That(Sent.RecipientsOf(VoiceRingService.ResolvedEvent),
                Is.EquivalentTo(new[] { Target, Inviter }));
        });
    }

    [Test]
    public async Task Accept_DoesNotPutTheTargetInTheChannel()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        await Controller(Target).Accept(ring.RingId, CancellationToken.None);

        var room = await _rooms.LoadAsync(VoiceRoomKey.Channel(ChannelId));
        Assert.That(room!.Find(Target), Is.Null,
            "accepting closes the invitation; joining is still the join endpoint's job");
    }

    [Test]
    public async Task Accept_FromSomebodyElse_IsForbidden()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        var result = await Controller(Bystander).Accept(ring.RingId, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ForbidResult>());
    }

    [Test]
    public async Task Decline_ResolvesTheRingAndShutsTheInviterOut()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        await Controller(Target).Decline(ring.RingId, CancellationToken.None);

        var again = await Controller(Inviter)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(((ObjectResult)again).StatusCode, Is.EqualTo(429));
            Assert.That(((VoiceRingRefusalDto)((ObjectResult)again).Value!).Reason,
                Is.EqualTo(VoiceRingRefusal.RecentlyDeclined),
                "a decline is the gesture people actually make, so it is the one with teeth");
        });
    }

    [Test]
    public async Task Cancel_ByTheInviter_ResolvesIt()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        var result = await Controller(Inviter).Cancel(ring.RingId, CancellationToken.None);
        var cancelled = (VoiceRingDto)((OkObjectResult)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Status, Is.EqualTo("Cancelled"));
            Assert.That(cancelled.Reason, Is.EqualTo(VoiceRingReason.InviterCancelled));
        });
    }

    [Test]
    public async Task Cancel_ByTheTarget_IsForbidden()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        var result = await Controller(Target).Cancel(ring.RingId, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<ForbidResult>(),
            "the target has a decline, and laundering it through cancel would hide it from the throttle");
    }

    // ══════════════════════════════════════════════════════════════════════════ Multiple devices
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Accept_OnASecondDeviceAfterTheFirstAnswered_IsAConflictAndDismissesOnlyThatDevice()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        await Controller(Target, "phone").Accept(ring.RingId, CancellationToken.None);
        var late = await Controller(Target, "laptop").Decline(ring.RingId, CancellationToken.None);

        var stored = await _store.LoadAsync(ring.RingId);
        var dismissal = Sent.SentMessages.Where(m => m.Method == VoiceRingService.DismissedEvent).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(((ObjectResult)late).StatusCode, Is.EqualTo(409));
            Assert.That(stored!.Status, Is.EqualTo(VoiceRingStatus.Accepted),
                "a second handset's late answer must not overturn the one the user actually gave");
            Assert.That(dismissal, Has.Count.EqualTo(1));
            Assert.That(Field(dismissal[0].Args[0], "deviceId"), Is.EqualTo("laptop"));
        });
    }

    [Test]
    public async Task Resolution_AsksForASilentCancelPushNamingTheAnsweringDevice()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        await Controller(Target, "phone").Accept(ring.RingId, CancellationToken.None);

        var cancel = Pushes.Single(p => p.Cancel);
        Assert.Multiple(() =>
        {
            Assert.That(cancel.RingId, Is.EqualTo(ring.RingId));
            Assert.That(cancel.ExcludeDeviceId, Is.EqualTo("phone"),
                "the device that answered drops its own cancel, because a token may not know which device it is");
            Assert.That(cancel.CancelReason, Is.EqualTo("Accepted"));
        });
    }

    [Test]
    public async Task Accept_WithNoDeviceHeader_StillWorks()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        var result = await Controller(Target).Accept(ring.RingId, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<OkObjectResult>(),
            "a desktop, or a build predating the header, must still be able to answer");
    }

    // ══════════════════════════════════════════════════════════════════════════ Timing out
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Timeout_ExpiresAPendingRingAndTellsBothSides()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        _clock.Advance(VoiceRing.Ttl + TimeSpan.FromSeconds(1));
        await VoiceRingTimeoutCheckHandler.Handle(new VoiceRingTimeoutCheck { RingId = ring.RingId }, _rings);

        var stored = await _store.LoadAsync(ring.RingId);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Status, Is.EqualTo(VoiceRingStatus.Expired));
            Assert.That(stored.Reason, Is.EqualTo(VoiceRingReason.TimedOut));
            Assert.That(Sent.RecipientsOf(VoiceRingService.ResolvedEvent),
                Is.EquivalentTo(new[] { Target, Inviter }));
        });
    }

    [Test]
    public async Task Timeout_LeavesAnAlreadyAcceptedRingAlone()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();
        await Controller(Target, "phone").Accept(ring.RingId, CancellationToken.None);

        _clock.Advance(VoiceRing.Ttl + TimeSpan.FromSeconds(1));
        await VoiceRingTimeoutCheckHandler.Handle(new VoiceRingTimeoutCheck { RingId = ring.RingId }, _rings);

        var stored = await _store.LoadAsync(ring.RingId);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Status, Is.EqualTo(VoiceRingStatus.Accepted));
            Assert.That(Sent.SentMessages.Count(m => m.Method == VoiceRingService.ResolvedEvent),
                Is.EqualTo(1),
                "a late timer must not re-fire the notifications for an invitation somebody accepted");
        });
    }

    [Test]
    public async Task Accept_AfterTheDeadline_IsAConflictEvenIfTheTimerHasNotFired()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        _clock.Advance(VoiceRing.Ttl + TimeSpan.FromSeconds(1));
        var result = await Controller(Target, "phone").Accept(ring.RingId, CancellationToken.None);

        var stored = await _store.LoadAsync(ring.RingId);
        Assert.Multiple(() =>
        {
            Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(409));
            Assert.That(stored!.Status, Is.EqualTo(VoiceRingStatus.Expired),
                "the deadline decides, not the scheduled message, which can be late");
        });
    }

    [Test]
    public async Task Pending_DropsARingThatHasLapsed()
    {
        await SitInAsync(ChannelId, Inviter);
        await RingAsync();

        _clock.Advance(VoiceRing.Ttl + TimeSpan.FromSeconds(1));

        var result = await Controller(Target).Pending(CancellationToken.None);
        Assert.That(((OkObjectResult)result).Value, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The channel and the inviter moving underneath it
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Accept_IsGone_WhenTheChannelWasDeletedMidRing()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        _context.Channels.Remove(_context.Channels.Single(c => c.Id == ChannelId));
        await _context.SaveChangesAsync();

        var result = await Controller(Target).Accept(ring.RingId, CancellationToken.None);
        var stored = await _store.LoadAsync(ring.RingId);

        Assert.Multiple(() =>
        {
            Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(410));
            Assert.That(stored!.Reason, Is.EqualTo(VoiceRingReason.ChannelGone));
        });
    }

    [Test]
    public async Task Accept_IsGone_WhenTheTargetLostAccessMidRing()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        // The role is stripped and the resolved-permission cache with it, which is what an overwrite
        // change actually does.
        var role = _context.Roles.Single(r => r.Id == "role-open");
        role.Permissions = Permissions.None;
        await _context.SaveChangesAsync();
        foreach (var key in _cache.Keys.Where(k => k.StartsWith("guild:")).ToList())
            await _cache.RemoveAsync(key);

        var result = await Controller(Target).Accept(ring.RingId, CancellationToken.None);

        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(410));
    }

    [Test]
    public async Task InviterLeavingTheChannel_CancelsTheirOutstandingRings()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        await _rings.CancelForInviterLeftAsync(ChannelId, Inviter);

        var stored = await _store.LoadAsync(ring.RingId);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.Status, Is.EqualTo(VoiceRingStatus.Cancelled));
            Assert.That(stored.Reason, Is.EqualTo(VoiceRingReason.InviterLeft));
        });
    }

    [Test]
    public async Task InviterLeaving_LeavesSomebodyElsesRingIntoTheSameChannelAlone()
    {
        await SitInAsync(ChannelId, Inviter, Bystander);
        var mine = await RingAsync();
        await Controller(Bystander)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        await _rings.CancelForInviterLeftAsync(ChannelId, Inviter);

        var theirs = (await _store.PendingForChannelAsync(ChannelId)).ToList();
        var stored = await _store.LoadAsync(mine.RingId);

        Assert.Multiple(() =>
        {
            Assert.That(stored!.Status, Is.EqualTo(VoiceRingStatus.Cancelled));
            Assert.That(theirs.Select(r => r.InviterId), Is.EqualTo(new[] { Bystander }));
        });
    }

    [Test]
    public async Task TargetJoiningByOtherMeans_ClosesTheRingWithoutCountingAsARefusal()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        await _rings.CancelForTargetJoinedAsync(ChannelId, Target);

        var stored = await _store.LoadAsync(ring.RingId);

        // Not a decline, so the inviter is not locked out - they may ring again straight away.
        await SitInAsync(SecondChannelId, Inviter);
        var again = await Controller(Inviter)
            .Ring(GuildId, SecondChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(stored!.Reason, Is.EqualTo(VoiceRingReason.TargetJoined));
            Assert.That(again, Is.InstanceOf<OkObjectResult>(),
                "somebody who walked in may never have seen the invitation, so it is not a no");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Push
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Push_CarriesTheLocalizationKeyWithTheChannelNameAsAnArgument()
    {
        await SitInAsync(ChannelId, Inviter);
        await RingAsync();

        var push = Pushes.Single(p => !p.Cancel);
        Assert.Multiple(() =>
        {
            Assert.That(push.Title, Is.EqualTo("Inviter"), "the inviter's own name has nothing to translate");
            Assert.That(push.BodyLocKey, Is.EqualTo(VoiceLocKeys.InviteBody));
            Assert.That(push.BodyLocArgs, Is.EqualTo(new[] { "General" }));
            Assert.That(push.Body, Is.EqualTo("Asked you to join General."),
                "the English always travels too, for a bundle with no translation of the key");
            Assert.That(push.ExpiresInSeconds, Is.EqualTo((int)VoiceRing.Ttl.TotalSeconds));
        });
    }

    [Test]
    public async Task Push_SendsOnlyKeysThatTheClientsHaveBeenToldAbout()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();
        await Controller(Target).Decline(ring.RingId, CancellationToken.None);

        var keys = Pushes
            .SelectMany(p => new[] { p.BodyLocKey })
            .Where(k => k is not null)
            .ToList();

        Assert.That(keys, Is.SubsetOf(VoiceLocKeys.All),
            "a key that exists in C# and in nobody's resource bundle arrives on a phone as its own name");
    }

    [Test]
    public async Task Push_IsSkipped_WhenTheTargetTurnedMobilePushOffForThisGuild()
    {
        await SitInAsync(ChannelId, Inviter);
        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnot-1", MemberId = "member-target", MobilePush = false,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        await RingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Pushes.Where(p => !p.Cancel), Is.Empty,
                "somebody who turned this server's phone notifications off asked not to be buzzed by it");
            Assert.That(Sent.RecipientsOf(VoiceRingService.IncomingEvent), Is.EqualTo(new[] { Target }),
                "the card still appears in an app they already have open, which is not an interruption");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The durable half: the invitation in the direct conversation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Ring_AsksMessagingToLeaveTheInvitationInTheDirectConversation()
    {
        await SitInAsync(ChannelId, Inviter);

        var ring = await RingAsync();
        var request = DirectMessages.SingleOrDefault();

        Assert.Multiple(() =>
        {
            Assert.That(request, Is.Not.Null,
                "a ring lasts a minute; the message is the only surface still there afterwards");
            Assert.That(request!.RingId, Is.EqualTo(ring.RingId));
            Assert.That(request.InviterId, Is.EqualTo(Inviter));
            Assert.That(request.TargetUserId, Is.EqualTo(Target));
            Assert.That(request.GuildId, Is.EqualTo(GuildId));
            Assert.That(request.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(request.ChannelName, Is.EqualTo("General"),
                "the card names the channel, and only a target already checked for ViewChannel gets one");
            Assert.That(request.ExpiresAt, Is.EqualTo(_clock.GetUtcNow().Add(VoiceRing.Ttl)),
                "an absolute instant, because unlike the push this is re-read months later");
            Assert.That(request.ExpiresAt.Offset, Is.EqualTo(TimeSpan.Zero),
                "a ring round-trips through Redis as JSON; an Unspecified kind would be read as local time");
        });
    }

    [Test]
    public async Task DirectMessage_IsStillRequested_WhenTheTargetTurnedMobilePushOffForThisGuild()
    {
        await SitInAsync(ChannelId, Inviter);
        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = "gnot-1", MemberId = "member-target", MobilePush = false,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        await RingAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Pushes.Where(p => !p.Cancel), Is.Empty);
            Assert.That(DirectMessages, Has.Count.EqualTo(1),
                "a mute is a request not to be interrupted, not to be left out of your own message history");
        });
    }

    [Test]
    public async Task DirectMessage_IsNotRequestedASecondTime_WhenTheRingIsAnswered()
    {
        await SitInAsync(ChannelId, Inviter);
        var ring = await RingAsync();

        await Controller(Target).Accept(ring.RingId, CancellationToken.None);

        Assert.That(DirectMessages, Has.Count.EqualTo(1),
            "the card carries the expiry and the client compares it to its own clock, so there is nothing to rewrite");
    }

    [Test]
    public async Task DirectMessage_IsNotRequested_WhenTheRingIsRefused()
    {
        // Nobody sitting in the channel, so the inviter is not in the room and the ring never
        // exists. The refusal paths must not leave an invitation behind in a conversation.
        var result = await Controller(Inviter)
            .Ring(GuildId, ChannelId, new RingVoiceChannelDto(Target), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.InstanceOf<OkObjectResult>());
            Assert.That(DirectMessages, Is.Empty);
        });
    }
}
