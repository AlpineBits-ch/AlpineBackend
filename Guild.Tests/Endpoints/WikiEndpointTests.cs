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

/// <summary>
/// Covers WikiEndpoint: GetWiki (lazily creates the Wiki row on first access), page CRUD
/// (own-vs-any edit permission split identical in spirit to ThreadEndpoint's archive gate),
/// revision listing/restore, and category CRUD.
/// </summary>
[TestFixture]
public class WikiEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private WikiEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _endpoint = new WikiEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild() => new()
    {
        Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedMember(Permissions permissions)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "role", Permissions = permissions, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();
    }

    private async Task<WikiPage> SeedPage(string authorId = UserId, string content = "original")
    {
        var page = WikiPage.Create(new CreateWikiPageParams { GuildId = GuildId, Title = "My Page", Content = content, AuthorId = authorId });
        _context.WikiPages.Add(page);
        await _context.SaveChangesAsync();
        return page;
    }

    // ══════════════════════════════════════════════════════════════════════ GetWiki
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetWiki_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetWiki_LacksViewWiki_ReturnsForbid()
    {
        await SeedMember(Permissions.None);
        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetWiki_FirstAccess_CreatesWikiRow()
    {
        await SeedMember(Permissions.ViewWiki);

        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.WikiDto>>());
        Assert.That(await _context.Wikis.AsNoTracking().AnyAsync(w => w.GuildId == GuildId), Is.True);
    }

    [Test]
    public async Task GetWiki_Valid_IncludesRevisionCount()
    {
        await SeedMember(Permissions.ViewWiki);
        await SeedPage();

        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiDto>;
        Assert.That(ok!.Value!.Pages, Has.Count.EqualTo(1));
        Assert.That(ok.Value.Pages[0].RevisionCount, Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ GetWikiPage
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetWikiPage_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.GetWikiPage(GuildId, "nonexistent", _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task GetWikiPage_LacksViewWiki_ReturnsForbid()
    {
        await SeedMember(Permissions.None);
        var result = await _endpoint.GetWikiPage(GuildId, "nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task GetWikiPage_DoesNotExist_ReturnsNotFound()
    {
        await SeedMember(Permissions.ViewWiki);
        var result = await _endpoint.GetWikiPage(GuildId, "nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetWikiPage_Valid_ReturnsPageWithRevisionCount()
    {
        await SeedMember(Permissions.ViewWiki);
        var page = await SeedPage();

        var result = await _endpoint.GetWikiPage(GuildId, page.Id, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiPageDto>;
        Assert.That(ok!.Value!.RevisionCount, Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ CreateWikiPage
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateWikiPage_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateWikiPage(GuildId, new CreateWikiPageDto { Title = "t" }, _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateWikiPage_LacksCreateWikiPages_ReturnsForbid()
    {
        await SeedMember(Permissions.ViewWiki);
        var result = await _endpoint.CreateWikiPage(GuildId, new CreateWikiPageDto { Title = "t" }, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateWikiPage_Valid_PersistsPageWithInitialRevision()
    {
        await SeedMember(Permissions.CreateWikiPages);

        var result = await _endpoint.CreateWikiPage(GuildId, new CreateWikiPageDto { Title = "New Page", Content = "hello" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiPageDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(ok!.Value!.RevisionCount, Is.EqualTo(1));
        var created = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == ok.Value.Id);
        Assert.That(created.Title, Is.EqualTo("New Page"));
        Assert.That(created.AuthorId, Is.EqualTo(UserId));
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateWikiPage
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateWikiPage_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.UpdateWikiPage(GuildId, "nonexistent", new UpdateWikiPageDto(), _permissionService, _context, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task UpdateWikiPage_DoesNotExist_ReturnsNotFound()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var result = await _endpoint.UpdateWikiPage(GuildId, "nonexistent", new UpdateWikiPageDto(), _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateWikiPage_OwnPage_RequiresEditOwnWikiPages()
    {
        await SeedMember(Permissions.None);
        var page = await SeedPage(authorId: UserId);

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Content = "new" }, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateWikiPage_OtherAuthorPage_RequiresEditAnyWikiPage_NotEditOwn()
    {
        // Holding only EditOwnWikiPages must not be enough to edit someone else's page.
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage(authorId: "someone-else");

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Content = "new" }, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateWikiPage_ContentChanged_AddsNewRevision()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage(authorId: UserId, content: "v1");

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Content = "v2" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiPageDto>;
        Assert.That(ok!.Value!.RevisionCount, Is.EqualTo(2));
        var revisions = await _context.WikiRevisions.AsNoTracking().Where(r => r.PageId == page.Id).ToListAsync();
        Assert.That(revisions, Has.Count.EqualTo(2));
        Assert.That(revisions.Max(r => r.RevisionNumber), Is.EqualTo(2));
    }

    [Test]
    public async Task UpdateWikiPage_ContentUnchanged_DoesNotAddRevision()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage(authorId: UserId, content: "v1");

        await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Content = "v1", Title = "renamed" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var revisions = await _context.WikiRevisions.AsNoTracking().Where(r => r.PageId == page.Id).ToListAsync();
        Assert.That(revisions, Has.Count.EqualTo(1));
        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.That(reloaded.Title, Is.EqualTo("renamed"));
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteWikiPage
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteWikiPage_DoesNotExist_ReturnsNotFound()
    {
        await SeedMember(Permissions.DeleteWikiPages);
        var result = await _endpoint.DeleteWikiPage(GuildId, "nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteWikiPage_LacksDeleteWikiPages_ReturnsForbid()
    {
        await SeedMember(Permissions.ViewWiki);
        var page = await SeedPage();

        var result = await _endpoint.DeleteWikiPage(GuildId, page.Id, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteWikiPage_Valid_RemovesPage()
    {
        await SeedMember(Permissions.DeleteWikiPages);
        var page = await SeedPage();

        var result = await _endpoint.DeleteWikiPage(GuildId, page.Id, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.WikiPages.AsNoTracking().AnyAsync(p => p.Id == page.Id), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════ GetWikiPageRevisions
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetWikiPageRevisions_PageDoesNotExist_ReturnsNotFound()
    {
        await SeedMember(Permissions.ViewWiki);
        var result = await _endpoint.GetWikiPageRevisions(GuildId, "nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task GetWikiPageRevisions_Valid_ReturnsNewestFirst()
    {
        await SeedMember(Permissions.ViewWiki | Permissions.EditOwnWikiPages);
        var page = await SeedPage(content: "v1");
        page.Content = "v2";
        page.Revisions.Add(WikiRevision.Create(new CreateWikiRevisionParams { PageId = page.Id, Content = "v2", EditorId = UserId, RevisionNumber = 2 }));
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetWikiPageRevisions(GuildId, page.Id, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<List<Guild.Application.Dtos.Response.WikiRevisionDto>>;
        var list = ok!.Value!;
        Assert.That(list, Has.Count.EqualTo(2));
        Assert.That(list[0].RevisionNumber, Is.EqualTo(2));
    }

    // ══════════════════════════════════════════════════════════════════════ RestoreWikiRevision
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RestoreWikiRevision_LacksManageWikiRevisions_ReturnsForbid()
    {
        await SeedMember(Permissions.ViewWiki);
        var result = await _endpoint.RestoreWikiRevision(GuildId, "nonexistent", "nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task RestoreWikiRevision_PageDoesNotExist_ReturnsNotFound()
    {
        await SeedMember(Permissions.ManageWikiRevisions);
        var result = await _endpoint.RestoreWikiRevision(GuildId, "nonexistent", "nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task RestoreWikiRevision_RevisionDoesNotExist_ReturnsNotFound()
    {
        await SeedMember(Permissions.ManageWikiRevisions);
        var page = await SeedPage();

        var result = await _endpoint.RestoreWikiRevision(GuildId, page.Id, "nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task RestoreWikiRevision_Valid_RestoresContentAndAddsNewRevision()
    {
        await SeedMember(Permissions.ManageWikiRevisions);
        var page = await SeedPage(content: "v1");
        var firstRevisionId = page.Revisions.First().Id;
        page.Content = "v2";
        await _context.SaveChangesAsync();

        var result = await _endpoint.RestoreWikiRevision(GuildId, page.Id, firstRevisionId, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiPageDto>;
        Assert.That(ok!.Value!.RevisionCount, Is.EqualTo(2));
        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.That(reloaded.Content, Is.EqualTo("v1"));
    }

    // ══════════════════════════════════════════════════════════════════════ Wiki Categories
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateWikiCategory_LacksManageWikiStructure_ReturnsForbid()
    {
        await SeedMember(Permissions.ViewWiki);
        var result = await _endpoint.CreateWikiCategory(GuildId, new CreateWikiCategoryDto { Name = "c" }, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateWikiCategory_Valid_DefaultsPositionToCount()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        _context.WikiCategories.Add(WikiCategory.Create(new CreateWikiCategoryParams { GuildId = GuildId, Name = "existing" }));
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateWikiCategory(GuildId, new CreateWikiCategoryDto { Name = "new" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiCategoryDto>;
        Assert.That(ok!.Value!.Position, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdateWikiCategory_DoesNotExist_ReturnsNotFound()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        var result = await _endpoint.UpdateWikiCategory(GuildId, "nonexistent", new UpdateWikiCategoryDto(), _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateWikiCategory_Valid_UpdatesName()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        var category = WikiCategory.Create(new CreateWikiCategoryParams { GuildId = GuildId, Name = "old" });
        _context.WikiCategories.Add(category);
        await _context.SaveChangesAsync();

        var result = await _endpoint.UpdateWikiCategory(GuildId, category.Id, new UpdateWikiCategoryDto { Name = "renamed" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.WikiCategoryDto>>());
        var reloaded = await _context.WikiCategories.AsNoTracking().FirstAsync(c => c.Id == category.Id);
        Assert.That(reloaded.Name, Is.EqualTo("renamed"));
    }

    [Test]
    public async Task DeleteWikiCategory_DoesNotExist_ReturnsNotFound()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        var result = await _endpoint.DeleteWikiCategory(GuildId, "nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task DeleteWikiCategory_Valid_RemovesCategory()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        var category = WikiCategory.Create(new CreateWikiCategoryParams { GuildId = GuildId, Name = "cat" });
        _context.WikiCategories.Add(category);
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteWikiCategory(GuildId, category.Id, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.WikiCategories.AsNoTracking().AnyAsync(c => c.Id == category.Id), Is.False);
    }
}
