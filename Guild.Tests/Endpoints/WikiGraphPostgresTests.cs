using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// The two queries the page graph adds, against a real Postgres: InMemory answers an untranslatable
/// query rather than failing on it, so behaviour tests alone would not notice.
/// </summary>
[TestFixture]
public class WikiGraphPostgresTests
{
    private const string GuildId = "guild-graph";
    private const string UserId = "user-graph";
    private const string Slug = "a-guild";

    private MicroserviceContext _context = null!;
    private GuildPermissionService _permissionService = null!;
    private VanityUrlService _vanity = null!;
    private WikiEndpoint _wiki = null!;
    private PublicWikiEndpoint _public = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => await PostgresTestDatabase.EnsureStartedAsync();

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync();

        _context = new PostgresGuildContext();
        _permissionService = PermissionTestFactory.Create(new FakeDistributedCache(), _context);
        _vanity = new VanityUrlService(_context, NullLogger<VanityUrlService>.Instance);
        _wiki = new WikiEndpoint();
        _public = new PublicWikiEndpoint();

        await SeedMemberAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [OneTimeTearDown]
    public async Task OneTimeTearDown() => await PostgresTestDatabase.ResetAsync();

    [Test]
    public async Task GetWikiGraph_TranslatesAndReturnsTheEdge()
    {
        var target = await SeedPageAsync();
        var source = await SeedPageAsync($"[the map](wiki:{target.Id})");
        await LinkAsync(source.Id, target.Id);

        var result = await _wiki.GetWikiGraph(GuildId, _permissionService, _context, TestPrincipal.Create(UserId));

        var graph = ((Ok<WikiGraphDto>)result).Value!;
        Assert.That(graph.Nodes, Has.Count.EqualTo(2));
        Assert.That(graph.Edges, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(graph.Edges[0].SourcePageId, Is.EqualTo(source.Id));
            Assert.That(graph.Edges[0].TargetPageId, Is.EqualTo(target.Id));
        });
    }

    [Test]
    public async Task GetPublicWikiPage_TranslatesAndNamesOnlyThePublishedTarget()
    {
        var wiki = Wiki.Create(GuildId);
        wiki.Publish(Slug);
        _context.Wikis.Add(wiki);

        var published = await SeedPageAsync(publishedAt: DateTimeOffset.UtcNow);
        var unpublished = await SeedPageAsync();
        var source = await SeedPageAsync(
            $"[a](wiki:{published.Id}) [b](wiki:{unpublished.Id})", DateTimeOffset.UtcNow);

        await LinkAsync(source.Id, published.Id);
        await LinkAsync(source.Id, unpublished.Id);

        var result = await _public.GetPublicWikiPage(Slug, source.Slug, _context, _vanity);

        var page = ((Ok<PublicWikiPageDto>)result).Value!;
        Assert.That(page.LinkedPages, Has.Count.EqualTo(1));
        Assert.That(page.LinkedPages[published.Id], Is.EqualTo(published.Slug));
    }

    private async Task SeedMemberAsync()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = "owner", Name = "A Guild", Features = GuildFeatures.Wiki,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = "role-graph", GuildId = GuildId, Name = "role",
            ModulePermissions = ModulePermissions.ViewWiki,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-graph", GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-graph", RoleId = "role-graph", MemberId = "member-graph",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<WikiPage> SeedPageAsync(string content = "prose", DateTimeOffset? publishedAt = null)
    {
        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = GuildId, Title = "A Page", Content = content, AuthorId = UserId,
        });
        page.PublishedAt = publishedAt;
        _context.WikiPages.Add(page);
        await _context.SaveChangesAsync();
        return page;
    }

    private async Task LinkAsync(string sourcePageId, string targetPageId)
    {
        _context.WikiPageLinks.Add(new WikiPageLink
        {
            SourcePageId = sourcePageId, TargetPageId = targetPageId, GuildId = GuildId,
        });
        await _context.SaveChangesAsync();
    }
}
