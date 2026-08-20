using System.Collections;
using Guild.Application.Dtos.Request;
using Guild.Application.Endpoints;
using Guild.Application.Endpoints.Channel;
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
/// Covers ChannelFollowEndpoint: creating/listing/removing "follows" from an Announcement source
/// channel into a target channel (possibly in a different guild), including the
/// source-must-be-Announcement validation and the dual-sided (source or target manager) removal
/// permission.
/// </summary>
[TestFixture]
public class ChannelFollowEndpointTests
{
    private const string SourceGuildId = "guild-source";
    private const string TargetGuildId = "guild-target";
    private const string SourceOwnerId = "owner-source";
    private const string TargetOwnerId = "owner-target";
    private const string UserId = "user-1";
    private const string SourceChannelId = "chan-source";
    private const string TargetChannelId = "chan-target";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _permissionService = null!;
    private AuditLogService _auditLog = null!;
    private ChannelFollowEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _permissionService = PermissionTestFactory.Create(_cache, _context);
        _auditLog = new AuditLogService(_context);
        _endpoint = new ChannelFollowEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static Guild.Domain.Aggregates.Guild MakeGuild(string id, string ownerId) => new()
    {
        Id = id, OwnerId = ownerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Channel MakeChannel(string id, string guildId, ChannelType type) => new()
    {
        Id = id, GuildId = guildId, Name = "chan", Description = "d", Type = type,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Seeds source (Announcement) + target (Text) channels in separate guilds, and gives
    /// <paramref name="userId"/> ViewChannel on the source guild plus (optionally) ManageChannel on
    /// the target guild - the two checks CreateFollow performs.</summary>
    private async Task SeedChannels(string userId, bool viewSource = true, bool manageTarget = true)
    {
        _context.Guilds.Add(MakeGuild(SourceGuildId, SourceOwnerId));
        _context.Guilds.Add(MakeGuild(TargetGuildId, TargetOwnerId));
        _context.Channels.Add(MakeChannel(SourceChannelId, SourceGuildId, ChannelType.Announcement));
        _context.Channels.Add(MakeChannel(TargetChannelId, TargetGuildId, ChannelType.Text));

        if (viewSource)
        {
            var role = new Role { Id = "role-src", GuildId = SourceGuildId, Name = "viewer", Permissions = Permissions.ViewChannel, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var member = new GuildMember { Id = "member-src", GuildId = SourceGuildId, UserId = userId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{userId}#{SourceGuildId}" };
            _context.Roles.Add(role);
            _context.GuildMembers.Add(member);
            _context.RoleMembers.Add(new RoleMember { Id = "rm-src", RoleId = "role-src", MemberId = "member-src", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        }

        if (manageTarget)
        {
            var role = new Role { Id = "role-tgt", GuildId = TargetGuildId, Name = "manager", Permissions = Permissions.ManageChannel, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var member = new GuildMember { Id = "member-tgt", GuildId = TargetGuildId, UserId = userId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{userId}#{TargetGuildId}" };
            _context.Roles.Add(role);
            _context.GuildMembers.Add(member);
            _context.RoleMembers.Add(new RoleMember { Id = "rm-tgt", RoleId = "role-tgt", MemberId = "member-tgt", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        }

        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════ CreateFollow
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateFollow_Unauthenticated_ReturnsUnauthorized()
    {
        var result = await _endpoint.CreateFollow(SourceChannelId, new CreateChannelFollowDto { TargetChannelId = TargetChannelId },
            _permissionService, _context, _auditLog, TestPrincipal.CreateAnonymous());

        Assert.That(result, Is.InstanceOf<UnauthorizedHttpResult>());
    }

    [Test]
    public async Task CreateFollow_SourceChannelDoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.CreateFollow("nonexistent", new CreateChannelFollowDto { TargetChannelId = TargetChannelId },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task CreateFollow_SourceNotAnnouncementType_ReturnsBadRequest()
    {
        _context.Guilds.Add(MakeGuild(SourceGuildId, SourceOwnerId));
        _context.Channels.Add(MakeChannel(SourceChannelId, SourceGuildId, ChannelType.Text));
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateFollow(SourceChannelId, new CreateChannelFollowDto { TargetChannelId = TargetChannelId },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task CreateFollow_TargetChannelDoesNotExist_ReturnsNotFound()
    {
        _context.Guilds.Add(MakeGuild(SourceGuildId, SourceOwnerId));
        _context.Channels.Add(MakeChannel(SourceChannelId, SourceGuildId, ChannelType.Announcement));
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateFollow(SourceChannelId, new CreateChannelFollowDto { TargetChannelId = "nonexistent" },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NotFound<string>>());
    }

    [Test]
    public async Task CreateFollow_CannotViewSource_ReturnsForbid()
    {
        await SeedChannels(UserId, viewSource: false, manageTarget: true);

        var result = await _endpoint.CreateFollow(SourceChannelId, new CreateChannelFollowDto { TargetChannelId = TargetChannelId },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateFollow_CannotManageTarget_ReturnsForbid()
    {
        await SeedChannels(UserId, viewSource: true, manageTarget: false);

        var result = await _endpoint.CreateFollow(SourceChannelId, new CreateChannelFollowDto { TargetChannelId = TargetChannelId },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task CreateFollow_AlreadyFollowing_ReturnsConflict()
    {
        await SeedChannels(UserId);
        _context.Set<GuildChannelFollow>().Add(GuildChannelFollow.Create(new CreateGuildChannelFollowParams
        {
            SourceChannelId = SourceChannelId, SourceGuildId = SourceGuildId,
            TargetChannelId = TargetChannelId, TargetGuildId = TargetGuildId,
            CreatedByUserId = UserId,
        }));
        await _context.SaveChangesAsync();

        var result = await _endpoint.CreateFollow(SourceChannelId, new CreateChannelFollowDto { TargetChannelId = TargetChannelId },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<Conflict<string>>());
    }

    [Test]
    public async Task CreateFollow_Valid_PersistsFollowAndWritesAuditLog()
    {
        await SeedChannels(UserId);

        var result = await _endpoint.CreateFollow(SourceChannelId, new CreateChannelFollowDto { TargetChannelId = TargetChannelId },
            _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<IValueHttpResult>());
        var follow = _context.Set<GuildChannelFollow>().FirstOrDefault(f => f.SourceChannelId == SourceChannelId && f.TargetChannelId == TargetChannelId);
        Assert.That(follow, Is.Not.Null);

        var entries = _context.Set<GuildAuditLogEntry>().Where(e => e.GuildId == TargetGuildId).ToList();
        Assert.That(entries, Has.Count.EqualTo(1));
        Assert.That(entries[0].ActionType, Is.EqualTo(AuditActionType.ChannelFollowCreated));
    }

    // ══════════════════════════════════════════════════════════════════════ ListFollowers
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ListFollowers_SourceChannelDoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.ListFollowers("nonexistent", _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task ListFollowers_LacksManageChannel_ReturnsForbid()
    {
        _context.Guilds.Add(MakeGuild(SourceGuildId, SourceOwnerId));
        _context.Channels.Add(MakeChannel(SourceChannelId, SourceGuildId, ChannelType.Announcement));
        await _context.SaveChangesAsync();

        var result = await _endpoint.ListFollowers(SourceChannelId, _permissionService, _context, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task ListFollowers_Valid_ReturnsFollowersForThatSourceOnly()
    {
        await SeedChannels(UserId);
        _context.Set<GuildChannelFollow>().Add(GuildChannelFollow.Create(new CreateGuildChannelFollowParams
        {
            SourceChannelId = SourceChannelId, SourceGuildId = SourceGuildId,
            TargetChannelId = TargetChannelId, TargetGuildId = TargetGuildId, CreatedByUserId = UserId,
        }));
        // A follow from a different source channel must not show up.
        _context.Channels.Add(MakeChannel("other-source", SourceGuildId, ChannelType.Announcement));
        _context.Set<GuildChannelFollow>().Add(GuildChannelFollow.Create(new CreateGuildChannelFollowParams
        {
            SourceChannelId = "other-source", SourceGuildId = SourceGuildId,
            TargetChannelId = TargetChannelId, TargetGuildId = TargetGuildId, CreatedByUserId = UserId,
        }));
        await _context.SaveChangesAsync();

        // Need ManageChannel on the SOURCE guild for ListFollowers (checked against sourceChannel.GuildId).
        var role = new Role { Id = "role-manage-src", GuildId = SourceGuildId, Name = "m", Permissions = Permissions.ManageChannel, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var member = _context.GuildMembers.First(m => m.GuildId == SourceGuildId && m.UserId == UserId);
        _context.Roles.Add(role);
        _context.RoleMembers.Add(new RoleMember { Id = "rm-manage-src", RoleId = "role-manage-src", MemberId = member.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        var result = await _endpoint.ListFollowers(SourceChannelId, _permissionService, _context, TestPrincipal.Create(UserId));

        var value = ((IValueHttpResult)result).Value;
        var list = ((IEnumerable)value!).Cast<object>().ToList();
        Assert.That(list, Has.Count.EqualTo(1));
    }

    // ══════════════════════════════════════════════════════════════════════ RemoveFollow
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RemoveFollow_DoesNotExist_ReturnsNotFound()
    {
        var result = await _endpoint.RemoveFollow(SourceChannelId, "nonexistent", _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));
        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    [Test]
    public async Task RemoveFollow_NeitherSideCanManage_ReturnsForbid()
    {
        await SeedChannels(UserId, viewSource: false, manageTarget: false);
        var follow = GuildChannelFollow.Create(new CreateGuildChannelFollowParams
        {
            SourceChannelId = SourceChannelId, SourceGuildId = SourceGuildId,
            TargetChannelId = TargetChannelId, TargetGuildId = TargetGuildId, CreatedByUserId = "someone-else",
        });
        _context.Set<GuildChannelFollow>().Add(follow);
        await _context.SaveChangesAsync();

        var result = await _endpoint.RemoveFollow(SourceChannelId, follow.Id, _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task RemoveFollow_TargetManagerCanRemove()
    {
        await SeedChannels(UserId, viewSource: false, manageTarget: true);
        var follow = GuildChannelFollow.Create(new CreateGuildChannelFollowParams
        {
            SourceChannelId = SourceChannelId, SourceGuildId = SourceGuildId,
            TargetChannelId = TargetChannelId, TargetGuildId = TargetGuildId, CreatedByUserId = "someone-else",
        });
        _context.Set<GuildChannelFollow>().Add(follow);
        await _context.SaveChangesAsync();

        var result = await _endpoint.RemoveFollow(SourceChannelId, follow.Id, _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));
        await _context.SaveChangesAsync();

        Assert.That(result, Is.InstanceOf<NoContent>());
        Assert.That(_context.Set<GuildChannelFollow>().Find(follow.Id), Is.Null);
    }

    [Test]
    public async Task RemoveFollow_SourceManagerCanRemove_RevokingSomeoneFollowingThem()
    {
        // ManageChannel on the SOURCE guild (not target) should also be sufficient - "revoke
        // someone following you".
        _context.Guilds.Add(MakeGuild(SourceGuildId, SourceOwnerId));
        _context.Guilds.Add(MakeGuild(TargetGuildId, TargetOwnerId));
        _context.Channels.Add(MakeChannel(SourceChannelId, SourceGuildId, ChannelType.Announcement));
        _context.Channels.Add(MakeChannel(TargetChannelId, TargetGuildId, ChannelType.Text));
        var role = new Role { Id = "role-src-mgr", GuildId = SourceGuildId, Name = "m", Permissions = Permissions.ManageChannel, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var member = new GuildMember { Id = "member-src-mgr", GuildId = SourceGuildId, UserId = UserId, JoinedAt = DateTime.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, SearchValue = $"{UserId}#{SourceGuildId}" };
        _context.Roles.Add(role);
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(new RoleMember { Id = "rm-src-mgr", RoleId = "role-src-mgr", MemberId = "member-src-mgr", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var follow = GuildChannelFollow.Create(new CreateGuildChannelFollowParams
        {
            SourceChannelId = SourceChannelId, SourceGuildId = SourceGuildId,
            TargetChannelId = TargetChannelId, TargetGuildId = TargetGuildId, CreatedByUserId = "someone-else",
        });
        _context.Set<GuildChannelFollow>().Add(follow);
        await _context.SaveChangesAsync();

        var result = await _endpoint.RemoveFollow(SourceChannelId, follow.Id, _permissionService, _context, _auditLog, TestPrincipal.Create(UserId));

        Assert.That(result, Is.InstanceOf<NoContent>());
    }
}
