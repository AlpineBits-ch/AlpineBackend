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
        _service = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
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

    private static IEnumerable<ExternalPermission> AllExternalPermissions() => Enum.GetValues<ExternalPermission>();

    /// <summary>Whether a Community guild (what SeedOwner creates) offers this permission at all.
    /// Household-module permissions are gated off there for everyone, owner included.</summary>
    private static bool IsAvailableToCommunityGuild(ExternalPermission permission) =>
        permission is not (ExternalPermission.ManageLists or ExternalPermission.AddListItems
            or ExternalPermission.CheckOffListItems or ExternalPermission.ManageChores
            or ExternalPermission.CompleteChores or ExternalPermission.ManageLedger
            or ExternalPermission.AddExpenses or ExternalPermission.ManagePantry
            or ExternalPermission.CreateDecisions or ExternalPermission.VoteDecisions
            or ExternalPermission.ManageGuests);
}
