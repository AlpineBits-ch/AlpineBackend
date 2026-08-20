using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Endpoints.Channel;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
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

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers ChannelEndpoint: CreateChannel (type gating + ManageChannel permission), Delete/Update
/// (not-found + ManageChannel gating), and ReorderChannels (category/channel membership
/// validation). All Wolverine-committed - tests call SaveChangesAsync themselves afterward.
/// </summary>
[TestFixture]
public class ChannelEndpointTests
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
    private FakeMessageBus _bus = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private ChannelEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _bus = new FakeMessageBus();
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new ChannelEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedManagerMember()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "manager", Permissions = Permissions.ManageChannel, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private async Task<Channel> SeedChannel(ChannelType type = ChannelType.Text)
    {
        var channel = Channel.Create(new CreateChannelParams { Name = "general", Type = type, GuildId = GuildId, Description = "d" });
        _context.Channels.Add(channel);
        await _context.SaveChangesAsync();
        return channel;
    }

    // ══════════════════════════════════════════════════════════════════════ CreateChannel
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateChannel_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateChannel(GuildId, new CreateChannelDto { Name = "c", Type = ChannelType.Text },
            _permissionService, _context, _hub, _hydrateService, new ChannelPrivacyService(_context), _bus, NullLogger<ChannelEndpoint>.Instance, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateChannel_NonCreatableType_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var result = await _endpoint.CreateChannel(GuildId, new CreateChannelDto { Name = "c", Type = ChannelType.Thread },
            _permissionService, _context, _hub, _hydrateService, new ChannelPrivacyService(_context), _bus, NullLogger<ChannelEndpoint>.Instance, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateChannel_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateChannel(GuildId, new CreateChannelDto { Name = "c", Type = ChannelType.Text },
            _permissionService, _context, _hub, _hydrateService, new ChannelPrivacyService(_context), _bus, NullLogger<ChannelEndpoint>.Instance, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateChannel_Valid_PersistsChannelAndPublishesForBots()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateChannel(GuildId, new CreateChannelDto { Name = "new-channel", Type = ChannelType.Text, Position = 2 },
            _permissionService, _context, _hub, _hydrateService, new ChannelPrivacyService(_context), _bus, NullLogger<ChannelEndpoint>.Instance, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
        Assert.That(await _context.Channels.AsNoTracking().AnyAsync(c => c.Name == "new-channel" && c.GuildId == GuildId), Is.True);
        Assert.That(_bus.Published.OfType<ChannelCreatedForBots>().Any(e => e.Name == "new-channel"), Is.True);
    }

    [Test]
    public async Task CreateChannel_ForumType_IsCreatable()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateChannel(GuildId, new CreateChannelDto { Name = "forum", Type = ChannelType.Forum },
            _permissionService, _context, _hub, _hydrateService, new ChannelPrivacyService(_context), _bus, NullLogger<ChannelEndpoint>.Instance, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteChannelAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteChannel_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.DeleteChannelAsync("nonexistent", _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task DeleteChannel_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.DeleteChannelAsync("nonexistent", _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteChannel_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        var channel = await SeedChannel();

        var result = await _endpoint.DeleteChannelAsync(channel.Id, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteChannel_Valid_RemovesChannelAndPublishesForBots()
    {
        await SeedManagerMember();
        var channel = await SeedChannel();

        var result = await _endpoint.DeleteChannelAsync(channel.Id, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.Channels.AsNoTracking().AnyAsync(c => c.Id == channel.Id), Is.False);
        Assert.That(_bus.Published.OfType<ChannelDeletedForBots>().Any(e => e.ChannelId == channel.Id), Is.True);
    }

    [Test]
    public async Task DeleteChannel_Valid_WritesAuditLogEntry()
    {
        await SeedManagerMember();
        var channel = await SeedChannel();

        await _endpoint.DeleteChannelAsync(channel.Id, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.ChannelDeleted).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DeleteChannel_MessageThread_DetachesTheStarterMessage()
    {
        await SeedManagerMember();
        var parent = await SeedChannel();
        var thread = Channel.Create(new CreateChannelParams
        {
            Name = "thread", Type = ChannelType.Thread, GuildId = GuildId, Description = "",
            ParentChannelId = parent.Id, CreatedByUserId = UserId, StarterMessageId = "mesg_1",
        });
        _context.Channels.Add(thread);
        await _context.SaveChangesAsync();

        await _endpoint.DeleteChannelAsync(thread.Id, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var detach = _bus.Published.OfType<DetachThreadFromMessageCommand>().Single();
        Assert.That(detach.MessageId, Is.EqualTo("mesg_1"));
        Assert.That(detach.ThreadId, Is.EqualTo(thread.Id));
    }

    [Test]
    public async Task DeleteChannel_PlainChannel_PublishesNoDetach()
    {
        await SeedManagerMember();
        var channel = await SeedChannel();

        await _endpoint.DeleteChannelAsync(channel.Id, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(_bus.Published.OfType<DetachThreadFromMessageCommand>(), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateChannelAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateChannel_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.UpdateChannelAsync("nonexistent", new UpdateChannelDto { Name = "x" }, _permissionService, _context, _hub, _hydrateService, _auditLog, new ChannelPrivacyService(_context), _bus, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UpdateChannel_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.UpdateChannelAsync("nonexistent", new UpdateChannelDto { Name = "x" }, _permissionService, _context, _hub, _hydrateService, _auditLog, new ChannelPrivacyService(_context), _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateChannel_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        var channel = await SeedChannel();

        var result = await _endpoint.UpdateChannelAsync(channel.Id, new UpdateChannelDto { Name = "x" }, _permissionService, _context, _hub, _hydrateService, _auditLog, new ChannelPrivacyService(_context), _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateChannel_Valid_UpdatesFieldsAndPublishesForBots()
    {
        await SeedManagerMember();
        var channel = await SeedChannel();

        var result = await _endpoint.UpdateChannelAsync(channel.Id, new UpdateChannelDto { Name = "renamed", Description = "new desc", SlowModeSeconds = 5 },
            _permissionService, _context, _hub, _hydrateService, _auditLog, new ChannelPrivacyService(_context), _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
        var reloaded = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == channel.Id);
        Assert.That(reloaded.Name, Is.EqualTo("renamed"));
        Assert.That(reloaded.SlowModeSeconds, Is.EqualTo(5));
        Assert.That(_bus.Published.OfType<ChannelUpdatedForBots>().Any(e => e.ChannelId == channel.Id), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ ReorderChannels
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReorderChannels_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.ReorderChannels(GuildId, new ReorderChannelsDto(), _context, TestPrincipal.CreateAnonymous(), _hub, _hydrateService, _permissionService);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task ReorderChannels_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.ReorderChannels(GuildId, new ReorderChannelsDto(), _context, TestPrincipal.Create(UserId), _hub, _hydrateService, _permissionService);
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ReorderChannels_UnknownCategoryId_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var dto = new ReorderChannelsDto { Categories = [new CategoryPositionDto { CategoryId = "nonexistent", Position = 0 }] };
        var result = await _endpoint.ReorderChannels(GuildId, dto, _context, TestPrincipal.Create(UserId), _hub, _hydrateService, _permissionService);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ReorderChannels_UnknownChannelId_ReturnsBadRequest()
    {
        await SeedManagerMember();

        var dto = new ReorderChannelsDto { Channels = [new ChannelPositionDto { ChannelId = "nonexistent", Position = 0 }] };
        var result = await _endpoint.ReorderChannels(GuildId, dto, _context, TestPrincipal.Create(UserId), _hub, _hydrateService, _permissionService);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ReorderChannels_ChannelReferencesUnknownCategory_ReturnsBadRequest()
    {
        await SeedManagerMember();
        var channel = await SeedChannel();

        var dto = new ReorderChannelsDto { Channels = [new ChannelPositionDto { ChannelId = channel.Id, Position = 0, CategoryId = "nonexistent-category" }] };
        var result = await _endpoint.ReorderChannels(GuildId, dto, _context, TestPrincipal.Create(UserId), _hub, _hydrateService, _permissionService);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ReorderChannels_Valid_UpdatesPositions()
    {
        await SeedManagerMember();
        var category = Category.Create(new CreateCategoryParams { Name = "cat", GuildId = GuildId, Position = 0 });
        _context.Categories.Add(category);
        var channel = await SeedChannel();

        var dto = new ReorderChannelsDto
        {
            Categories = [new CategoryPositionDto { CategoryId = category.Id, Position = 3 }],
            Channels = [new ChannelPositionDto { ChannelId = channel.Id, Position = 7, CategoryId = category.Id }],
        };
        var result = await _endpoint.ReorderChannels(GuildId, dto, _context, TestPrincipal.Create(UserId), _hub, _hydrateService, _permissionService);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var reloadedCategory = await _context.Categories.AsNoTracking().FirstAsync(c => c.Id == category.Id);
        var reloadedChannel = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == channel.Id);
        Assert.That(reloadedCategory.Position, Is.EqualTo(3));
        Assert.That(reloadedChannel.Position, Is.EqualTo(7));
        Assert.That(reloadedChannel.CategoryId, Is.EqualTo(category.Id));
    }
}
