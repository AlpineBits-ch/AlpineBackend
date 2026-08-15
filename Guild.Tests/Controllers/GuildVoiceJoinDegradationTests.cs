using System.Text.Json.Nodes;
using Echo.Entitlements.Keys;
using Echo.Entitlements.Model;
using Echo.Entitlements.Sources;
using Echo.Realtime.Caching;
using Echo.Realtime.Devices;
using Echo.Voice.Rooms;
using Echo.Voice.Testing;
using Echo.Voice.Transport;
using Guild.Application.Controllers;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Controllers;

/// <summary>A channel join that was reduced says so on the reply that caused it.</summary>
[TestFixture]
public class GuildVoiceJoinDegradationTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "channel-1";
    private const string FirstUser = "user-first";
    private const string SecondUser = "user-second";

    /// <summary>One seat, which is simply the smallest room that has an overflow.</summary>
    private static readonly OperatorCeilings OneSeat = new(
        new Dictionary<EntitlementKey, EntitlementValue>
        {
            [EntitlementKeys.VoiceMaxParticipants] = EntitlementValue.OfNumber(1),
        });

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private GuildPermissionService _permissions = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _permissions = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance);

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = FirstUser, Name = GuildId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = ChannelId, Description = "d",
            Type = ChannelType.Voice,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-1", GuildId = GuildId, Name = "member",
            Permissions = Permissions.ViewChannel | Permissions.Connect | Permissions.Speak,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        SeedMember(FirstUser, "member-first");
        SeedMember(SecondUser, "member-second");

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private void SeedMember(string userId, string memberId)
    {
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{userId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rm-{memberId}", RoleId = "role-1", MemberId = memberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    private GuildVoiceController ControllerFor(string userId, OperatorCeilings? ceilings)
    {
        var locks = new FakeDistributedLockService();

        return new GuildVoiceController(
            _permissions, _hub, _cache,
            new CloudflareMediaTransport(StubCloudflareHttp.CreateService()), _context,
            new DeviceIdResolver(_bus, _cache, NullLogger<DeviceIdResolver>.Instance),
            new GuildVoiceActivityStore(locks, _cache),
            new StreamViewerStore(locks, _cache),
            new VoiceRoomService(
                VoiceTestHarness.StoreFor(_cache, locks), new VoiceAnnouncer(_hub),
                operatorCeilings: ceilings),
            VoiceTestHarness.StoreFor(_cache, locks),
            VoiceRingTestFactory.Create(_context, _cache, locks, _hub, _bus), _bus)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = TestPrincipal.Create(userId) },
            },
        };
    }

    private async Task<object?> JoinAsync(string userId, OperatorCeilings? ceilings) =>
        ((OkObjectResult)await ControllerFor(userId, ceilings)
            .Join(GuildId, ChannelId, CancellationToken.None)).Value;

    [Test]
    public async Task The_joiner_past_the_ceiling_is_admitted_and_told_what_it_cost()
    {
        await JoinAsync(FirstUser, OneSeat);

        var body = await JoinAsync(SecondUser, OneSeat) as JsonObject;

        Assert.Multiple(async () =>
        {
            Assert.That(body, Is.Not.Null,
                "a reduced join is a 200 carrying the normal body plus the array");
            Assert.That(body!["participants"], Is.Not.Null,
                "and the snapshot the client already parses is still all of it");

            var degradations = body["degradations"]!.AsArray();
            Assert.That(degradations, Has.Count.EqualTo(1));
            Assert.That((string?)degradations[0]!["key"],
                Is.EqualTo(EntitlementKeys.VoiceMaxParticipants.Name));
            Assert.That((string?)degradations[0]!["reason"], Is.EqualTo("operator_ceiling"));

            var room = await VoiceTestHarness.ReadRoomAsync(_cache, VoiceRoomKey.Channel(ChannelId));
            Assert.That(room!.Participants.Select(p => p.UserId), Does.Contain(SecondUser),
                "degrade, do not deny - the eleventh member of a ten-person room is in the channel");
        });
    }

    [Test]
    public async Task A_join_inside_the_ceiling_is_byte_identical_to_what_a_v1_client_receives()
    {
        var body = await JoinAsync(FirstUser, OneSeat);

        Assert.That(body, Is.InstanceOf<VoiceRoomSnapshot>(),
            "absent and empty mean the same thing, and absent is the reply this endpoint has always "
            + "given");
    }

    [Test]
    public async Task A_box_with_no_ceilings_and_no_resolver_never_reduces_anybody()
    {
        await JoinAsync(FirstUser, null);

        var body = await JoinAsync(SecondUser, null);

        Assert.That(body, Is.InstanceOf<VoiceRoomSnapshot>(),
            "which is every deployment today, and the join reply must not change shape for them");
    }
}
