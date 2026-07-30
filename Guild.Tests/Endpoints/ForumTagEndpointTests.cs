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
/// Covers ForumTagEndpoint: tag CRUD (per-forum cap, case-insensitive name uniqueness, emoji
/// exclusivity, ManageChannel gating resolved per channel rather than per guild), the
/// all-or-nothing reorder, and forum config (read returns unpersisted defaults, write inserts).
/// </summary>
[TestFixture]
public class ForumTagEndpointTests
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
    private ForumService _forumService = null!;
    private FakeHubContext _hub = null!;
    private GuildHydrateService _hydrateService = null!;
    private ForumTagEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _auditLog = new AuditLogService(_context);
        _forumService = new ForumService(_context);
        _hub = new FakeHubContext();
        _hydrateService = new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance);
        _endpoint = new ForumTagEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Seeds a member holding <paramref name="rolePermissions"/> on every channel via the
    /// Everyone role, plus a forum channel to hang tags off.</summary>
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

    // ══════════════════════════════════════════════════════════════════════ ListTagsAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ListTags_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.ListTagsAsync("nope", _permissionService, _context, _forumService, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task ListTags_ChannelIsNotAForum_ReturnsNotFound()
    {
        var text = await SeedForum(Permissions.ViewChannel, ChannelType.Text);

        var result = await _endpoint.ListTagsAsync(text.Id, _permissionService, _context, _forumService, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task ListTags_LacksViewChannel_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.None);

        var result = await _endpoint.ListTagsAsync(forum.Id, _permissionService, _context, _forumService, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ListTags_ReturnsTagsOrderedByPosition()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        await SeedTag(forum.Id, "charlie", position: 2);
        await SeedTag(forum.Id, "alpha", position: 0);
        await SeedTag(forum.Id, "beta", position: 1);

        var result = await _endpoint.ListTagsAsync(forum.Id, _permissionService, _context, _forumService, TestPrincipal.Create(UserId));

        var ok = result as Ok<List<ForumTagDto>>;
        Assert.That(ok!.Value!.Select(t => t.Name), Is.EqualTo(new[] { "alpha", "beta", "charlie" }));
    }

    [Test]
    public async Task ListTags_MediaChannelIsTreatedAsAForum()
    {
        var media = await SeedForum(Permissions.ViewChannel, ChannelType.Media);
        await SeedTag(media.Id, "alpha");

        var result = await _endpoint.ListTagsAsync(media.Id, _permissionService, _context, _forumService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<List<ForumTagDto>>>());
    }

    [Test]
    public async Task ListTags_CarriesNonArchivedPostCount()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var tag = await SeedTag(forum.Id, "bug");

        var live = Channel.Create(new CreateChannelParams { Name = "p1", Type = ChannelType.Thread, GuildId = GuildId, ParentChannelId = forum.Id, Description = "" });
        var archived = Channel.Create(new CreateChannelParams { Name = "p2", Type = ChannelType.Thread, GuildId = GuildId, ParentChannelId = forum.Id, Description = "" });
        archived.IsArchived = true;
        _context.Channels.AddRange(live, archived);
        _context.ForumPostTags.AddRange(
            new ForumPostTag { ThreadChannelId = live.Id, TagId = tag.Id, CreatedAt = DateTimeOffset.UtcNow },
            new ForumPostTag { ThreadChannelId = archived.Id, TagId = tag.Id, CreatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.ListTagsAsync(forum.Id, _permissionService, _context, _forumService, TestPrincipal.Create(UserId));

        var ok = result as Ok<List<ForumTagDto>>;
        Assert.That(ok!.Value![0].PostCount, Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ CreateTagAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateTag_LacksManageChannel_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.ViewChannel);

        var result = await _endpoint.CreateTagAsync(forum.Id, new CreateForumTagDto { Name = "bug" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateTag_Valid_PersistsAndReturnsTag()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        var result = await _endpoint.CreateTagAsync(forum.Id,
            new CreateForumTagDto { Name = "bug", EmojiName = "🐛", Color = "#e74c3c" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<ForumTagDto>;
        Assert.That(ok, Is.Not.Null);
        var stored = await _context.ForumTags.AsNoTracking().FirstAsync(t => t.Id == ok!.Value!.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.Name, Is.EqualTo("bug"));
            Assert.That(stored.EmojiName, Is.EqualTo("🐛"));
            Assert.That(stored.Color, Is.EqualTo("#e74c3c"));
            Assert.That(stored.GuildId, Is.EqualTo(GuildId));
        });
    }

    [Test]
    public async Task CreateTag_AppendsPositionAfterExistingTags()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        await SeedTag(forum.Id, "alpha", position: 0);
        await SeedTag(forum.Id, "beta", position: 1);

        var result = await _endpoint.CreateTagAsync(forum.Id, new CreateForumTagDto { Name = "gamma" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        var ok = result as Ok<ForumTagDto>;
        Assert.That(ok!.Value!.Position, Is.EqualTo(2));
    }

    [Test]
    public async Task CreateTag_DuplicateNameDifferentCase_ReturnsConflict()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        await SeedTag(forum.Id, "Bug");

        var result = await _endpoint.CreateTagAsync(forum.Id, new CreateForumTagDto { Name = "bug" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task CreateTag_SameNameInADifferentForum_IsAllowed()
    {
        // Tags are scoped to one forum - two forums sharing a tag name is normal, not a clash.
        var forum = await SeedForum(Permissions.ManageChannel);
        var other = Channel.Create(new CreateChannelParams { Name = "other", Type = ChannelType.Forum, GuildId = GuildId, Description = "" });
        _context.Channels.Add(other);
        await _context.SaveChangesAsync();
        await SeedTag(other.Id, "bug");

        var result = await _endpoint.CreateTagAsync(forum.Id, new CreateForumTagDto { Name = "bug" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<ForumTagDto>>());
    }

    [Test]
    public async Task CreateTag_AtForumCap_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        for (var i = 0; i < ForumTag.MaxTagsPerForum; i++) await SeedTag(forum.Id, $"tag{i}", i);

        var result = await _endpoint.CreateTagAsync(forum.Id, new CreateForumTagDto { Name = "one-too-many" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateTag_BothEmojiFields_ReturnsValidationProblem()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        var result = await _endpoint.CreateTagAsync(forum.Id,
            new CreateForumTagDto { Name = "bug", EmojiId = "emoj_1", EmojiName = "🐛" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        // Results.ValidationProblem materializes as ProblemHttpResult, not the ValidationProblem type.
        Assert.That(result, Is.InstanceOf<ProblemHttpResult>());
        Assert.That(((ProblemHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task CreateTag_MalformedColor_ReturnsValidationProblem()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        var result = await _endpoint.CreateTagAsync(forum.Id, new CreateForumTagDto { Name = "bug", Color = "red" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        // Results.ValidationProblem materializes as ProblemHttpResult, not the ValidationProblem type.
        Assert.That(result, Is.InstanceOf<ProblemHttpResult>());
        Assert.That(((ProblemHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task CreateTag_NameOverLengthCap_ReturnsValidationProblem()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        var result = await _endpoint.CreateTagAsync(forum.Id,
            new CreateForumTagDto { Name = new string('x', ForumTag.MaxNameLength + 1) },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        // Results.ValidationProblem materializes as ProblemHttpResult, not the ValidationProblem type.
        Assert.That(result, Is.InstanceOf<ProblemHttpResult>());
        Assert.That(((ProblemHttpResult)result).StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
    }

    [Test]
    public async Task CreateTag_Valid_BroadcastsAndWritesAuditEntry()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        await _endpoint.CreateTagAsync(forum.Id, new CreateForumTagDto { Name = "bug" },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var clients = (FakeHubClients)_hub.Clients;
        Assert.That(clients.SentMessages.Any(m => m.Method == "guild.ForumTagCreated"), Is.True);
        Assert.That(_context.Set<GuildAuditLogEntry>().Count(e => e.ActionType == AuditActionType.ForumTagCreated), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ UpdateTagAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UpdateTag_DoesNotExist_ReturnsNotFound()
    {
        await SeedForum(Permissions.ManageChannel);

        var result = await _endpoint.UpdateTagAsync("ftag_missing", new UpdateForumTagDto { Name = "x" },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task UpdateTag_LacksManageChannel_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var tag = await SeedTag(forum.Id, "bug");

        var result = await _endpoint.UpdateTagAsync(tag.Id, new UpdateForumTagDto { Name = "feature" },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateTag_OmittedFieldsAreLeftUnchanged()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var tag = await SeedTag(forum.Id, "bug");
        tag.Color = "#123456";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateTagAsync(tag.Id, new UpdateForumTagDto { Name = "defect" },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.ForumTags.AsNoTracking().FirstAsync(t => t.Id == tag.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.Name, Is.EqualTo("defect"));
            Assert.That(stored.Color, Is.EqualTo("#123456"));
        });
    }

    [Test]
    public async Task UpdateTag_EmptyEmojiString_ClearsTheEmoji()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var tag = await SeedTag(forum.Id, "bug");
        tag.EmojiName = "🐛";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateTagAsync(tag.Id, new UpdateForumTagDto { EmojiName = "" },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.ForumTags.AsNoTracking().FirstAsync(t => t.Id == tag.Id);
        Assert.That(stored.EmojiName, Is.Null);
    }

    [Test]
    public async Task UpdateTag_SettingOneEmojiClearsTheOther()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var tag = await SeedTag(forum.Id, "bug");
        tag.EmojiName = "🐛";
        await _context.SaveChangesAsync();

        await _endpoint.UpdateTagAsync(tag.Id, new UpdateForumTagDto { EmojiId = "emoj_1" },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.ForumTags.AsNoTracking().FirstAsync(t => t.Id == tag.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.EmojiId, Is.EqualTo("emoj_1"));
            Assert.That(stored.EmojiName, Is.Null);
        });
    }

    [Test]
    public async Task UpdateTag_RenamingOntoAnotherTagsName_ReturnsConflict()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        await SeedTag(forum.Id, "bug", 0);
        var other = await SeedTag(forum.Id, "feature", 1);

        var result = await _endpoint.UpdateTagAsync(other.Id, new UpdateForumTagDto { Name = "BUG" },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task UpdateTag_RenamingToItsOwnName_IsAllowed()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var tag = await SeedTag(forum.Id, "bug");

        var result = await _endpoint.UpdateTagAsync(tag.Id, new UpdateForumTagDto { Name = "bug", Color = "#abcdef" },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<ForumTagDto>>());
    }

    [Test]
    public async Task UpdateTag_MakingItModerated_LeavesExistingApplicationsInPlace()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var tag = await SeedTag(forum.Id, "bug");
        _context.ForumPostTags.Add(new ForumPostTag { ThreadChannelId = "chan-post", TagId = tag.Id, CreatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        await _endpoint.UpdateTagAsync(tag.Id, new UpdateForumTagDto { Moderated = true },
            _permissionService, _context, _forumService, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(await _context.ForumPostTags.CountAsync(pt => pt.TagId == tag.Id), Is.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ DeleteTagAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteTag_LacksManageChannel_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.ViewChannel);
        var tag = await SeedTag(forum.Id, "bug");

        var result = await _endpoint.DeleteTagAsync(tag.Id, _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task DeleteTag_RemovesTagAndItsApplications()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var tag = await SeedTag(forum.Id, "bug");
        _context.ForumPostTags.Add(new ForumPostTag { ThreadChannelId = "chan-post", TagId = tag.Id, CreatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.DeleteTagAsync(tag.Id, _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(await _context.ForumTags.AnyAsync(t => t.Id == tag.Id), Is.False);
        Assert.That(await _context.ForumPostTags.AnyAsync(pt => pt.TagId == tag.Id), Is.False);
    }

    [Test]
    public async Task DeleteTag_BroadcastsDeletedEvent()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var tag = await SeedTag(forum.Id, "bug");

        await _endpoint.DeleteTagAsync(tag.Id, _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        var clients = (FakeHubClients)_hub.Clients;
        Assert.That(clients.SentMessages.Any(m => m.Method == "guild.ForumTagDeleted"), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ ReorderTagsAsync
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ReorderTags_AssignsPositionsFromArrayIndex()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var a = await SeedTag(forum.Id, "alpha", 0);
        var b = await SeedTag(forum.Id, "beta", 1);
        var c = await SeedTag(forum.Id, "charlie", 2);

        var result = await _endpoint.ReorderTagsAsync(forum.Id, new ReorderForumTagsDto { TagIds = [c.Id, a.Id, b.Id] },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        var stored = await _context.ForumTags.AsNoTracking().Where(t => t.ChannelId == forum.Id).ToListAsync();
        Assert.Multiple(() =>
        {
            Assert.That(stored.First(t => t.Id == c.Id).Position, Is.EqualTo(0));
            Assert.That(stored.First(t => t.Id == a.Id).Position, Is.EqualTo(1));
            Assert.That(stored.First(t => t.Id == b.Id).Position, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ReorderTags_PartialList_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var a = await SeedTag(forum.Id, "alpha", 0);
        await SeedTag(forum.Id, "beta", 1);

        var result = await _endpoint.ReorderTagsAsync(forum.Id, new ReorderForumTagsDto { TagIds = [a.Id] },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task ReorderTags_ContainingAForeignTag_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ManageChannel);
        var a = await SeedTag(forum.Id, "alpha", 0);

        var result = await _endpoint.ReorderTagsAsync(forum.Id, new ReorderForumTagsDto { TagIds = [a.Id, "ftag_elsewhere"] },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ══════════════════════════════════════════════════════════════════════ Config
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetConfig_Unconfigured_ReturnsDefaultsAndInsertsNothing()
    {
        var forum = await SeedForum(Permissions.ViewChannel);

        var result = await _endpoint.GetConfigAsync(forum.Id, _permissionService, _context, _forumService, TestPrincipal.Create(UserId));

        var ok = result as Ok<ForumConfigDto>;
        Assert.That(ok!.Value!.RequireTag, Is.False);
        Assert.That(await _context.ForumConfigs.AnyAsync(), Is.False);
    }

    [Test]
    public async Task UpdateConfig_LacksManageChannel_ReturnsForbid()
    {
        var forum = await SeedForum(Permissions.ViewChannel);

        var result = await _endpoint.UpdateConfigAsync(forum.Id, new UpdateForumConfigDto { RequireTag = true },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task UpdateConfig_FirstWrite_InsertsTheRow()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        var result = await _endpoint.UpdateConfigAsync(forum.Id,
            new UpdateForumConfigDto { RequireTag = true, DefaultSortOrder = ForumSortOrder.CreationDate },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Ok<ForumConfigDto>>());
        var stored = await _context.ForumConfigs.AsNoTracking().FirstAsync(c => c.ChannelId == forum.Id);
        Assert.Multiple(() =>
        {
            Assert.That(stored.RequireTag, Is.True);
            Assert.That(stored.DefaultSortOrder, Is.EqualTo(ForumSortOrder.CreationDate));
        });
    }

    [Test]
    public async Task UpdateConfig_SecondWrite_UpdatesRatherThanDuplicating()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        await _endpoint.UpdateConfigAsync(forum.Id, new UpdateForumConfigDto { RequireTag = true },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        await _endpoint.UpdateConfigAsync(forum.Id, new UpdateForumConfigDto { DefaultLayout = ForumLayout.Gallery },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var stored = await _context.ForumConfigs.AsNoTracking().Where(c => c.ChannelId == forum.Id).ToListAsync();
        Assert.That(stored, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(stored[0].RequireTag, Is.True, "the earlier field must survive a later partial write");
            Assert.That(stored[0].DefaultLayout, Is.EqualTo(ForumLayout.Gallery));
        });
    }

    [Test]
    public async Task UpdateConfig_UnsupportedAutoArchiveDuration_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        var result = await _endpoint.UpdateConfigAsync(forum.Id, new UpdateForumConfigDto { DefaultAutoArchiveMinutes = 999 },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_NegativeSlowMode_ReturnsBadRequest()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        var result = await _endpoint.UpdateConfigAsync(forum.Id, new UpdateForumConfigDto { DefaultThreadSlowModeSeconds = -1 },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task UpdateConfig_Valid_BroadcastsConfigUpdated()
    {
        var forum = await SeedForum(Permissions.ManageChannel);

        await _endpoint.UpdateConfigAsync(forum.Id, new UpdateForumConfigDto { RequireTag = true },
            _permissionService, _context, _auditLog, _hub, _hydrateService, TestPrincipal.Create(UserId));

        var clients = (FakeHubClients)_hub.Clients;
        Assert.That(clients.SentMessages.Any(m => m.Method == "guild.ForumConfigUpdated"), Is.True);
    }
}
