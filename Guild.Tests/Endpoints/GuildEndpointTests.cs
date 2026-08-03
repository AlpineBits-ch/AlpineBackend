using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Messaging.Contracts.Bus.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Contracts.Bus.Integration.Request;
using Social.Contracts.Bus.Integration.Response;
using Social.Contracts.Dtos;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers GuildEndpoint: CreateGuild (including the system-channel FK-ordering hack that
/// legitimately calls SaveChangesAsync manually), DeleteGuild (owner-only, also manually
/// commits), and UpdateGuild (ManageGuild-gated, Wolverine-committed - test calls
/// SaveChangesAsync itself to simulate the middleware).
/// </summary>
[TestFixture]
public class GuildEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private FakeInvokingMessageBus _bus = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private GuildEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _bus = new FakeInvokingMessageBus();
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new GuildEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild(string ownerId = OwnerId, string id = GuildId) => new()
    {
        Id = id, OwnerId = ownerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static ProfileDto MakeProfile(string userId) => new()
    {
        Id = userId, UserId = userId, UserName = "Tester", Hash = 1234, AvatarUrl = "", BannerUrl = "",
    };

    private async Task<GuildMember> SeedManagerMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageGuild, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var member = new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" };
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
        return member;
    }

    // ══════════════════════════════════════════════════════════════════════ CreateGuild
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateGuild_ProfileNotFound_ReturnsBadRequest()
    {
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = null });

        var result = await _endpoint.CreateGuild(new CreateGuildDto { Name = "My Guild" }, _context, TestPrincipal.Create(UserId), _bus);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateGuild_Valid_PersistsGuildWithSystemChannel()
    {
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        var result = await _endpoint.CreateGuild(new CreateGuildDto { Name = "My Guild", Description = "d" }, _context, TestPrincipal.Create(UserId), _bus);
        // The FK-ordering hack's final "restore SystemChannelId" assignment relies on Wolverine's
        // outer auto-transaction to actually persist it - simulate that here, same convention as
        // every other Wolverine-committed endpoint in this suite.
        await _context.SaveChangesAsync();

        var ok = result as Ok<GuildDto>;
        Assert.That(ok, Is.Not.Null);
        var guildId = ok!.Value!.Id;

        var reloaded = await _context.Guilds.AsNoTracking().FirstOrDefaultAsync(g => g.Id == guildId);
        Assert.That(reloaded, Is.Not.Null, "CreateGuild commits manually via the FK-ordering hack");
        Assert.That(reloaded!.OwnerId, Is.EqualTo(UserId));
        Assert.That(reloaded.SystemChannelId, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task CreateGuild_Valid_SendsSystemJoinMessage()
    {
        _bus.SetResponse<GetProfileByUserIdRequest>(new GetProfileByUserIdResponse { Profile = MakeProfile(UserId) });

        await _endpoint.CreateGuild(new CreateGuildDto { Name = "My Guild" }, _context, TestPrincipal.Create(UserId), _bus);

        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>().Any(c => c.Type == MessageType.GuildMemberJoin), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteGuild
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteGuild_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.DeleteGuild(GuildId, _context, TestPrincipal.CreateAnonymous(), _hub);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task DeleteGuild_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.DeleteGuild("nonexistent", _context, TestPrincipal.Create(UserId), _hub);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteGuild_NotOwner_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteGuild(GuildId, _context, TestPrincipal.Create(UserId), _hub);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteGuild_Owner_RemovesGuildAndNotifiesMembers()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteGuild(GuildId, _context, TestPrincipal.Create(OwnerId), _hub);

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.Guilds.AsNoTracking().AnyAsync(g => g.Id == GuildId), Is.False, "DeleteGuild commits manually");
        var hubClients = (FakeHubClients)_hub.Clients;
        Assert.That(hubClients.SentMessages.Any(m => m.Method == "guild.GuildDeleted"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateGuild
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateGuild_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.UpdateGuild(GuildId, new UpdateGuildDto { Name = "x" }, _context, TestPrincipal.CreateAnonymous(), _permissionService, _auditLog, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UpdateGuild_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.UpdateGuild("nonexistent", new UpdateGuildDto { Name = "x" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateGuild_LacksManageGuild_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.UpdateGuild(GuildId, new UpdateGuildDto { Name = "x" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateGuild_SystemChannelNotTextOrAnnouncement_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var voiceChannel = Channel.Create(new CreateChannelParams { Name = "voice", Type = ChannelType.Voice, GuildId = GuildId, Description = "d" });
        _context.Channels.Add(voiceChannel);
        await _context.SaveChangesAsync();

        var result = await _endpoint.UpdateGuild(GuildId, new UpdateGuildDto { Name = "x", SystemChannelId = voiceChannel.Id }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateGuild_SystemChannelNotInGuild_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var result = await _endpoint.UpdateGuild(GuildId, new UpdateGuildDto { Name = "x", SystemChannelId = "nonexistent-channel" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ── DefaultMessageNotifications ─────────────────────────────────────── Discord's
    // default_message_notifications: what a member who has set nothing falls back to.

    [Test]
    public async Task UpdateGuild_DefaultMessageNotifications_IsPersisted()
    {
        await SeedManagerMember();

        var result = await _endpoint.UpdateGuild(
            GuildId,
            new UpdateGuildDto { Name = "x", DefaultMessageNotifications = NotificationLevel.OnlyMentions },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<GuildDto>>());
            Assert.That(_context.Guilds.Single(g => g.Id == GuildId).DefaultMessageNotifications,
                Is.EqualTo(NotificationLevel.OnlyMentions));
        });
    }

    /// <summary>Omitting the field must leave the setting alone rather than resetting it, the same
    /// contract SystemChannelId documents - otherwise any older client doing a name edit silently
    /// reverts the guild to AllMessages.</summary>
    [Test]
    public async Task UpdateGuild_DefaultMessageNotificationsOmitted_LeavesItUntouched()
    {
        await SeedManagerMember();
        _context.Guilds.Single(g => g.Id == GuildId).DefaultMessageNotifications = NotificationLevel.OnlyMentions;
        await _context.SaveChangesAsync();

        await _endpoint.UpdateGuild(
            GuildId, new UpdateGuildDto { Name = "renamed" },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);

        Assert.That(_context.Guilds.Single(g => g.Id == GuildId).DefaultMessageNotifications,
            Is.EqualTo(NotificationLevel.OnlyMentions));
    }

    [Test]
    public async Task UpdateGuild_DefaultMessageNotificationsNothing_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var result = await _endpoint.UpdateGuild(
            GuildId,
            new UpdateGuildDto { Name = "x", DefaultMessageNotifications = NotificationLevel.Nothing },
            _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(_context.Guilds.Single(g => g.Id == GuildId).DefaultMessageNotifications,
                Is.EqualTo(NotificationLevel.AllMessages), "the rejected value must not have been written");
        });
    }

    [Test]
    public async Task UpdateGuild_NewGuild_DefaultsToAllMessages()
    {
        await SeedManagerMember();

        Assert.That(_context.Guilds.Single(g => g.Id == GuildId).DefaultMessageNotifications,
            Is.EqualTo(NotificationLevel.AllMessages),
            "existing guilds must keep behaving exactly as they did before this field existed");
    }

    [Test]
    public async Task UpdateGuild_Valid_UpdatesNameAndDescription()
    {
        await SeedManagerMember();

        var result = await _endpoint.UpdateGuild(GuildId, new UpdateGuildDto { Name = "New Name", Description = "New Desc" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<GuildDto>>());
        var reloaded = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(reloaded.Name, Is.EqualTo("New Name"));
        Assert.That(reloaded.Description, Is.EqualTo("New Desc"));
    }

    [Test]
    public async Task UpdateGuild_ValidSystemChannel_UpdatesSystemChannelId()
    {
        await SeedManagerMember();
        var textChannel = Channel.Create(new CreateChannelParams { Name = "announcements", Type = ChannelType.Announcement, GuildId = GuildId, Description = "d" });
        _context.Channels.Add(textChannel);
        await _context.SaveChangesAsync();

        await _endpoint.UpdateGuild(GuildId, new UpdateGuildDto { Name = "x", SystemChannelId = textChannel.Id }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(reloaded.SystemChannelId, Is.EqualTo(textChannel.Id));
    }

    [Test]
    public async Task UpdateGuild_WithVerificationLevel_UpdatesLevel()
    {
        await SeedManagerMember();

        await _endpoint.UpdateGuild(GuildId, new UpdateGuildDto { Name = "x", VerificationLevel = GuildVerificationLevel.High }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Guilds.AsNoTracking().FirstAsync(g => g.Id == GuildId);
        Assert.That(reloaded.VerificationLevel, Is.EqualTo(GuildVerificationLevel.High));
    }

    [Test]
    public async Task UpdateGuild_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();

        await _endpoint.UpdateGuild(GuildId, new UpdateGuildDto { Name = "x" }, _context, TestPrincipal.Create(UserId), _permissionService, _auditLog, _hub, _hydrateService);
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == GuildId).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ActionType, Is.EqualTo(AuditActionType.GuildUpdated));
    }
}
