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
    private ForumService _forumService = null!;
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
        _forumService = new ForumService(_context);
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
        var result = await _endpoint.CreateThreadAsync("nonexistent", new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.CreateAnonymous());
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateThread_ParentDoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.CreateThreadAsync("nonexistent", new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task CreateThread_ParentIsVoiceChannel_ReturnsBadRequest()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads, ChannelType.Voice);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateThread_ParentIsSceneUnderTextChannel_Succeeds()
    {
        // The scene header carries a thread list of its own, so the plain route has to serve it.
        var text = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        var scene = await SeedChildChannel(text.Id, ChannelType.Scene, "a scene");

        var result = await _endpoint.CreateThreadAsync(scene.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
    }

    [Test]
    public async Task CreateThread_LacksCreateThreads_ReturnsForbid()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ViewChannel);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateThread_UnderTextParent_Valid_PersistsThread()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "my-thread" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));
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

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
    }

    [Test]
    public async Task CreateThread_WithInitialContent_SendsCreateMessageCommand()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);

        await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post", Content = "hello world" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>().Any(), Is.True);
    }

    [Test]
    public async Task CreateThread_NoContent_DoesNotSendCreateMessageCommand()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);

        await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>().Any(), Is.False);
    }

    [Test]
    public async Task CreateThread_Valid_PublishesThreadCreatedForBots()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);

        await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(_bus.Published.OfType<ThreadCreatedForBots>().Any(e => e.ParentChannelId == parent.Id), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════ CreateThreadAsync -
    // forum tags ══════════════════════════════════════════════════════════════════════

    private async Task<ForumTag> SeedTag(string forumId, string name, bool moderated = false)
    {
        var tag = ForumTag.Create(new CreateForumTagParams
        {
            ChannelId = forumId, GuildId = GuildId, Name = name, Moderated = moderated,
        });
        _context.ForumTags.Add(tag);
        await _context.SaveChangesAsync();
        return tag;
    }

    [Test]
    public async Task CreateThread_UnderForumParent_AppliesRequestedTags()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);
        var tag = await SeedTag(parent.Id, "bug");

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post", TagIds = [tag.Id] }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.ChannelDto>;
        Assert.That(ok, Is.Not.Null);
        Assert.That(await _context.ForumPostTags.AnyAsync(pt => pt.ThreadChannelId == ok!.Value!.Id && pt.TagId == tag.Id), Is.True);
    }

    [Test]
    public async Task CreateThread_UnderForumParent_RequireTagWithNoTags_ReturnsBadRequest()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);
        _context.ForumConfigs.Add(new ForumConfig { ChannelId = parent.Id, GuildId = GuildId, RequireTag = true });
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateThread_NonModeratorApplyingModeratedTag_ReturnsForbid()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);
        var tag = await SeedTag(parent.Id, "confirmed", moderated: true);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post", TagIds = [tag.Id] }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateThread_TagFromAnotherForum_ReturnsBadRequest()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);
        var other = Channel.Create(new CreateChannelParams { Name = "other", Type = ChannelType.Forum, GuildId = GuildId, Description = "" });
        _context.Channels.Add(other);
        await _context.SaveChangesAsync();
        var foreignTag = await SeedTag(other.Id, "elsewhere");

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post", TagIds = [foreignTag.Id] }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateThread_UnderForumParent_InheritsConfigSlowModeAndAutoArchive()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);
        _context.ForumConfigs.Add(new ForumConfig
        {
            ChannelId = parent.Id, GuildId = GuildId, DefaultThreadSlowModeSeconds = 30, DefaultAutoArchiveMinutes = 1440,
        });
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.ChannelDto>;
        var created = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == ok!.Value!.Id);
        Assert.Multiple(() =>
        {
            Assert.That(created.SlowModeSeconds, Is.EqualTo(30));
            Assert.That(created.AutoArchiveMinutes, Is.EqualTo(1440));
            Assert.That(created.AutoArchiveAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task CreateThread_UnderTextParent_TagIdsAreIgnored()
    {
        // A text channel has no tag vocabulary; sending tagIds there is a client mistake that
        // shouldn't fail the create.
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "t", TagIds = ["ftag_whatever"] }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
    }

    [Test]
    public async Task CreateThread_UnderMediaParent_Valid()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Media);

        var result = await _endpoint.CreateThreadAsync(parent.Id, new CreateThreadDto { Name = "post" }, _permissionService, _context, _hub, _hydrateService, _auditLog, _forumService, _bus, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
    }

    // ══════════════════════════════════════════════════════════════════ CreateThreadFromMessage
    // ══════════════════════════════════════════════════════════════════════

    private const string MessageId = "mesg_1";

    private void AttachReturns(AttachThreadOutcome outcome, string? existingThreadId = null) =>
        _bus.SetResponse<AttachThreadToMessageCommand>(new AttachThreadToMessageResponse
        {
            Outcome = outcome,
            ExistingThreadId = existingThreadId,
        });

    private Task<IResult> CreateFromMessage(string channelId, CreateThreadDto dto, string? userId = UserId) =>
        _endpoint.CreateThreadFromMessageAsync(channelId, MessageId, dto, _permissionService, _context, _hub,
            _hydrateService, _auditLog, _bus,
            userId is null ? TestPrincipal.CreateAnonymous() : TestPrincipal.Create(userId));

    [Test]
    public async Task CreateThreadFromMessage_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await CreateFromMessage("nonexistent", new CreateThreadDto { Name = "t" }, userId: null);
        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateThreadFromMessage_ParentDoesNotExist_ReturnsNotFound()
    {
        var result = await CreateFromMessage("nonexistent", new CreateThreadDto { Name = "t" });
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task CreateThreadFromMessage_ParentIsForum_ReturnsBadRequest()
    {
        // A forum post already is the thread, so there is no message to hang a second one off.
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    /// <summary>A room hanging off <paramref name="under"/>, which is what both halves of a scene
    /// are and what an ordinary message thread is.</summary>
    private async Task<Channel> SeedChildChannel(string under, ChannelType type, string name = "child")
    {
        var child = Channel.Create(new CreateChannelParams
        {
            Name = name, Description = "", Type = type, GuildId = GuildId, ParentChannelId = under,
        });
        _context.Channels.Add(child);
        await _context.SaveChangesAsync();
        return child;
    }

    [Test]
    public async Task CreateThreadFromMessage_ParentIsSceneUnderTextChannel_Succeeds()
    {
        var text = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        var scene = await SeedChildChannel(text.Id, ChannelType.Scene, "a scene");
        AttachReturns(AttachThreadOutcome.Attached);

        var result = await CreateFromMessage(scene.Id, new CreateThreadDto { Name = "t" });
        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
    }

    [Test]
    public async Task CreateThreadFromMessage_ParentIsOutOfCharacterRoom_Succeeds()
    {
        // The OOC half of a scene is an ordinary Thread hanging off the same text channel.
        var text = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        var ooc = await SeedChildChannel(text.Id, ChannelType.Thread, "a scene (OOC)");
        AttachReturns(AttachThreadOutcome.Attached);

        var result = await CreateFromMessage(ooc.Id, new CreateThreadDto { Name = "t" });
        Assert.That(result, Is.InstanceOf<Ok<Guild.Application.Dtos.Response.ChannelDto>>());
    }

    [Test]
    public async Task CreateThreadFromMessage_ParentIsForumPost_ReturnsBadRequest()
    {
        var forum = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel, ChannelType.Forum);
        var post = await SeedChildChannel(forum.Id, ChannelType.Thread, "a post");

        var result = await CreateFromMessage(post.Id, new CreateThreadDto { Name = "t" });
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateThreadFromMessage_ParentIsTwoDeep_ReturnsBadRequest()
    {
        var text = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        var scene = await SeedChildChannel(text.Id, ChannelType.Scene, "a scene");
        var nested = await SeedChildChannel(scene.Id, ChannelType.Thread, "already deep");

        var result = await CreateFromMessage(nested.Id, new CreateThreadDto { Name = "t" });
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateThreadFromMessage_ParentIsEncrypted_ReturnsBadRequest()
    {
        await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);

        var encrypted = new Channel
        {
            Id = "chan_encrypted", Name = "secret", Type = ChannelType.Text, GuildId = GuildId,
            EncryptionState = EncryptionState.Encrypted,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        _context.Channels.Add(encrypted);
        await _context.SaveChangesAsync();

        var result = await CreateFromMessage(encrypted.Id, new CreateThreadDto { Name = "t" });
        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateThreadFromMessage_LacksCreateThreads_ReturnsForbid()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.ViewChannel);

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateThreadFromMessage_MessageAlreadyHasThreadLocally_ReturnsConflict()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        _context.Channels.Add(Channel.Create(new CreateChannelParams
        {
            Name = "existing", Description = "", Type = ChannelType.Thread, GuildId = GuildId,
            ParentChannelId = parent.Id, CreatedByUserId = UserId, StarterMessageId = MessageId,
        }));
        await _context.SaveChangesAsync();

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });

        Assert.That(result, Is.InstanceOf<Conflict<Guild.Application.Dtos.Response.ThreadConflictDto>>());
        // Never reached Messaging: the local row already answers the question.
        Assert.That(_bus.Invoked, Is.Empty);
    }

    [Test]
    public async Task CreateThreadFromMessage_ThreadOnThatMessageElsewhere_DoesNotLeakItAsAConflict()
    {
        // The pre-check must not confirm the existence of a thread in a channel the caller was not
        // asking about; Messaging decides, and it answers 404 for a foreign message.
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        var other = Channel.Create(new CreateChannelParams { Name = "other", Description = "", Type = ChannelType.Text, GuildId = GuildId });
        _context.Channels.Add(other);
        _context.Channels.Add(Channel.Create(new CreateChannelParams
        {
            Name = "elsewhere", Description = "", Type = ChannelType.Thread, GuildId = GuildId,
            ParentChannelId = other.Id, CreatedByUserId = UserId, StarterMessageId = MessageId,
        }));
        await _context.SaveChangesAsync();
        AttachReturns(AttachThreadOutcome.WrongChannel);

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task CreateThreadFromMessage_MessagingSaysNotFound_ReturnsNotFoundAndPersistsNothing()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        AttachReturns(AttachThreadOutcome.MessageNotFound);

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NotFound>());
        // AutoApplyTransactions commits whatever is staged when the endpoint returns, so a rejected
        // create is only really rejected if nothing was staged.
        Assert.That(await _context.Channels.CountAsync(c => c.Type == ChannelType.Thread), Is.Zero);
    }

    [Test]
    public async Task CreateThreadFromMessage_MessageInAnotherChannel_ReturnsNotFoundAndPersistsNothing()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        AttachReturns(AttachThreadOutcome.WrongChannel);

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NotFound>());
        Assert.That(await _context.Channels.CountAsync(c => c.Type == ChannelType.Thread), Is.Zero);
    }

    [Test]
    public async Task CreateThreadFromMessage_MessagingSaysAlreadyAttached_ReturnsConflictAndPersistsNothing()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        AttachReturns(AttachThreadOutcome.AlreadyHasThread, "chan_other");

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<Conflict<Guild.Application.Dtos.Response.ThreadConflictDto>>());
        Assert.That(await _context.Channels.CountAsync(c => c.Type == ChannelType.Thread), Is.Zero);
    }

    [Test]
    public async Task CreateThreadFromMessage_Valid_PersistsThreadWithStarterMessage()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        AttachReturns(AttachThreadOutcome.Attached);

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "my-thread" });
        await _context.SaveChangesAsync();

        var ok = result as Ok<Guild.Application.Dtos.Response.ChannelDto>;
        Assert.That(ok, Is.Not.Null);

        var created = await _context.Channels.AsNoTracking().FirstAsync(c => c.Id == ok!.Value!.Id);
        Assert.Multiple(() =>
        {
            Assert.That(created.Type, Is.EqualTo(ChannelType.Thread));
            Assert.That(created.ParentChannelId, Is.EqualTo(parent.Id));
            Assert.That(created.StarterMessageId, Is.EqualTo(MessageId));
            Assert.That(ok!.Value!.StarterMessageId, Is.EqualTo(MessageId));
        });
    }

    [Test]
    public async Task CreateThreadFromMessage_Valid_AttachesTheThreadItIsAboutToPersist()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        AttachReturns(AttachThreadOutcome.Attached);

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });
        var ok = result as Ok<Guild.Application.Dtos.Response.ChannelDto>;

        var attach = _bus.Invoked.OfType<AttachThreadToMessageCommand>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(attach.MessageId, Is.EqualTo(MessageId));
            Assert.That(attach.ChannelId, Is.EqualTo(parent.Id));
            Assert.That(attach.ThreadId, Is.EqualTo(ok!.Value!.Id));
        });
    }

    [Test]
    public async Task CreateThreadFromMessage_Valid_PublishesThreadCreatedForBotsWithStarter()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        AttachReturns(AttachThreadOutcome.Attached);

        await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });

        var evt = _bus.Published.OfType<ThreadCreatedForBots>().Single();
        Assert.That(evt.StarterMessageId, Is.EqualTo(MessageId));
    }

    [Test]
    public async Task CreateThreadFromMessage_WithContent_SendsCreateMessageCommandIntoTheThread()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        AttachReturns(AttachThreadOutcome.Attached);

        var result = await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t", Content = "first reply" });
        var ok = result as Ok<Guild.Application.Dtos.Response.ChannelDto>;

        var create = _bus.Invoked.OfType<CreateMessageCommand>().Single();
        Assert.That(create.ChannelId, Is.EqualTo(ok!.Value!.Id));
    }

    [Test]
    public async Task CreateThreadFromMessage_NoContent_DoesNotSendCreateMessageCommand()
    {
        var parent = await SeedMemberAndParentChannel(Permissions.CreateThreads | Permissions.ViewChannel);
        AttachReturns(AttachThreadOutcome.Attached);

        await CreateFromMessage(parent.Id, new CreateThreadDto { Name = "t" });

        Assert.That(_bus.Invoked.OfType<CreateMessageCommand>(), Is.Empty);
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
