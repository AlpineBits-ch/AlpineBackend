using Guild.Application.Bus.Consumers;
using Guild.Application.Services;
using Guild.Contracts;
using Guild.Contracts.Bus.Request;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Bus.Consumers;

/// <summary>
/// Covers HasUserPermissionToGuildHandler and HasUserPermissionsHandler - the bus-facing surface
/// other services use to ask "can this user do X", each with its own ExternalPermission ->
/// internal Permissions mapping switch (see PermissionsParityTests for the enum-shape parity
/// check; this covers the runtime mapping/dispatch behavior instead).
/// </summary>
[TestFixture]
public class HasUserPermissionHandlersTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ChannelId = "channel-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _service = PermissionTestFactory.Create(_cache, _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedOwner()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "c", Description = "d", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════
    // HasUserPermissionToGuildHandler
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GuildHandler_EchoesRequestFields()
    {
        await SeedOwner();
        var response = await HasUserPermissionToGuildHandler.Handle(
            new HasUserPermissionToGuildRequest { UserId = OwnerId, GuildId = GuildId, Permission = ExternalPermission.ManageGuild },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.UserId, Is.EqualTo(OwnerId));
            Assert.That(response.GuildId, Is.EqualTo(GuildId));
            Assert.That(response.Permission, Is.EqualTo(ExternalPermission.ManageGuild));
        });
    }

    [Test]
    public async Task GuildHandler_Owner_IsAllowedTrue()
    {
        await SeedOwner();
        var response = await HasUserPermissionToGuildHandler.Handle(
            new HasUserPermissionToGuildRequest { UserId = OwnerId, GuildId = GuildId, Permission = ExternalPermission.BanMembers },
            _service);

        Assert.That(response.IsAllowed, Is.True);
    }

    [Test]
    public async Task GuildHandler_NonMember_IsAllowedFalse()
    {
        await SeedOwner();
        var response = await HasUserPermissionToGuildHandler.Handle(
            new HasUserPermissionToGuildRequest { UserId = UserId, GuildId = GuildId, Permission = ExternalPermission.BanMembers },
            _service);

        Assert.That(response.IsAllowed, Is.False);
    }

    [TestCaseSource(nameof(AllExternalPermissions))]
    public async Task GuildHandler_EveryExternalPermission_MapsWithoutThrowing(ExternalPermission permission)
    {
        await SeedOwner();
        // The point of this case is that MapToInternal covers every contract value - an unmapped
        // one throws ArgumentOutOfRangeException before any permission logic runs.
        var response = await HasUserPermissionToGuildHandler.Handle(
            new HasUserPermissionToGuildRequest { UserId = OwnerId, GuildId = GuildId, Permission = permission },
            _service);

        Assert.That(response.IsAllowed, Is.EqualTo(IsAvailableToCommunityGuild(permission)));
    }

    // ══════════════════════════════════════════════════════════════════════
    // HasUserPermissionsHandler (channel-scoped)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ChannelHandler_EchoesRequestFields()
    {
        await SeedOwner();
        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = OwnerId, ChannelId = ChannelId, Permission = ExternalPermission.ViewChannel },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.UserId, Is.EqualTo(OwnerId));
            Assert.That(response.ChannelId, Is.EqualTo(ChannelId));
            Assert.That(response.Permission, Is.EqualTo(ExternalPermission.ViewChannel));
        });
    }

    [Test]
    public async Task ChannelHandler_Owner_IsAllowedTrue()
    {
        await SeedOwner();
        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = OwnerId, ChannelId = ChannelId, Permission = ExternalPermission.SendMessages },
            _service);

        Assert.That(response.IsAllowed, Is.True);
    }

    [Test]
    public async Task ChannelHandler_NonMember_IsAllowedFalse()
    {
        await SeedOwner();
        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = UserId, ChannelId = ChannelId, Permission = ExternalPermission.SendMessages },
            _service);

        Assert.That(response.IsAllowed, Is.False);
    }

    [TestCaseSource(nameof(AllExternalPermissions))]
    public async Task ChannelHandler_EveryExternalPermission_MapsWithoutThrowing(ExternalPermission permission)
    {
        await SeedOwner();
        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = OwnerId, ChannelId = ChannelId, Permission = permission },
            _service);

        // Same reasoning as the guild-scoped case above.
        Assert.That(response.IsAllowed, Is.EqualTo(IsAvailableToCommunityGuild(permission)));
    }

    // ══════════════════════════════════════════════════════════════════════
    // SlowModeSeconds / CanBypassSlowMode ride-along
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Seeds a guild whose only channel has slowmode configured, plus a plain member
    /// holding just SendMessages (so they are allowed to post but not exempt).</summary>
    private async Task SeedSlowModeChannel(int slowModeSeconds, Permissions memberPermissions)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "c", Description = "d", Type = ChannelType.Text,
            SlowModeSeconds = slowModeSeconds,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-member", GuildId = GuildId, Name = "member", Permissions = memberPermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = "USER-1",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = "role-member", MemberId = "member-1",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task ChannelHandler_SendMessages_CarriesSlowModeSeconds()
    {
        await SeedSlowModeChannel(30, Permissions.ViewChannel | Permissions.SendMessages);

        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = UserId, ChannelId = ChannelId, Permission = ExternalPermission.SendMessages },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.IsAllowed, Is.True);
            Assert.That(response.SlowModeSeconds, Is.EqualTo(30));
            Assert.That(response.CanBypassSlowMode, Is.False);
        });
    }

    [Test]
    public async Task ChannelHandler_SendMessages_ManageChannelHolderCanBypass()
    {
        await SeedSlowModeChannel(30, Permissions.ViewChannel | Permissions.SendMessages | Permissions.ManageChannel);

        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = UserId, ChannelId = ChannelId, Permission = ExternalPermission.SendMessages },
            _service);

        Assert.That(response.CanBypassSlowMode, Is.True);
    }

    [Test]
    public async Task ChannelHandler_NonSendMessagesPermission_DoesNotResolveSlowMode()
    {
        await SeedSlowModeChannel(30, Permissions.ViewChannel | Permissions.SendMessages);

        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = UserId, ChannelId = ChannelId, Permission = ExternalPermission.ViewChannel },
            _service);

        Assert.That(response.SlowModeSeconds, Is.Zero,
            "only the send path acts on it - every other query would pay for a value it discards");
    }

    [Test]
    public async Task ChannelHandler_DeniedSendMessages_DoesNotResolveSlowMode()
    {
        await SeedSlowModeChannel(30, Permissions.ViewChannel);

        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = UserId, ChannelId = ChannelId, Permission = ExternalPermission.SendMessages },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.IsAllowed, Is.False);
            Assert.That(response.SlowModeSeconds, Is.Zero);
        });
    }

    [Test]
    public async Task ChannelHandler_SlowModeOff_SkipsTheBypassResolutionEntirely()
    {
        await SeedSlowModeChannel(0, Permissions.ViewChannel | Permissions.SendMessages | Permissions.ManageChannel);

        var response = await HasUserPermissionsHandler.Handle(
            new HasUserPermissionToChannelRequest { UserId = UserId, ChannelId = ChannelId, Permission = ExternalPermission.SendMessages },
            _service);

        Assert.Multiple(() =>
        {
            Assert.That(response.SlowModeSeconds, Is.Zero);
            Assert.That(response.CanBypassSlowMode, Is.False,
                "there is nothing to be exempt from, so the extra resolution is skipped");
        });
    }

    private static IEnumerable<ExternalPermission> AllExternalPermissions() => Enum.GetValues<ExternalPermission>();

    /// <summary>
    /// Whether a Community guild (what SeedOwner creates) offers this permission at all.
    /// </summary>
    private static bool IsAvailableToCommunityGuild(ExternalPermission permission) => true;
}
