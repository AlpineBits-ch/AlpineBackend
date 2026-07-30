using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Endpoints;

/// <summary>
/// Covers ForumPostEndpoint: the filtered/sorted/keyset-paginated post listing (match any vs all,
/// pinned hoisting, activity vs creation ordering, archived visibility), the replace-semantics tag
/// write with its author-vs-moderator split, and pin/lock.
/// </summary>
[TestFixture]
public class ForumPostEndpointTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string OtherUserId = "user-2";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private ForumService _forumService = null!;
    private FakeInvokingMessageBus _bus = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private ForumPostEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _forumService = new ForumService(_context);
        _bus = new FakeInvokingMessageBus();
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new ForumPostEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task<Channel> SeedForum(Permissions rolePermissions, ChannelType type = ChannelType.Forum)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role { Id = RoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone, Permissions = rolePermissions, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _context.GuildMembers.Add(new GuildMember { Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{GuildId}" });
        _context.RoleMembers.Add(new RoleMember { Id = "rm-1", RoleId = RoleId, MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });

        var forum = Channel.Create(new CreateChannelParams { Name = "feedback", Type = type, GuildId = GuildId, Description = "d" });
        _context.Channels.Add(forum);
        await _context.SaveChangesAsync();
        return forum;
    }

    private Channel AddPost(string forumId, string name, string? createdBy = null,
        DateTimeOffset? createdAt = null, DateTimeOffset? lastActivityAt = null,
        bool pinned = false, bool archived = false)
    {
        var post = Channel.Create(new CreateChannelParams
        {
            Name = name, Type = ChannelType.Thread, GuildId = GuildId,
            ParentChannelId = forumId, CreatedByUserId = createdBy ?? UserId, Description = "",
        });
        if (createdAt is not null) post.CreatedAt = createdAt.Value;
        post.LastActivityAt = lastActivityAt;
        post.IsPinned = pinned;
        post.IsArchived = archived;
        _context.Channels.Add(post);
        return post;
    }

    private async Task<ForumTag> SeedTag(string forumId, string name, int position = 0, bool moderated = false)
    {
        var tag = ForumTag.Create(new CreateForumTagParams
        {
            ChannelId = forumId, GuildId = GuildId, Name = name, Position = position, Moderated = moderated,
        });
        _context.ForumTags.Add(tag);
        await _context.SaveChangesAsync();
        return tag;
    }

    private void Apply(string postId, params string[] tagIds)
    {
        foreach (var tagId in tagIds)
            _context.ForumPostTags.Add(new ForumPostTag { ThreadChannelId = postId, TagId = tagId, CreatedAt = DateTimeOffset.UtcNow });
    }

    private Task<IResult> List(string forumId, string? tagIds = null, string? match = null,
        string? sort = null, string? archived = null, int? limit = null, string? cursor = null, string? asUser = null) =>
        _endpoint.ListPostsAsync(forumId, _permissionService, _context, _forumService,
            TestPrincipal.Create(asUser ?? UserId), tagIds, match, sort, archived, limit, cursor);

    // ══════════════════════════════════════════════════════════════════════
    // ListPostsAsync — access
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ListPosts_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.ListPostsAsync("nope", _permissionService, _context, _forumService,
            TestPrincipal.CreateAnonymous(), null, null, null, null, null, null);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task ListPosts_ChannelIsNotAForum_ReturnsNotFound()
    {
        var text = await SeedForum(Permissions.ViewChannel, ChannelType.Text);
        Assert.That(await List(text.Id), Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task ListPosts_LacksViewChannel_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.None);
        Assert.That(await List(forum.Id), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ListPosts_UnknownSort_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        Assert.That(await List(forum.Id, sort: "sideways"), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ListPosts_UnknownMatch_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        Assert.That(await List(forum.Id, match: "maybe"), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ListPosts_MalformedCursor_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        Assert.That(await List(forum.Id, cursor: "!!!not-a-cursor"), Is.InstanceOf<BadRequest<string>>());
    }

    // ══════════════════════════════════════════════════════════════════════
    // ListPostsAsync — ordering and visibility
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ListPosts_ExcludesArchivedByDefault()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        AddPost(forum.Id, "live");
        AddPost(forum.Id, "gone", archived: true);
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id)).Value!;

        Assert.That(page.Posts.Select(p => p.Name), Is.EqualTo(new[] { "live" }));
    }

    [Test]
    public async Task ListPosts_ArchivedAll_IncludesBoth()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        AddPost(forum.Id, "live");
        AddPost(forum.Id, "gone", archived: true);
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, archived: "all")).Value!;

        Assert.That(page.Posts, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ListPosts_ArchivedTrue_ReturnsOnlyArchived()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        AddPost(forum.Id, "live");
        AddPost(forum.Id, "gone", archived: true);
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, archived: "true")).Value!;

        Assert.That(page.Posts.Select(p => p.Name), Is.EqualTo(new[] { "gone" }));
    }

    [Test]
    public async Task ListPosts_PinnedPostsSortFirstDespiteBeingOlder()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var now = DateTimeOffset.UtcNow;
        AddPost(forum.Id, "recent", createdAt: now);
        AddPost(forum.Id, "ancient-but-pinned", createdAt: now.AddDays(-30), pinned: true);
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, sort: "created")).Value!;

        Assert.That(page.Posts[0].Name, Is.EqualTo("ancient-but-pinned"));
    }

    [Test]
    public async Task ListPosts_SortActivity_UsesLastActivityOverCreation()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var now = DateTimeOffset.UtcNow;
        AddPost(forum.Id, "new-but-quiet", createdAt: now, lastActivityAt: null);
        AddPost(forum.Id, "old-but-busy", createdAt: now.AddDays(-10), lastActivityAt: now.AddMinutes(5));
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, sort: "activity")).Value!;

        Assert.That(page.Posts[0].Name, Is.EqualTo("old-but-busy"));
    }

    [Test]
    public async Task ListPosts_SortCreated_IgnoresActivity()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var now = DateTimeOffset.UtcNow;
        AddPost(forum.Id, "new-but-quiet", createdAt: now);
        AddPost(forum.Id, "old-but-busy", createdAt: now.AddDays(-10), lastActivityAt: now.AddMinutes(5));
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, sort: "created")).Value!;

        Assert.That(page.Posts[0].Name, Is.EqualTo("new-but-quiet"));
    }

    [Test]
    public async Task ListPosts_NoSortParam_FallsBackToForumDefault()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        _context.ForumConfigs.Add(new ForumConfig { ChannelId = forum.Id, GuildId = GuildId, DefaultSortOrder = ForumSortOrder.CreationDate });
        var now = DateTimeOffset.UtcNow;
        AddPost(forum.Id, "new-but-quiet", createdAt: now);
        AddPost(forum.Id, "old-but-busy", createdAt: now.AddDays(-10), lastActivityAt: now.AddMinutes(5));
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id)).Value!;

        Assert.That(page.Posts[0].Name, Is.EqualTo("new-but-quiet"));
    }

    [Test]
    public async Task ListPosts_CarriesAppliedTagIdsOrderedByTagPosition()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var b = await SeedTag(forum.Id, "beta", 1);
        var a = await SeedTag(forum.Id, "alpha", 0);
        var post = AddPost(forum.Id, "p");
        await _context.SaveChangesAsync();
        Apply(post.Id, b.Id, a.Id);
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id)).Value!;

        Assert.That(page.Posts[0].TagIds, Is.EqualTo(new[] { a.Id, b.Id }));
    }

    // ══════════════════════════════════════════════════════════════════════
    // ListPostsAsync — tag filtering
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ListPosts_MatchAny_ReturnsPostsCarryingEitherTag()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var bug = await SeedTag(forum.Id, "bug", 0);
        var ui = await SeedTag(forum.Id, "ui", 1);
        var p1 = AddPost(forum.Id, "only-bug");
        var p2 = AddPost(forum.Id, "only-ui");
        AddPost(forum.Id, "untagged");
        await _context.SaveChangesAsync();
        Apply(p1.Id, bug.Id);
        Apply(p2.Id, ui.Id);
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, tagIds: $"{bug.Id},{ui.Id}", match: "any")).Value!;

        Assert.That(page.Posts.Select(p => p.Name), Is.EquivalentTo(new[] { "only-bug", "only-ui" }));
    }

    [Test]
    public async Task ListPosts_MatchAll_RequiresEveryTag()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var bug = await SeedTag(forum.Id, "bug", 0);
        var ui = await SeedTag(forum.Id, "ui", 1);
        var both = AddPost(forum.Id, "both");
        var one = AddPost(forum.Id, "only-bug");
        await _context.SaveChangesAsync();
        Apply(both.Id, bug.Id, ui.Id);
        Apply(one.Id, bug.Id);
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, tagIds: $"{bug.Id},{ui.Id}", match: "all")).Value!;

        Assert.That(page.Posts.Select(p => p.Name), Is.EqualTo(new[] { "both" }));
    }

    [Test]
    public async Task ListPosts_SingleTag_AnyAndAllAgree()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var bug = await SeedTag(forum.Id, "bug");
        var post = AddPost(forum.Id, "tagged");
        AddPost(forum.Id, "untagged");
        await _context.SaveChangesAsync();
        Apply(post.Id, bug.Id);
        await _context.SaveChangesAsync();

        var any = ((Ok<ForumPostPageDto>)await List(forum.Id, tagIds: bug.Id, match: "any")).Value!;
        var all = ((Ok<ForumPostPageDto>)await List(forum.Id, tagIds: bug.Id, match: "all")).Value!;

        Assert.That(any.Posts.Select(p => p.Name), Is.EqualTo(new[] { "tagged" }));
        Assert.That(all.Posts.Select(p => p.Name), Is.EqualTo(new[] { "tagged" }));
    }

    [Test]
    public async Task ListPosts_TagFilterCombinesWithArchivedFilter()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var bug = await SeedTag(forum.Id, "bug");
        var live = AddPost(forum.Id, "live");
        var gone = AddPost(forum.Id, "gone", archived: true);
        await _context.SaveChangesAsync();
        Apply(live.Id, bug.Id);
        Apply(gone.Id, bug.Id);
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, tagIds: bug.Id)).Value!;

        Assert.That(page.Posts.Select(p => p.Name), Is.EqualTo(new[] { "live" }));
    }

    // ══════════════════════════════════════════════════════════════════════
    // ListPostsAsync — pagination
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ListPosts_FullPage_ReturnsCursor()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 3; i++) AddPost(forum.Id, $"p{i}", createdAt: now.AddMinutes(-i));
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, sort: "created", limit: 2)).Value!;

        Assert.That(page.Posts, Has.Count.EqualTo(2));
        Assert.That(page.NextCursor, Is.Not.Null);
    }

    [Test]
    public async Task ListPosts_LastPage_ReturnsNullCursor()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        AddPost(forum.Id, "only");
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, limit: 25)).Value!;

        Assert.That(page.NextCursor, Is.Null);
    }

    [Test]
    public async Task ListPosts_PagingThrough_VisitsEveryPostExactlyOnce()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 7; i++) AddPost(forum.Id, $"p{i}", createdAt: now.AddMinutes(-i));
        await _context.SaveChangesAsync();

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = ((Ok<ForumPostPageDto>)await List(forum.Id, sort: "created", limit: 3, cursor: cursor)).Value!;
            seen.AddRange(page.Posts.Select(p => p.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.That(seen, Is.Unique);
        Assert.That(seen, Has.Count.EqualTo(7));
        Assert.That(seen, Is.EqualTo(new[] { "p0", "p1", "p2", "p3", "p4", "p5", "p6" }));
    }

    [Test]
    public async Task ListPosts_PagingWithIdenticalTimestamps_StillVisitsEachPostOnce()
    {
        // The id tiebreak is the whole reason the cursor carries one - without it, posts sharing
        // a timestamp shadow each other across the page boundary.
        var forum = await SeedForum(Permissions.ViewChannel);
        var sameInstant = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) AddPost(forum.Id, $"p{i}", createdAt: sameInstant);
        await _context.SaveChangesAsync();

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = ((Ok<ForumPostPageDto>)await List(forum.Id, sort: "created", limit: 2, cursor: cursor)).Value!;
            seen.AddRange(page.Posts.Select(p => p.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.That(seen, Is.Unique);
        Assert.That(seen, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task ListPosts_PagingAcrossThePinnedBoundary_DoesNotRepeatPinnedPosts()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var now = DateTimeOffset.UtcNow;
        AddPost(forum.Id, "pin-a", createdAt: now.AddMinutes(-1), pinned: true);
        AddPost(forum.Id, "pin-b", createdAt: now.AddMinutes(-2), pinned: true);
        for (var i = 0; i < 3; i++) AddPost(forum.Id, $"plain{i}", createdAt: now.AddMinutes(-10 - i));
        await _context.SaveChangesAsync();

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = ((Ok<ForumPostPageDto>)await List(forum.Id, sort: "created", limit: 2, cursor: cursor)).Value!;
            seen.AddRange(page.Posts.Select(p => p.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.That(seen, Is.Unique);
        Assert.That(seen.Take(2), Is.EqualTo(new[] { "pin-a", "pin-b" }));
        Assert.That(seen, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task ListPosts_LimitIsClampedToTheMaximum()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        for (var i = 0; i < 55; i++) AddPost(forum.Id, $"p{i}", createdAt: DateTimeOffset.UtcNow.AddMinutes(-i));
        await _context.SaveChangesAsync();

        var page = ((Ok<ForumPostPageDto>)await List(forum.Id, limit: 500)).Value!;

        Assert.That(page.Posts, Has.Count.EqualTo(50));
    }

    // ══════════════════════════════════════════════════════════════════════
    // SetTagsAsync
    // ══════════════════════════════════════════════════════════════════════

    private Task<IResult> SetTags(string threadId, List<string> tagIds, string asUser) =>
        _endpoint.SetTagsAsync(threadId, new SetThreadTagsDto { TagIds = tagIds }, _permissionService, _context,
            _forumService, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(asUser));

    [Test]
    public async Task SetTags_PostDoesNotExist_ReturnsNotFound()
    {
        await SeedForum(Permissions.ViewChannel);
        Assert.That(await SetTags("chan_missing", [], UserId), Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task SetTags_ParentIsNotAForum_ReturnsNotFound()
    {
        var text = await SeedForum(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Text);
        var thread = AddPost(text.Id, "t");
        await _context.SaveChangesAsync();

        Assert.That(await SetTags(thread.Id, [], UserId), Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task SetTags_AuthorCanTagTheirOwnPost()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var tag = await SeedTag(forum.Id, "bug");
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        var result = await SetTags(post.Id, [tag.Id], UserId);
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<ForumPostDto>>());
        Assert.That(await _context.ForumPostTags.AnyAsync(pt => pt.ThreadChannelId == post.Id && pt.TagId == tag.Id), Is.True);
    }

    [Test]
    public async Task SetTags_NonAuthorWithoutManageAnyThread_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var tag = await SeedTag(forum.Id, "bug");
        var post = AddPost(forum.Id, "p", createdBy: OtherUserId);
        await _context.SaveChangesAsync();

        Assert.That(await SetTags(post.Id, [tag.Id], UserId), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task SetTags_NonAuthorWithManageAnyThread_Succeeds()
    {
        var forum = await SeedForum(Permissions.ViewChannel | Permissions.ManageAnyThread);
        var tag = await SeedTag(forum.Id, "bug");
        var post = AddPost(forum.Id, "p", createdBy: OtherUserId);
        await _context.SaveChangesAsync();

        Assert.That(await SetTags(post.Id, [tag.Id], UserId), Is.InstanceOf<Ok<ForumPostDto>>());
    }

    [Test]
    public async Task SetTags_AuthorApplyingModeratedTag_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var tag = await SeedTag(forum.Id, "confirmed", moderated: true);
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        Assert.That(await SetTags(post.Id, [tag.Id], UserId), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task SetTags_ModeratorApplyingModeratedTag_Succeeds()
    {
        var forum = await SeedForum(Permissions.ViewChannel | Permissions.ManageAnyThread);
        var tag = await SeedTag(forum.Id, "confirmed", moderated: true);
        var post = AddPost(forum.Id, "p", createdBy: OtherUserId);
        await _context.SaveChangesAsync();

        Assert.That(await SetTags(post.Id, [tag.Id], UserId), Is.InstanceOf<Ok<ForumPostDto>>());
    }

    [Test]
    public async Task SetTags_RequireTagWithEmptySet_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        _context.ForumConfigs.Add(new ForumConfig { ChannelId = forum.Id, GuildId = GuildId, RequireTag = true });
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        Assert.That(await SetTags(post.Id, [], UserId), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task SetTags_TagFromAnotherForum_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var other = Channel.Create(new CreateChannelParams { Name = "other", Type = ChannelType.Forum, GuildId = GuildId, Description = "" });
        _context.Channels.Add(other);
        await _context.SaveChangesAsync();
        var foreignTag = await SeedTag(other.Id, "elsewhere");
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        Assert.That(await SetTags(post.Id, [foreignTag.Id], UserId), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task SetTags_Valid_BroadcastsThreadUpdatedWithTagIds()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var tag = await SeedTag(forum.Id, "bug");
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        await SetTags(post.Id, [tag.Id], UserId);

        var clients = (FakeHubClients)_hub.Clients;
        Assert.That(clients.SentMessages.Any(m => m.Method == "guild.ThreadUpdated"), Is.True);
    }

    [Test]
    public async Task SetTags_Valid_WritesAuditEntry()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var tag = await SeedTag(forum.Id, "bug");
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        await SetTags(post.Id, [tag.Id], UserId);
        await _context.SaveChangesAsync();

        Assert.That(_context.Set<GuildAuditLogEntry>().Count(e => e.ActionType == AuditActionType.ThreadTagsUpdated), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Pin / lock
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SetPinned_WithoutManageAnyThread_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        var result = await _endpoint.SetPinnedAsync(post.Id, new SetThreadPinnedDto { Pinned = true },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task SetPinned_WithManageAnyThread_PinsThePost()
    {
        var forum = await SeedForum(Permissions.ViewChannel | Permissions.ManageAnyThread);
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        var result = await _endpoint.SetPinnedAsync(post.Id, new SetThreadPinnedDto { Pinned = true },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That((await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == post.Id)).IsPinned, Is.True);
    }

    [Test]
    public async Task SetPinned_False_UnpinsAgain()
    {
        var forum = await SeedForum(Permissions.ViewChannel | Permissions.ManageAnyThread);
        var post = AddPost(forum.Id, "p", createdBy: UserId, pinned: true);
        await _context.SaveChangesAsync();

        await _endpoint.SetPinnedAsync(post.Id, new SetThreadPinnedDto { Pinned = false },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That((await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == post.Id)).IsPinned, Is.False);
    }

    [Test]
    public async Task SetLocked_WithManageAnyThread_LocksWithoutArchiving()
    {
        // Locked and archived are orthogonal - locking must not quietly archive the post.
        var forum = await SeedForum(Permissions.ViewChannel | Permissions.ManageAnyThread);
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        var result = await _endpoint.SetLockedAsync(post.Id, new SetThreadLockedDto { Locked = true },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var stored = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == post.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.IsLocked, Is.True);
            Assert.That(stored.IsArchived, Is.False);
        });
    }

    [Test]
    public async Task SetLocked_WithoutManageAnyThread_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var post = AddPost(forum.Id, "p", createdBy: UserId);
        await _context.SaveChangesAsync();

        var result = await _endpoint.SetLockedAsync(post.Id, new SetThreadLockedDto { Locked = true },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }
}
