using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>Covers CategoryEndpoint: CreateCategory and DeleteChannelAsync (despite the method
/// name, this actually deletes a Category - not renamed here to keep the diff to tests only).</summary>
[TestFixture]
public class CategoryEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private CategoryEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new CategoryEndpoint();
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
    // CreateCategory
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateCategory_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateCategory(GuildId, new CreateCategoryDto { Name = "c" }, _permissionService, _context, _hub, _hydrateService, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateCategory_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateCategory(GuildId, new CreateCategoryDto { Name = "c" }, _permissionService, _context, _hub, _hydrateService, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateCategory_Valid_PersistsCategory()
    {
        await SeedManagerMember();

        var result = await _endpoint.CreateCategory(GuildId, new CreateCategoryDto { Name = "New Category", Position = 3 }, _permissionService, _context, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.CategoryDto>;
        Assert.That(ok, Is.Not.Null);
        var created = await _context.Categories.AsNoTracking().FirstAsync(c => c.Id == ok!.Value!.Id);
        Assert.That(created.Name, Is.EqualTo("New Category"));
        Assert.That(created.Position, Is.EqualTo(3));
    }

    // ══════════════════════════════════════════════════════════════════════
    // DeleteChannelAsync (deletes a Category)
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteCategory_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.DeleteChannelAsync("nonexistent", _permissionService, _context, _hub, _hydrateService, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task DeleteCategory_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.DeleteChannelAsync("nonexistent", _permissionService, _context, _hub, _hydrateService, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteCategory_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        var category = Guild.Domain.Entity.Category.Create(new CreateCategoryParams { Name = "cat", GuildId = GuildId, Position = 0 });
        _context.Categories.Add(category);
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteChannelAsync(category.Id, _permissionService, _context, _hub, _hydrateService, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteCategory_Valid_RemovesCategory()
    {
        await SeedManagerMember();
        var category = Guild.Domain.Entity.Category.Create(new CreateCategoryParams { Name = "cat", GuildId = GuildId, Position = 0 });
        _context.Categories.Add(category);
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteChannelAsync(category.Id, _permissionService, _context, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.Categories.AsNoTracking().AnyAsync(c => c.Id == category.Id), Is.False);
    }
}
