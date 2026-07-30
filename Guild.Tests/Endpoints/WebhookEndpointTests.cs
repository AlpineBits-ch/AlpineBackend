using Guild.Application.Dtos.Request;
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

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers WebhookEndpoint: Get/Create/Delete (ManageChannel-gated - unlike the WolverineHttp
/// endpoints elsewhere, these use plain Mvc Http* attributes and take ClaimsPrincipal directly
/// into GuildPermissionService's overload, so an anonymous caller resolves to Forbid rather than
/// a separate Unauthorized branch) and ExecuteWebhook (public, no auth check).
/// </summary>
[TestFixture]
public class WebhookEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private FakeInvokingMessageBus _bus = null!;
    private WebhookEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _bus = new FakeInvokingMessageBus();
        _endpoint = new WebhookEndpoint();
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

    // ══════════════════════════════════════════════════════════════════════
    // GetWebhooksByGuildAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetWebhooks_Unauthenticated_ReturnsForbid()
    {
        var result = await _endpoint.GetWebhooksByGuildAsync(GuildId, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetWebhooks_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetWebhooksByGuildAsync(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetWebhooks_Valid_ReturnsWebhooks()
    {
        await SeedManagerMember();
        _context.WebhookConfigs.Add(new WebhookConfig { Id = "wh-1", GuildId = GuildId, ChannelId = "chan-1", CreatedBy = UserId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetWebhooksByGuildAsync(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        var ok = result as Ok<List<Guild.Application.Dtos.Response.WebhookConfigDto>>;
        Assert.That(ok!.Value, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════
    // CreateWebhookAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateWebhook_LacksManageChannel_ReturnsForbid()
    {
        var result = await _endpoint.CreateWebhookAsync(GuildId, new CreateWebhookDto { Name = "Hook", ChannelId = "chan-1" }, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateWebhook_Valid_PersistsWebhook()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateWebhookAsync(GuildId, new CreateWebhookDto { Name = "My Hook", ChannelId = "chan-1" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.WebhookConfigDto>;
        Assert.That(ok, Is.Not.Null);
        var created = await _context.WebhookConfigs.AsNoTracking().FirstAsync(w => w.Id == ok!.Value!.Id);
        Assert.That(created.Name, Is.EqualTo("My Hook"));
        Assert.That(created.CreatedBy, Is.EqualTo(UserId));
    }

    // ══════════════════════════════════════════════════════════════════════
    // DeleteWebhookAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteWebhook_LacksManageChannel_ReturnsForbid()
    {
        var result = await _endpoint.DeleteWebhookAsync("wh-1", GuildId, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteWebhook_DoesNotExist_ReturnsNotFound()
    {
        await SeedManagerMember();
        var result = await _endpoint.DeleteWebhookAsync("nonexistent", GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteWebhook_Valid_RemovesWebhook()
    {
        await SeedManagerMember();
        _context.WebhookConfigs.Add(new WebhookConfig { Id = "wh-1", GuildId = GuildId, ChannelId = "chan-1", CreatedBy = UserId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteWebhookAsync("wh-1", GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.WebhookConfigDto>>());
        Assert.That(await _context.WebhookConfigs.AsNoTracking().AnyAsync(w => w.Id == "wh-1"), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════
    // ExecuteWebhook
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ExecuteWebhook_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.ExecuteWebhook("nonexistent", new WebhookRequestDto { UserName = "bot", Content = "hi" }, _context, _bus);
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task ExecuteWebhook_Valid_SendsCreateMessageCommand()
    {
        _context.WebhookConfigs.Add(new WebhookConfig { Id = "wh-1", GuildId = GuildId, ChannelId = "chan-1", CreatedBy = UserId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.ExecuteWebhook("wh-1", new WebhookRequestDto { UserName = "bot", Content = "hi there" }, _context, _bus);

        Assert.That(result, Is.InstanceOf<Ok>());
        var sent = _bus.Invoked.OfType<CreateMessageCommand>().FirstOrDefault();
        Assert.That(sent, Is.Not.Null);
        Assert.That(sent!.ChannelId, Is.EqualTo("chan-1"));
        Assert.That(sent.AuthorIdType, Is.EqualTo(AuthorIdType.Webhook));
    }
}
