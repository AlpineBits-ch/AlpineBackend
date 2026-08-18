using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Covers ChannelPrivacyService, which makes Channel.IsPrivate mean an @everyone ViewChannel deny.
/// The flag was display-only before: no permission code read it, so a channel marked private stayed
/// readable by the whole guild.
/// </summary>
[TestFixture]
public class ChannelPrivacyServiceTests
{
    private const string GuildId = "gild-1";
    private const string ChannelId = "chan-1";
    private const string EveryoneRoleId = "role-everyone";
    private const string MemberUserId = "user-1";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private ChannelPrivacyService _privacy = null!;
    private GuildPermissionService _permissions = null!;

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _privacy = new ChannelPrivacyService(_context);
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>A guild with @everyone holding ViewChannel, one channel, and one ordinary member.</summary>
    private async Task<Channel> SeedAsync()
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "g", OwnerId = "user-owner", CreatedAt = Now, UpdatedAt = Now,
        });

        var channel = new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "secret", Description = "d",
            Type = ChannelType.Text, CreatedAt = Now, UpdatedAt = Now,
        };
        _context.Channels.Add(channel);

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.ReadMessageHistory | Permissions.SendMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "memb-1", GuildId = GuildId, UserId = MemberUserId, JoinedAt = DateTime.UtcNow,
            SearchValue = MemberUserId.ToUpperInvariant(), CreatedAt = Now, UpdatedAt = Now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rome-1", RoleId = EveryoneRoleId, MemberId = "memb-1", CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
        return channel;
    }

    private Task<ChannelPermission?> EveryoneOverwriteAsync() =>
        _context.ChannelPermissions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.ChannelId == ChannelId && p.RoleId == EveryoneRoleId && p.MemberId == null);

    // ══════════════════════════════════════════════════════════════════════ Marking private
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MarkingPrivate_WritesTheEveryoneViewChannelDeny()
    {
        var channel = await SeedAsync();

        var evt = await _privacy.ApplyAsync(channel, isPrivate: true);
        await _context.SaveChangesAsync();

        var overwrite = await EveryoneOverwriteAsync();

        Assert.Multiple(() =>
        {
            Assert.That(channel.IsPrivate, Is.True);
            Assert.That(overwrite, Is.Not.Null);
            Assert.That(overwrite!.DenyPermissions.HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(evt, Is.Not.Null, "the caches resolved against the old overwrite have to be dropped");
            Assert.That(evt!.RoleId, Is.EqualTo(EveryoneRoleId));
        });
    }

    /// <summary>The point of the whole change: the flag now actually denies reads.</summary>
    [Test]
    public async Task MarkingPrivate_DeniesAnOrdinaryMemberViewChannel()
    {
        var channel = await SeedAsync();

        Assert.That(await _permissions.CanUserPerformActionAsync(MemberUserId, ChannelId, Permissions.ViewChannel),
            Is.True, "precondition: @everyone grants ViewChannel");

        await _privacy.ApplyAsync(channel, isPrivate: true);
        await _context.SaveChangesAsync();
        await _permissions.InvalidateUserPermissionsCacheAsync(GuildId, MemberUserId);

        Assert.Multiple(async () =>
        {
            Assert.That(await _permissions.CanUserPerformActionAsync(MemberUserId, ChannelId, Permissions.ViewChannel), Is.False);
            Assert.That(await _permissions.CanUserPerformActionAsync(MemberUserId, ChannelId, Permissions.SendMessages), Is.False);
        });
    }

    /// <summary>Resolution applies allow after deny, so an @everyone allow of the same bit would
    /// undo the deny outright.</summary>
    [Test]
    public async Task MarkingPrivate_ClearsAConflictingEveryoneAllowOfViewChannel()
    {
        var channel = await SeedAsync();
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "chpr-1", ChannelId = ChannelId, RoleId = EveryoneRoleId, MemberId = null,
            AllowPermissions = Permissions.ViewChannel | Permissions.AddReactions,
            DenyPermissions = Permissions.None,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await _privacy.ApplyAsync(channel, isPrivate: true);
        await _context.SaveChangesAsync();

        var overwrite = await EveryoneOverwriteAsync();

        Assert.Multiple(() =>
        {
            Assert.That(overwrite!.AllowPermissions.HasFlag(Permissions.ViewChannel), Is.False);
            Assert.That(overwrite.AllowPermissions.HasFlag(Permissions.AddReactions), Is.True,
                "unrelated bits on the row are not this service's business");
            Assert.That(overwrite.DenyPermissions.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Unmarking
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UnmarkingPrivate_RemovesTheDenyAndRestoresAccess()
    {
        var channel = await SeedAsync();
        await _privacy.ApplyAsync(channel, isPrivate: true);
        await _context.SaveChangesAsync();

        await _privacy.ApplyAsync(channel, isPrivate: false);
        await _context.SaveChangesAsync();
        await _permissions.InvalidateUserPermissionsCacheAsync(GuildId, MemberUserId);

        Assert.Multiple(async () =>
        {
            Assert.That(channel.IsPrivate, Is.False);
            Assert.That(await EveryoneOverwriteAsync(), Is.Null, "an all-None row is only overhead");
            Assert.That(await _permissions.CanUserPerformActionAsync(MemberUserId, ChannelId, Permissions.ViewChannel), Is.True);
        });
    }

    [Test]
    public async Task UnmarkingPrivate_KeepsUnrelatedBitsOnTheSameOverwrite()
    {
        var channel = await SeedAsync();
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "chpr-1", ChannelId = ChannelId, RoleId = EveryoneRoleId, MemberId = null,
            AllowPermissions = Permissions.None,
            DenyPermissions = Permissions.ViewChannel | Permissions.SendMessages,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await _privacy.ApplyAsync(channel, isPrivate: false);
        await _context.SaveChangesAsync();

        var overwrite = await EveryoneOverwriteAsync();

        Assert.Multiple(() =>
        {
            Assert.That(overwrite, Is.Not.Null);
            Assert.That(overwrite!.DenyPermissions.HasFlag(Permissions.ViewChannel), Is.False);
            Assert.That(overwrite.DenyPermissions.HasFlag(Permissions.SendMessages), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Nulls and edges
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>A PATCH that omits the field must not publish a private channel to the guild.</summary>
    [Test]
    public async Task ApplyingNull_LeavesBothTheFlagAndTheOverwriteAlone()
    {
        var channel = await SeedAsync();
        await _privacy.ApplyAsync(channel, isPrivate: true);
        await _context.SaveChangesAsync();

        var evt = await _privacy.ApplyAsync(channel, isPrivate: null);
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(evt, Is.Null);
            Assert.That(channel.IsPrivate, Is.True);
            Assert.That((await EveryoneOverwriteAsync())!.DenyPermissions.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    [Test]
    public async Task ReapplyingTheSameState_ReportsNoChange()
    {
        var channel = await SeedAsync();
        await _privacy.ApplyAsync(channel, isPrivate: true);
        await _context.SaveChangesAsync();

        Assert.That(await _privacy.ApplyAsync(channel, isPrivate: true), Is.Null);
    }

    /// <summary>Nothing to deny against means the flag would be a claim the permission model
    /// cannot back.</summary>
    [Test]
    public async Task WithNoEveryoneRole_RefusesToRecordTheFlag()
    {
        var channel = await SeedAsync();
        _context.Roles.Remove(_context.Roles.Single(r => r.Id == EveryoneRoleId));
        await _context.SaveChangesAsync();

        var evt = await _privacy.ApplyAsync(channel, isPrivate: true);

        Assert.Multiple(() =>
        {
            Assert.That(evt, Is.Null);
            Assert.That(channel.IsPrivate, Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════ Flag follows overwrite
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task EditingTheEveryoneOverwriteDirectly_UpdatesTheFlag()
    {
        var channel = await SeedAsync();
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "chpr-1", ChannelId = ChannelId, RoleId = EveryoneRoleId, MemberId = null,
            AllowPermissions = Permissions.None,
            DenyPermissions = Permissions.ViewChannel,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await _privacy.SyncFlagFromOverwriteAsync(ChannelId, EveryoneRoleId);
        await _context.SaveChangesAsync();

        Assert.That(_context.Channels.Single(c => c.Id == ChannelId).IsPrivate, Is.True);
    }

    [Test]
    public async Task EditingANonEveryoneOverwrite_LeavesTheFlagAlone()
    {
        var channel = await SeedAsync();
        _context.Roles.Add(new Role
        {
            Id = "role-mods", GuildId = GuildId, Name = "mods", Type = RoleType.None,
            Permissions = Permissions.ViewChannel, CreatedAt = Now, UpdatedAt = Now,
        });
        _context.ChannelPermissions.Add(new ChannelPermission
        {
            Id = "chpr-1", ChannelId = ChannelId, RoleId = "role-mods", MemberId = null,
            AllowPermissions = Permissions.None,
            DenyPermissions = Permissions.ViewChannel,
            CreatedAt = Now, UpdatedAt = Now,
        });
        await _context.SaveChangesAsync();

        await _privacy.SyncFlagFromOverwriteAsync(ChannelId, "role-mods");
        await _context.SaveChangesAsync();

        Assert.That(_context.Channels.Single(c => c.Id == ChannelId).IsPrivate, Is.False,
            "one role losing access is not the channel becoming private");
    }
}
