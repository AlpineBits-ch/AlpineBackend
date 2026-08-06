using System.Text.Json;
using Guild.Application.Dtos;
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

    // The default stays a summary listing: content is the largest thing a wiki holds, and most
    // callers only want the tree.
    [Test]
    public async Task GetWiki_ByDefault_OmitsPageContent()
    {
        await SeedMember(Permissions.ViewWiki);
        await SeedPage(content: "the body");

        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiDto>;
        Assert.That(ok!.Value!.Pages[0].Content, Is.Null);
    }

    // Full-text search and backlinks need every body.
    [Test]
    public async Task GetWiki_IncludeContent_ReturnsPageContent()
    {
        await SeedMember(Permissions.ViewWiki);
        await SeedPage(content: "the body");

        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.Create(UserId), includeContent: true);

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiDto>;
        Assert.That(ok!.Value!.Pages[0].Content, Is.EqualTo("the body"));
    }

    // Guards the switch away from Include(p => p.Revisions): the count must survive being
    // computed by a projection rather than by materialising every revision.
    [Test]
    public async Task GetWiki_CountsRevisionsWithoutLoadingThem()
    {
        await SeedMember(Permissions.EditOwnWikiPages | Permissions.ViewWiki);
        var page = await SeedPage(content: "v1");
        await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Content = "v2" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiDto>;
        Assert.That(ok!.Value!.Pages[0].RevisionCount, Is.EqualTo(2));
    }

    // A grouped count returns no row at all for a page with no revisions, so the lookup has to
    // default rather than throw.
    [Test]
    public async Task GetWiki_PageWithNoRevisions_ReportsZero()
    {
        await SeedMember(Permissions.ViewWiki);
        var page = WikiPage.Create(new CreateWikiPageParams { GuildId = GuildId, Title = "No revisions", Content = "x", AuthorId = UserId });
        page.Revisions.Clear();
        _context.WikiPages.Add(page);
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiDto>;
        Assert.That(ok!.Value!.Pages[0].RevisionCount, Is.EqualTo(0));
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

    // WikiRevision has carried a Summary since the feature shipped and nothing could ever set
    // it, so every revision in every wiki reads "No summary".
    [Test]
    public async Task UpdateWikiPage_WithSummary_StoresItOnTheNewRevision()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage(authorId: UserId, content: "v1");

        await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Content = "v2", Summary = "Fixed the install steps" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var latest = await _context.WikiRevisions.AsNoTracking()
            .Where(r => r.PageId == page.Id).OrderByDescending(r => r.RevisionNumber).FirstAsync();
        Assert.That(latest.Summary, Is.EqualTo("Fixed the install steps"));
    }

    [Test]
    public async Task UpdateWikiPage_WithoutSummary_LeavesItNull()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage(authorId: UserId, content: "v1");

        await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Content = "v2" }, _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var latest = await _context.WikiRevisions.AsNoTracking()
            .Where(r => r.PageId == page.Id).OrderByDescending(r => r.RevisionNumber).FirstAsync();
        Assert.That(latest.Summary, Is.Null);
    }

    // A summary describes a content change.
    [Test]
    public async Task UpdateWikiPage_SummaryWithoutContentChange_AddsNoRevision()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage(authorId: UserId, content: "v1");

        await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Title = "renamed", Summary = "orphaned" }, _permissionService, _context, TestPrincipal.Create(UserId));
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


    // ══════════════════════════════════════════════════════════════════════ Page icon and cover
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateWikiPage_WithIconAndCover_PersistsBoth()
    {
        await SeedMember(Permissions.CreateWikiPages);

        var result = await _endpoint.CreateWikiPage(GuildId,
            new CreateWikiPageDto { Title = "Runbook", Icon = "📘", CoverUrl = "https://cdn.example/c.png" },
            _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiPageDto>;
        Assert.That(ok!.Value!.Icon, Is.EqualTo("📘"));
        Assert.That(ok.Value.CoverUrl, Is.EqualTo("https://cdn.example/c.png"));
        var created = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == ok.Value.Id);
        Assert.That(created.Icon, Is.EqualTo("📘"));
    }

    // The icon column is a single emoji, not a free text field.
    [Test]
    public async Task CreateWikiPage_IconIsNotAnEmoji_ReturnsBadRequest()
    {
        await SeedMember(Permissions.CreateWikiPages);

        var result = await _endpoint.CreateWikiPage(GuildId,
            new CreateWikiPageDto { Title = "t", Icon = "not an emoji" },
            _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateWikiPage_CoverUrlTooLong_ReturnsBadRequest()
    {
        await SeedMember(Permissions.CreateWikiPages);

        var result = await _endpoint.CreateWikiPage(GuildId,
            new CreateWikiPageDto { Title = "t", CoverUrl = new string('x', 2049) },
            _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // The reason Icon/CoverUrl are three-state instead of following ParentPageId's
    // null-means-clear rule: the client autosaves content, and a save that only carries the body
    // must not wipe the page's identity.
    [Test]
    public async Task UpdateWikiPage_OmittingIcon_LeavesItAlone()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage(content: "v1");
        page.Icon = "📘";
        page.CoverUrl = "https://cdn.example/c.png";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Content = "v2" },
            _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.That(reloaded.Icon, Is.EqualTo("📘"));
        Assert.That(reloaded.CoverUrl, Is.EqualTo("https://cdn.example/c.png"));
    }

    [Test]
    public async Task UpdateWikiPage_EmptyIcon_ClearsIt()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage();
        page.Icon = "📘";
        page.CoverUrl = "https://cdn.example/c.png";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Icon = Optional<string>.Of(""), CoverUrl = Optional<string>.Of("") },
            _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.That(reloaded.Icon, Is.Null);
        Assert.That(reloaded.CoverUrl, Is.Null);
    }

    [Test]
    public async Task UpdateWikiPage_IconIsNotAnEmoji_ReturnsBadRequest()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage();

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, new UpdateWikiPageDto { Icon = Optional<string>.Of("ab") },
            _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // The tree renders the icon, so the summary listing has to carry it too.
    [Test]
    public async Task GetWiki_SummaryCarriesIconAndCover()
    {
        await SeedMember(Permissions.ViewWiki);
        var page = await SeedPage();
        page.Icon = "📘";
        page.CoverUrl = "https://cdn.example/c.png";
        await _context.SaveChangesAsync();

        var result = await _endpoint.GetWiki(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var ok = result as Ok<Guild.Application.Dtos.Response.WikiDto>;
        Assert.That(ok!.Value!.Pages[0].Icon, Is.EqualTo("📘"));
        Assert.That(ok.Value.Pages[0].CoverUrl, Is.EqualTo("https://cdn.example/c.png"));
    }

    // ══════════════════════════════════════════════════════════════════════ Partial update: absent
    // vs explicitly null

    private static UpdateWikiPageDto Deserialize(string json) =>
        JsonSerializer.Deserialize<UpdateWikiPageDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Test]
    public async Task UpdateWikiPage_OmittingCategoryAndParent_LeavesThemAlone()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage(content: "v1");
        page.CategoryId = "wkca_x";
        page.ParentPageId = "wkpg_parent";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("""{"content":"v2"}"""),
            _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.CategoryId, Is.EqualTo("wkca_x"));
            Assert.That(reloaded.ParentPageId, Is.EqualTo("wkpg_parent"));
        });
    }

    // The client's "No category" / "Top level" options.
    [Test]
    public async Task UpdateWikiPage_ExplicitNullCategoryAndParent_ClearsThem()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage();
        page.CategoryId = "wkca_x";
        page.ParentPageId = "wkpg_parent";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("""{"categoryId":null,"parentPageId":null}"""),
            _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.CategoryId, Is.Null);
            Assert.That(reloaded.ParentPageId, Is.Null);
        });
    }

    [Test]
    public async Task UpdateWikiPage_ExplicitCategoryAndParent_SetsThem()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage();

        await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("""{"categoryId":"wkca_y","parentPageId":"wkpg_p"}"""),
            _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.CategoryId, Is.EqualTo("wkca_y"));
            Assert.That(reloaded.ParentPageId, Is.EqualTo("wkpg_p"));
        });
    }

    // Icon and cover follow the same rule, and additionally accept "" as a clear.
    [Test]
    public async Task UpdateWikiPage_ExplicitNullIcon_ClearsIt()
    {
        await SeedMember(Permissions.EditOwnWikiPages);
        var page = await SeedPage();
        page.Icon = "📘";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("""{"icon":null}"""),
            _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.That(reloaded.Icon, Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════ Moving a page vs
    // editing it ══════════════════════════════════════════════════════════════════════

    // Where a page sits is the wiki's shape, which is what ManageWikiStructure governs everywhere
    // else. A structure manager who cannot edit the page can still move it.
    [Test]
    public async Task UpdateWikiPage_MoveOnly_IsAllowedByManageWikiStructureAlone()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        var page = await SeedPage(authorId: "someone-else");

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("""{"categoryId":"wkca_y"}"""),
            _permissionService, _context, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.WikiPageDto>>());
        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.That(reloaded.CategoryId, Is.EqualTo("wkca_y"));
    }

    // ...but a move must not launder a content change through the weaker permission.
    [Test]
    public async Task UpdateWikiPage_MovePlusContent_StillNeedsEditPermission()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        var page = await SeedPage(authorId: "someone-else", content: "v1");

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("""{"categoryId":"wkca_y","content":"v2"}"""),
            _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        var reloaded = await _context.WikiPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.That(reloaded.Content, Is.EqualTo("v1"));
    }

    // Pinning and tags are page content, not structure - the client gates them on edit permission
    // and the server agrees.
    [Test]
    public async Task UpdateWikiPage_PinOnly_IsNotAllowedByManageWikiStructure()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        var page = await SeedPage(authorId: "someone-else");

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("""{"isPinned":true}"""),
            _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // An empty body is not a move, so it does not get the structure manager's discount.
    [Test]
    public async Task UpdateWikiPage_EmptyBody_StillNeedsEditPermission()
    {
        await SeedMember(Permissions.ManageWikiStructure);
        var page = await SeedPage(authorId: "someone-else");

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("{}"),
            _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    // Neither permission is still no.
    [Test]
    public async Task UpdateWikiPage_MoveOnly_WithoutEitherPermission_ReturnsForbid()
    {
        await SeedMember(Permissions.ViewWiki);
        var page = await SeedPage(authorId: "someone-else");

        var result = await _endpoint.UpdateWikiPage(GuildId, page.Id, Deserialize("""{"categoryId":"wkca_y"}"""),
            _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

}
