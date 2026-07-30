using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
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
/// Covers ThreadEndpoint: CreateThread (Text/Forum parent gating + CreateThreads permission +
/// optional initial-post message), GetThreads (manually-built DTO list, a documented workaround
/// for a real prior bug in the generic Facet materialization), and ArchiveThread (creator vs
/// moderator permission split: ManageOwnThreads vs ManageAnyThread).
/// </summary>
[TestFixture]
public class ThreadEndpointTests
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
    private ThreadEndpoint _endpoint = null!;

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
        _endpoint = new ThreadEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Seeds a member holding CreateThreads/ManageAnyThread on every channel via the
    /// Everyone role (GuildPermissionService resolves channel perms from role aggregation).</summary>
    private async Task<Channel> SeedMemberAndParentChannel(Permissions rolePermissions, ChannelType parentType = ChannelType.Text)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone, Permissions = rolePermissions, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var parent = Channel.Create(new CreateChannelParams { Name = "parent", Type = parentType, GuildId = GuildId, Description = "d" });
        _context.Channels.Add(parent);
        await _context.SaveChangesAsync();
        return parent;
    }

    // ══════════════════════════════════════════════════════════════════════ CreateThreadAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateThread_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateThreadAsync("nonexistent", new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateThread_ParentDoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.CreateThreadAsync("nonexistent", new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task CreateThread_ParentIsVoiceChannel_ReturnsBadRequest()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads, ChannelType.Voice);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateThread_LacksCreateThreads_ReturnsForbid()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ViewChannel);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateThread_UnderTextParent_Valid_PersistsThread()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "my-thread" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.ChannelDto>;
        Assert.That(ok, Is.Not.Null);
        var created = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == ok!.Value!.Id);
        Assert.That(created.Type, Is.EqualTo(ChannelType.Thread));
        Assert.That(created.ParentChannelId, Is.EqualTo(parent.Id));
        Assert.That(created.CreatedByUserId, Is.EqualTo(UserId));
    }

    [Test]
    public async Task CreateThread_UnderForumParent_Valid()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
    }

    [Test]
    public async Task CreateThread_WithInitialContent_SendsCreateMessageCommand()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);

        await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post", Content = "hello world" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));

        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>().Any(), Is.True);
    }

    [Test]
    public async Task CreateThread_NoContent_DoesNotSendCreateMessageCommand()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);

        await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));

        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>().Any(), Is.False);
    }

    [Test]
    public async Task CreateThread_Valid_PublishesThreadCreatedForBots()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);

        await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _bus, TestPrincipal.Create(UserId));

        Assert.That(_bus.Published.OfType<ThreadCreatedForBots>().Any(e => e.ParentChannelId == parent.Id), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ GetThreadsAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetThreads_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.GetThreadsAsync("nonexistent", _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetThreads_LacksViewChannel_ReturnsForbid()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.None);
        var result = await _endpoint.GetThreadsAsync(parent.Id, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetThreads_Valid_ReturnsThreadsNewestFirst()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ViewChannel);
        var older = Channel.Create(new CreateChannelParams { Name = "older", Type = ChannelType.Thread, GuildId = GuildId, ParentChannelId = parent.Id, Description = "" });
        var newer = Channel.Create(new CreateChannelParams { Name = "newer", Type = ChannelType.Thread, GuildId = GuildId, ParentChannelId = parent.Id, Description = "" });
        older.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        newer.CreatedAt = DateTime.UtcNow;
        _context.Channels.AddRange(older, newer);
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetThreadsAsync(parent.Id, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<List<Guild.Application.Dtos.Response.ChannelDto>>;
        Assert.That(ok!.Value, Has.Count.EqualTo(2));
        Assert.That(ok.Value![0].Name, Is.EqualTo("newer"));
    }

    // ══════════════════════════════════════════════════════════════════════ ArchiveThreadAsync
    // ══════════════════════════════════════════════════════════════════════

    private async Task<Channel> SeedThread(string createdByUserId, string parentId)
    {
        var thread = Channel.Create(new CreateChannelParams { Name = "thread", Type = ChannelType.Thread, GuildId = GuildId, ParentChannelId = parentId, CreatedByUserId = createdByUserId, Description = "" });
        _context.Channels.Add(thread);
        await _context.SaveChangesAsync();
        return thread;
    }

    [Test]
    public async Task ArchiveThread_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.ArchiveThreadAsync("nonexistent", _permissionService, _context, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task ArchiveThread_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.ArchiveThreadAsync("nonexistent", _permissionService, _context, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task ArchiveThread_NonCreatorLacksManageAnyThread_ReturnsForbid()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ManageOwnThreads); // holds own, not any
        var thread = await SeedThread(createdByUserId: "someone-else", parentId: parent.Id);

        var result = await _endpoint.ArchiveThreadAsync(thread.Id, _permissionService, _context, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ArchiveThread_Creator_WithManageOwnThreads_Succeeds()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ManageOwnThreads);
        var thread = await SeedThread(createdByUserId: UserId, parentId: parent.Id);

        var result = await _endpoint.ArchiveThreadAsync(thread.Id, _permissionService, _context, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var reloaded = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == thread.Id);
        Assert.That(reloaded.IsArchived, Is.True);
    }

    [Test]
    public async Task ArchiveThread_NonCreator_WithManageAnyThread_Succeeds()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ManageAnyThread);
        var thread = await SeedThread(createdByUserId: "someone-else", parentId: parent.Id);

        var result = await _endpoint.ArchiveThreadAsync(thread.Id, _permissionService, _context, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
    }

    [Test]
    public async Task ArchiveThread_Valid_PublishesThreadUpdatedForBots()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ManageOwnThreads);
        var thread = await SeedThread(createdByUserId: UserId, parentId: parent.Id);

        await _endpoint.ArchiveThreadAsync(thread.Id, _permissionService, _context, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));

        Assert.That(_bus.Published.OfType<ThreadUpdatedForBots>().Any(e => e.ChannelId == thread.Id && e.Archived), Is.True);
    }

    [Test]
    public async Task ArchiveThread_Valid_WritesAuditLogEntry()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ManageOwnThreads);
        var thread = await SeedThread(createdByUserId: UserId, parentId: parent.Id);

        await _endpoint.ArchiveThreadAsync(thread.Id, _permissionService, _context, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.ActionType == AuditActionType.ChannelUpdated).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
    }
}
