using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// The anonymous wiki surface, and the reason it exists as a second pair of columns rather than as
/// a third WikiVisibility member: Visibility defaults to Public and always meant "visible to the
/// guild", so every page written since the wiki shipped reads public. If that column granted
/// external access, turning the feature on would publish all of them retroactively.
/// </summary>
[TestFixture]
public class PublicWikiEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";
    private const string Slug = "a-guild";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private VanityUrlService _vanity = null!;
    private PublicWikiEndpoint _public = null!;
    private WikiEndpoint _wiki = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _vanity = new VanityUrlService(_context, NullLogger<VanityUrlService>.Instance);
        _public = new PublicWikiEndpoint();
        _wiki = new WikiEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedGuild(
        GuildKind kind = GuildKind.Community,
        GuildFeatures features = GuildFeatures.Wiki,
        ModulePermissions modulePermissions = ModulePermissions.None)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "A Guild", Description = "About us",
            Kind = kind, Features = features,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "role", ModulePermissions = modulePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<Wiki> SeedWiki(string? publishedSlug)
    {
        var wiki = Wiki.Create(GuildId);
        if (publishedSlug is not null) wiki.Publish(publishedSlug);
        _context.Wikis.Add(wiki);
        await _context.SaveChangesAsync();
        return wiki;
    }

    private async Task<WikiPage> SeedPage(
        bool published, WikiVisibility visibility = WikiVisibility.Public, string content = "hello")
    {
        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = GuildId, Title = "My Page", Content = content, AuthorId = UserId,
            Visibility = visibility,
        });
        if (published) page.PublishedAt = DateTimeOffset.UtcNow;
        _context.WikiPages.Add(page);
        await _context.SaveChangesAsync();
        return page;
    }

    // ══════════════════════════════════════════════════════════ The two flags
    // ══════════════════════════════════════════════════════════

    /// <summary>The deploy-day scenario: a wiki that predates publishing entirely.</summary>
    [Test]
    public async Task An_existing_page_is_not_published_by_the_feature_existing()
    {
        await SeedGuild();
        await SeedWiki(publishedSlug: null);
        var page = await SeedPage(published: false);

        var index = await _public.GetPublicWiki(Slug, _context, _vanity);
        var single = await _public.GetPublicWikiPage(Slug, page.Slug, _context, _vanity);

        Assert.Multiple(() =>
        {
            Assert.That(index, Is.InstanceOf<NotFound>());
            Assert.That(single, Is.InstanceOf<NotFound>());
        });
    }

    /// <summary>
    /// Visibility.Public is the default on every row ever written and must not be what opens the
    /// door.
    /// </summary>
    [Test]
    public async Task The_legacy_visibility_column_alone_grants_nothing()
    {
        await SeedGuild();
        await SeedWiki(Slug);
        var page = await SeedPage(published: false, visibility: WikiVisibility.Public);

        var result = await _public.GetPublicWikiPage(Slug, page.Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>Both halves, and only both halves.</summary>
    [Test]
    public async Task The_two_opt_ins_together_grant_access()
    {
        await SeedGuild();
        await SeedWiki(Slug);
        var page = await SeedPage(published: true);

        var result = await _public.GetPublicWikiPage(Slug, page.Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<Ok<PublicWikiPageDto>>());
        Assert.That(((Ok<PublicWikiPageDto>)result).Value!.Content, Is.EqualTo("hello"));
    }

    /// <summary>The guild-level slug is the master switch.</summary>
    [Test]
    public async Task A_published_page_in_an_unpublished_wiki_is_not_reachable()
    {
        await SeedGuild();
        await SeedWiki(publishedSlug: null);
        var page = await SeedPage(published: true);

        var result = await _public.GetPublicWikiPage(Slug, page.Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>Visibility is a veto even once the page has been opted in.</summary>
    [Test]
    public async Task A_private_page_stays_private_even_when_flagged_published()
    {
        await SeedGuild();
        await SeedWiki(Slug);
        var page = await SeedPage(published: true, visibility: WikiVisibility.Private);

        var result = await _public.GetPublicWikiPage(Slug, page.Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>Unpublishing takes effect on the next read.</summary>
    [Test]
    public async Task Revoking_the_wiki_slug_takes_every_page_down()
    {
        await SeedGuild();
        var wiki = await SeedWiki(Slug);
        var page = await SeedPage(published: true);

        wiki.Unpublish();
        await _context.SaveChangesAsync();

        var result = await _public.GetPublicWikiPage(Slug, page.Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>The wiki row cascades with the guild, so a deleted guild leaves nothing to
    /// resolve; this pins the case where the row somehow outlives it.</summary>
    [Test]
    public async Task A_wiki_whose_guild_is_gone_is_not_reachable()
    {
        await SeedGuild();
        await SeedWiki(Slug);
        await SeedPage(published: true);

        _context.Guilds.Remove(await _context.Guilds.FirstAsync(g => g.Id == GuildId));
        await _context.SaveChangesAsync();

        var result = await _public.GetPublicWiki(Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>Switching the module off takes the public wiki with it.</summary>
    [Test]
    public async Task A_guild_with_the_wiki_module_off_serves_nothing()
    {
        await SeedGuild(features: GuildFeatures.None);
        await SeedWiki(Slug);
        await SeedPage(published: true);

        var result = await _public.GetPublicWiki(Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>A house manual is not something anybody meant to put on the open internet.</summary>
    [Test]
    public async Task A_household_serves_nothing()
    {
        await SeedGuild(kind: GuildKind.Household);
        await SeedWiki(Slug);
        await SeedPage(published: true);

        var result = await _public.GetPublicWiki(Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>Routing is on the slug, and an unknown one is the same answer as everything else.</summary>
    [Test]
    public async Task An_unknown_slug_is_not_found()
    {
        await SeedGuild();
        await SeedWiki(Slug);

        var result = await _public.GetPublicWiki("some-other-guild", _context, _vanity);

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>The index lists only what was opted in.</summary>
    [Test]
    public async Task The_index_lists_only_published_pages()
    {
        await SeedGuild();
        await SeedWiki(Slug);
        await SeedPage(published: true);
        await SeedPage(published: false);
        await SeedPage(published: true, visibility: WikiVisibility.Private);

        var result = await _public.GetPublicWiki(Slug, _context, _vanity);

        Assert.That(result, Is.InstanceOf<Ok<PublicWikiDto>>());
        Assert.That(((Ok<PublicWikiDto>)result).Value!.Pages, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════ The permission gate
    // ══════════════════════════════════════════════════════

    /// <summary>The bit has existed since the wiki shipped and gated nothing until now.</summary>
    [Test]
    public async Task Publishing_without_the_permission_is_forbidden()
    {
        await SeedGuild(modulePermissions: ModulePermissions.ViewWiki | ModulePermissions.EditAnyWikiPage);
        await SeedWiki(publishedSlug: null);

        var result = await _wiki.SetWikiPublication(
            GuildId, new SetWikiPublicationDto { Slug = Slug },
            _permissionService, _context, TestPrincipal.Create(UserId), _vanity, new AuditLogService(_context));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
        Assert.That(await _context.Wikis.AnyAsync(w => w.PublishedSlug != null), Is.False);
    }

    [Test]
    public async Task Publishing_with_the_permission_claims_the_slug()
    {
        await SeedGuild(modulePermissions: ModulePermissions.PublishWikiPublicly);
        await SeedWiki(publishedSlug: null);

        var result = await _wiki.SetWikiPublication(
            GuildId, new SetWikiPublicationDto { Slug = Slug },
            _permissionService, _context, TestPrincipal.Create(UserId), _vanity, new AuditLogService(_context));

        Assert.That(result, Is.InstanceOf<Ok<WikiPublicationDto>>());
        Assert.That(((Ok<WikiPublicationDto>)result).Value!.Slug, Is.EqualTo(Slug));
    }

    /// <summary>Publishing one page is the same permission as publishing the wiki.</summary>
    [Test]
    public async Task Publishing_a_page_without_the_permission_is_forbidden()
    {
        await SeedGuild(modulePermissions: ModulePermissions.EditAnyWikiPage);
        await SeedWiki(Slug);
        var page = await SeedPage(published: false);

        var result = await _wiki.SetWikiPagePublication(
            GuildId, page.Id, new SetWikiPagePublicationDto { Published = true },
            _permissionService, _context, TestPrincipal.Create(UserId), new AuditLogService(_context));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Publishing_a_page_with_the_permission_opts_it_in()
    {
        await SeedGuild(modulePermissions: ModulePermissions.PublishWikiPublicly);
        await SeedWiki(Slug);
        var page = await SeedPage(published: false);

        await _wiki.SetWikiPagePublication(
            GuildId, page.Id, new SetWikiPagePublicationDto { Published = true },
            _permissionService, _context, TestPrincipal.Create(UserId), new AuditLogService(_context));
        await _context.SaveChangesAsync();

        Assert.That((await _context.WikiPages.FirstAsync(p => p.Id == page.Id)).PublishedAt, Is.Not.Null);
    }

    /// <summary>A page nobody in the guild may read cannot coherently go to the open internet.</summary>
    [Test]
    public async Task A_private_page_cannot_be_published()
    {
        await SeedGuild(modulePermissions: ModulePermissions.PublishWikiPublicly);
        await SeedWiki(Slug);
        var page = await SeedPage(published: false, visibility: WikiVisibility.Private);

        var result = await _wiki.SetWikiPagePublication(
            GuildId, page.Id, new SetWikiPagePublicationDto { Published = true },
            _permissionService, _context, TestPrincipal.Create(UserId), new AuditLogService(_context));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    /// <summary>An off-instance cover is a per-visitor beacon aimed wherever its author chose.</summary>
    [Test]
    public async Task A_page_with_an_off_instance_cover_cannot_be_published()
    {
        await SeedGuild(modulePermissions: ModulePermissions.PublishWikiPublicly);
        await SeedWiki(Slug);

        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = GuildId, Title = "Covered", Content = "x", AuthorId = UserId,
            CoverUrl = "https://tracker.example/pixel.gif",
        });
        _context.WikiPages.Add(page);
        await _context.SaveChangesAsync();

        var result = await _wiki.SetWikiPagePublication(
            GuildId, page.Id, new SetWikiPagePublicationDto { Published = true },
            _permissionService, _context, TestPrincipal.Create(UserId), new AuditLogService(_context));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    /// <summary>A household may not claim a slug in the first place.</summary>
    [Test]
    public async Task A_household_cannot_publish_its_wiki()
    {
        await SeedGuild(kind: GuildKind.Household, modulePermissions: ModulePermissions.PublishWikiPublicly);
        await SeedWiki(publishedSlug: null);

        var result = await _wiki.SetWikiPublication(
            GuildId, new SetWikiPublicationDto { Slug = Slug },
            _permissionService, _context, TestPrincipal.Create(UserId), _vanity, new AuditLogService(_context));

        Assert.That(result, Is.Not.InstanceOf<Ok<WikiPublicationDto>>());
        Assert.That(await _context.Wikis.AnyAsync(w => w.PublishedSlug != null), Is.False);
    }

    /// <summary>Two guilds cannot answer on one address.</summary>
    [Test]
    public async Task A_slug_another_guild_holds_is_a_conflict()
    {
        await SeedGuild(modulePermissions: ModulePermissions.PublishWikiPublicly);
        await SeedWiki(publishedSlug: null);

        var other = Wiki.Create("guild-2");
        other.Publish(Slug);
        _context.Wikis.Add(other);
        await _context.SaveChangesAsync();

        var result = await _wiki.SetWikiPublication(
            GuildId, new SetWikiPublicationDto { Slug = Slug },
            _permissionService, _context, TestPrincipal.Create(UserId), _vanity, new AuditLogService(_context));

        Assert.That(result, Is.InstanceOf<Conflict<string>>());
    }

    // ══════════════════════════════════════════ Visibility on the private path
    // ══════════════════════════════════════════

    /// <summary>The column finally does something inside the guild too.</summary>
    [Test]
    public async Task A_private_page_is_hidden_from_an_ordinary_member()
    {
        await SeedGuild(modulePermissions: ModulePermissions.ViewWiki);

        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = GuildId, Title = "Secret", Content = "x", AuthorId = "somebody-else",
            Visibility = WikiVisibility.Private,
        });
        _context.WikiPages.Add(page);
        await _context.SaveChangesAsync();

        var result = await _wiki.GetWikiPage(GuildId, page.Id, _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task A_private_page_is_visible_to_its_author()
    {
        await SeedGuild(modulePermissions: ModulePermissions.ViewWiki);
        await SeedPage(published: false, visibility: WikiVisibility.Private);

        var page = await _context.WikiPages.FirstAsync();
        var result = await _wiki.GetWikiPage(GuildId, page.Id, _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<WikiPageDto>>());
    }

    [Test]
    public async Task A_private_page_is_visible_to_someone_who_may_already_rewrite_it()
    {
        await SeedGuild(modulePermissions: ModulePermissions.ViewWiki | ModulePermissions.EditAnyWikiPage);

        var page = WikiPage.Create(new CreateWikiPageParams
        {
            GuildId = GuildId, Title = "Secret", Content = "x", AuthorId = "somebody-else",
            Visibility = WikiVisibility.Private,
        });
        _context.WikiPages.Add(page);
        await _context.SaveChangesAsync();

        var result = await _wiki.GetWikiPage(GuildId, page.Id, _permissionService, _context, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<WikiPageDto>>());
    }
}
