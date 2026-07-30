using System.Text.Json;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Covers GuildPermissionService end-to-end using an EF Core InMemory database
/// and a simple fake distributed cache.
///
/// Each test gets its own database (Guid-named) to prevent state bleed.
///
/// Key implementation notes exercised by these tests:
///   - Owner check short-circuits all permission logic.
///   - Base permissions come from Role.Permissions (a scalar flags field), OR-combined
///     across all roles the user holds.  ChannelPermission records with only a RoleId
///     (no ChannelId / CategoryId) are ignored — they are not base permissions.
///   - Channel and category overwrites are applied after base (category first,
///     channel second so channel wins).
///   - User-specific overwrites are applied last and always take priority.
///   - ExpandImpliedPermissions adds implied flags (e.g. Stream → Speak → Connect).
///   - Results are cached with a 15-minute TTL; invalidation removes the entry.
///
/// NOTE ON NAVIGATION PROPERTIES
///   The service loads ChannelPermission via AsNoTracking without Include, so
///   the Role navigation property is always null at query time.  The "Everyone
///   role is applied first" ordering therefore relies on the RoleId being in
///   the user's computed role-ID set.  Tests use that path and deliberately
///   avoid testing an ordering that can't be observed without loaded nav props.
/// </summary>
[TestFixture]
public class GuildPermissionServiceTests
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ChannelId = "channel-1";
    private const string RoleId = "role-1";
    private const string CategoryId = "category-1";
    private const string MemberId = "member-1";

    // ── Per-test state ────────────────────────────────────────────────────────

    private string _dbName = null!;
    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbName = Guid.NewGuid().ToString();
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(_dbName);
        _service = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Entity factory helpers ────────────────────────────────────────────────

    private static Guild.Domain.Aggregates.Guild MakeGuild(string ownerId = OwnerId, string id = GuildId) => new()
    {
        Id = id, OwnerId = ownerId, Name = "Test Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Channel MakeChannel(
        string id = ChannelId, string guildId = GuildId, string? categoryId = null) => new()
    {
        Id = id, GuildId = guildId, CategoryId = categoryId,
        Name = "test-channel", Description = "desc", Type = ChannelType.Text,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, 
    };

    private static Category MakeCategory(string id = CategoryId, string guildId = GuildId) => new()
    {
        Id = id, GuildId = guildId, Name = "Test Category",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Role MakeRole(
        string id = RoleId, string guildId = GuildId, RoleType type = RoleType.None,
        Permissions permissions = Permissions.None) => new()
    {
        Id = id, GuildId = guildId, Type = type, Name = "test-role",
        Permissions = permissions,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GuildMember MakeGuildMember(
        string id = MemberId, string guildId = GuildId, string userId = UserId,
        Permissions allow = Permissions.None, Permissions deny = Permissions.None) => new()
    {
        Id = id, GuildId = guildId, UserId = userId,
        JoinedAt = DateTime.UtcNow,
        AllowPermissions = allow, DenyPermissions = deny,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        SearchValue = $"{userId}#{guildId}",
    };

    private static RoleMember MakeRoleMember(string id, string roleId, string memberId) => new()
    {
        Id = id, RoleId = roleId, MemberId = memberId,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    // A ChannelPermission with only RoleId (no ChannelId / CategoryId) acts as a
    // role-level base permission; one with ChannelId or CategoryId is an overwrite.
    private static ChannelPermission MakePermission(
        string id,
        string? channelId = null,
        string? categoryId = null,
        string? roleId = null,
        string? memberId = null,
        Permissions allow = Permissions.None,
        Permissions deny = Permissions.None) => new()
    {
        Id = id,
        ChannelId = channelId,
        CategoryId = categoryId,
        RoleId = roleId,
        MemberId = memberId,
        AllowPermissions = allow,
        DenyPermissions = deny,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    // Seeds a guild, one channel, one role (with the supplied permissions), and the user
    // in that role.  Returns when SaveChanges is done.
    private async Task SeedWithRolePermission(Permissions allow)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: allow));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();
    }

    // Computes permissions for UserId in the current context and returns the
    // effective Permissions flags for ChannelId.
    private async Task<Permissions> GetComputedForChannel(string channelId = ChannelId)
    {
        var result = await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        return result.Permissions.First(p => p.ChannelId == channelId).Permissions;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CanUserPerformActionAsync — public entry point
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CanUserPerformAction_ChannelDoesNotExist_ReturnsFalse()
    {
        var result = await _service.CanUserPerformActionAsync(
            UserId, "nonexistent-chan", Permissions.ViewChannel);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanUserPerformAction_GuildOwner_AlwaysReturnsTrue()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        _context.Channels.Add(MakeChannel());
        await _context.SaveChangesAsync();

        // Owner should pass for every defined permission.
        foreach (var perm in Enum.GetValues<Permissions>().Where(p => p != Permissions.None))
        {
            var result = await _service.CanUserPerformActionAsync(UserId, ChannelId, perm);
            Assert.That(result, Is.True, $"Owner must have {perm}");
        }
    }

    [Test]
    public async Task CanUserPerformAction_UserHasExactPermission_ReturnsTrue()
    {
        await SeedWithRolePermission(Permissions.ViewChannel);

        var result = await _service.CanUserPerformActionAsync(
            UserId, ChannelId, Permissions.ViewChannel);

        Assert.That(result, Is.True);
    }



    [Test]
    public async Task Bug_001_Mäxu_Can_Not_Stream()
    {
        var guild = MakeGuild();
        var channel = MakeChannel();
        var member = MakeGuildMember();
        var role = Role.CreateEveryoneRole(guild.Id, member.Id);
        var roleMember = MakeRoleMember("rm-1", role.Id, member.Id);
        _context.Guilds.Add(guild);
        _context.Channels.Add(channel);
        _context.Roles.Add(role);
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(roleMember);
        _context.SaveChanges();
        
        var result = await _service.CanUserPerformActionAsync(
            UserId, ChannelId, Permissions.Stream);
        
        Assert.That(result, Is.True);
        
    }

    [Test]
    public async Task CanUserPerformAction_UserHasEveryoneRole_CanStream_ReturnsTrue()
    {
       

        var everyoneRole = Role.CreateEveryoneRole(GuildId, MemberId);
        await SeedWithRolePermission(everyoneRole.Permissions);

        
        var result = await _service.CanUserPerformActionAsync(
            UserId, ChannelId, Permissions.Speak);
        
        Assert.That(result, Is.True);

    }
    
    
    [Test]
    public async Task CanUserPerformAction_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithRolePermission(Permissions.ViewChannel);   // has ViewChannel, not SendMessages

        var result = await _service.CanUserPerformActionAsync(
            UserId, ChannelId, Permissions.SendMessages);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanUserPerformAction_UserHasNoRoles_ReturnsFalse()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        await _context.SaveChangesAsync();

        var result = await _service.CanUserPerformActionAsync(
            UserId, ChannelId, Permissions.ViewChannel);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanUserPerformAction_MultipleRoles_PermissionsCombinedWithOR()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        // Role A grants ViewChannel, Role B grants SendMessages via their Permissions property.
        _context.Roles.AddRange(
            MakeRole("role-a", permissions: Permissions.ViewChannel),
            MakeRole("role-b", permissions: Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.AddRange(
            MakeRoleMember("rm-a", "role-a", MemberId),
            MakeRoleMember("rm-b", "role-b", MemberId));
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.ViewChannel), Is.True);
            Assert.That(await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.SendMessages), Is.True);
        });
    }

    [Test]
    public async Task CanUserPerformAction_NullUserId_ThrowsArgumentException()
    {
        // Channel must exist so the flow reaches ComputePermissionsForUserAsync,
        // which is where the guard throws.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        await _context.SaveChangesAsync();

        Assert.ThrowsAsync<ArgumentException>(
            () => _service.CanUserPerformActionAsync(null!, ChannelId, Permissions.ViewChannel));
    }

    [Test]
    public async Task CanUserPerformAction_WhitespaceUserId_ThrowsArgumentException()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        await _context.SaveChangesAsync();

        Assert.ThrowsAsync<ArgumentException>(
            () => _service.CanUserPerformActionAsync("   ", ChannelId, Permissions.ViewChannel));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CanUserPerformActionOnGuildAsync — guild-scoped (no channel context)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CanUserPerformActionOnGuild_GuildDoesNotExist_ReturnsFalse()
    {
        var result = await _service.CanUserPerformActionOnGuildAsync(
            UserId, "nonexistent-guild", Permissions.ViewChannel);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanUserPerformActionOnGuild_GuildOwner_AlwaysReturnsTrue()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        await _context.SaveChangesAsync();

        foreach (var perm in Enum.GetValues<Permissions>().Where(p => p != Permissions.None))
        {
            var result = await _service.CanUserPerformActionOnGuildAsync(UserId, GuildId, perm);
            Assert.That(result, Is.True, $"Owner must have {perm}");
        }
    }

    [Test]
    public async Task CanUserPerformActionOnGuild_UserHasRolePermission_ReturnsTrue()
    {
        await SeedWithRolePermission(Permissions.ManageChannel);

        var result = await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ManageChannel);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanUserPerformActionOnGuild_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithRolePermission(Permissions.ViewChannel);

        var result = await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ManageChannel);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanUserPerformActionOnGuild_UserHasNoRoles_ReturnsFalse()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        var result = await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ViewChannel);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanUserPerformActionOnGuild_MultipleRoles_PermissionsCombinedWithOR()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.AddRange(
            MakeRole("role-a", permissions: Permissions.ViewChannel),
            MakeRole("role-b", permissions: Permissions.ManageChannel));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.AddRange(
            MakeRoleMember("rm-a", "role-a", MemberId),
            MakeRoleMember("rm-b", "role-b", MemberId));
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(UserId, GuildId, Permissions.ViewChannel), Is.True);
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(UserId, GuildId, Permissions.ManageChannel), Is.True);
        });
    }

    [Test]
    public async Task CanUserPerformActionOnGuild_ChannelOverwriteDoesNotAffectGuildCheck()
    {
        // A channel-level deny overwrite must not influence the guild-level check,
        // which only evaluates base role permissions.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.ChannelPermissions.Add(
            MakePermission("cp-deny", channelId: ChannelId, roleId: RoleId, deny: Permissions.SendMessages));
        await _context.SaveChangesAsync();

        var result = await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.SendMessages);

        Assert.That(result, Is.True,
            "Channel-level deny must not strip a permission at the guild level");
    }

    [Test]
    public async Task CanUserPerformActionOnGuild_ImpliedPermissionsExpanded()
    {
        // Granting Stream at the role level implies Speak and Connect at guild level too.
        await SeedWithRolePermission(Permissions.Stream);

        Assert.Multiple(async () =>
        {
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(UserId, GuildId, Permissions.Stream), Is.True);
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(UserId, GuildId, Permissions.Speak), Is.True, "Stream → Speak");
            Assert.That(await _service.CanUserPerformActionOnGuildAsync(UserId, GuildId, Permissions.Connect), Is.True, "Stream → Speak → Connect");
        });
    }

    [Test]
    public void CanUserPerformActionOnGuild_NullUserId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.CanUserPerformActionOnGuildAsync(string.Empty, GuildId, Permissions.ViewChannel));
    }

    [Test]
    public void CanUserPerformActionOnGuild_WhitespaceGuildId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.CanUserPerformActionOnGuildAsync(UserId, "  ", Permissions.ViewChannel));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ComputePermissionsForUserAsync — internal method (InternalsVisibleTo)
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void ComputePermissions_NullUserId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.ComputePermissionsForUserAsync(null!, GuildId));
    }

    [Test]
    public void ComputePermissions_EmptyUserId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.ComputePermissionsForUserAsync("", GuildId));
    }

    [Test]
    public void ComputePermissions_NullGuildId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.ComputePermissionsForUserAsync(UserId, null!));
    }

    [Test]
    public void ComputePermissions_WhitespaceGuildId_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(
            () => _service.ComputePermissionsForUserAsync(UserId, "  "));
    }

    [Test]
    public async Task ComputePermissions_Owner_GetsSuperadminOnEveryChannel()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        _context.Channels.AddRange(MakeChannel("chan-a"), MakeChannel("chan-b"), MakeChannel("chan-c"));
        await _context.SaveChangesAsync();

        var result = await _service.ComputePermissionsForUserAsync(UserId, GuildId);

        Assert.That(result.Permissions.Count, Is.EqualTo(3));
        Assert.That(
            result.Permissions.All(p => p.Permissions == Permissions.Superadmin),
            Is.True,
            "Every channel should carry Superadmin for the owner");
    }

    [Test]
    public async Task ComputePermissions_CacheHit_ReturnsPreseededDataWithoutHittingDb()
    {
        // Pre-seed the cache with a known fake result; DB has no matching rows.
        var fakePerms = new GuildPermissionsForUser
        {
            GuildId = GuildId, UserId = UserId,
            Permissions =
            [
                new GuildChannelPermission
                {
                    UserId = UserId, GuildId = GuildId, ChannelId = ChannelId,
                    Permissions = Permissions.ManageChannel
                }
            ]
        };
        _cache.SetEntry(
            GuildPermissionsForUser.GetCacheKey(GuildId, UserId),
            JsonSerializer.Serialize(fakePerms));

        var result = await _service.ComputePermissionsForUserAsync(UserId, GuildId);

        Assert.That(result.Permissions.Count, Is.EqualTo(1));
        Assert.That(result.Permissions.First().Permissions, Is.EqualTo(Permissions.ManageChannel));
    }

    [Test]
    public async Task ComputePermissions_PopulatesCache_AfterFirstCompute()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        await _context.SaveChangesAsync();

        await _service.ComputePermissionsForUserAsync(UserId, GuildId);

        var key = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        Assert.That(_cache.HasEntry(key), Is.True, "Result must be written to cache");
    }

    // ── Role-based base permissions ───────────────────────────────────────────

    [Test]
    public async Task ComputePermissions_BaseRolePermissions_CombinedViaOR()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.AddRange(
            MakeRole("r-1", permissions: Permissions.ViewChannel),
            MakeRole("r-2", permissions: Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.AddRange(
            MakeRoleMember("rm-1", "r-1", MemberId),
            MakeRoleMember("rm-2", "r-2", MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
        });
    }

    // ── Channel-level overwrites ──────────────────────────────────────────────

    [Test]
    public async Task ComputePermissions_ChannelDenyOverwrite_RemovesPermission()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        // Role has ViewChannel + SendMessages globally; channel overwrite strips SendMessages.
        _context.Roles.Add(MakeRole(permissions: Permissions.ViewChannel | Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.ChannelPermissions.Add(
            MakePermission("cp-ow", channelId: ChannelId, roleId: RoleId,
                deny: Permissions.SendMessages));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "ViewChannel retained");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False, "SendMessages denied by channel overwrite");
        });
    }

    [Test]
    public async Task ComputePermissions_ChannelAllowOverwrite_AddsPermission()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        // No base permissions; channel overwrite grants ViewChannel.
        _context.ChannelPermissions.Add(
            MakePermission("cp-ow", channelId: ChannelId, roleId: RoleId,
                allow: Permissions.ViewChannel));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    // ── Category-level overwrites ─────────────────────────────────────────────

    [Test]
    public async Task ComputePermissions_CategoryDenyOverwrite_RemovesPermission()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Categories.Add(MakeCategory());
        _context.Channels.Add(MakeChannel(categoryId: CategoryId));
        // Role has SendMessages globally; category overwrite denies it for this category.
        _context.Roles.Add(MakeRole(permissions: Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.ChannelPermissions.Add(
            MakePermission("cp-cat", categoryId: CategoryId, roleId: RoleId,
                deny: Permissions.SendMessages));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False);
    }

    [Test]
    public async Task ComputePermissions_CategoryAllowOverwrite_AddsPermission()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Categories.Add(MakeCategory());
        _context.Channels.Add(MakeChannel(categoryId: CategoryId));
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        // No base permissions; category overwrite grants ViewChannel.
        _context.ChannelPermissions.Add(
            MakePermission("cp-cat", categoryId: CategoryId, roleId: RoleId,
                allow: Permissions.ViewChannel));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task ComputePermissions_CategoryDenyOverwrite_DoesNotAffectChannelWithoutCategory()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Categories.Add(MakeCategory());
        // channel-1: no category  |  channel-2: inside the category
        _context.Channels.AddRange(
            MakeChannel(ChannelId),
            MakeChannel("channel-2", categoryId: CategoryId));
        // Role grants SendMessages globally; category overwrite must only remove it from channel-2.
        _context.Roles.Add(MakeRole(permissions: Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.ChannelPermissions.Add(
            MakePermission("cp-cat", categoryId: CategoryId, roleId: RoleId,
                deny: Permissions.SendMessages));
        await _context.SaveChangesAsync();

        var result = await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        var standalonePerms = result.Permissions.First(p => p.ChannelId == ChannelId).Permissions;
        var categorisedPerms = result.Permissions.First(p => p.ChannelId == "channel-2").Permissions;

        Assert.Multiple(() =>
        {
            Assert.That(standalonePerms.HasFlag(Permissions.SendMessages), Is.True,
                "Channel without the category should keep its base permission");
            Assert.That(categorisedPerms.HasFlag(Permissions.SendMessages), Is.False,
                "Channel inside the category should have the permission removed by the category deny");
        });
    }

    // ── Overwrite precedence ──────────────────────────────────────────────────

    [Test]
    public async Task ComputePermissions_ChannelOverwrite_TakesPrecedenceOverCategory()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Categories.Add(MakeCategory());
        _context.Channels.Add(MakeChannel(categoryId: CategoryId));
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        // Category grants SendMessages; channel-specific overwrite denies it.
        _context.ChannelPermissions.AddRange(
            MakePermission("cp-cat", categoryId: CategoryId, roleId: RoleId,
                allow: Permissions.SendMessages),
            MakePermission("cp-chan", channelId: ChannelId, roleId: RoleId,
                deny: Permissions.SendMessages));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False,
            "Channel overwrite must take precedence over category overwrite");
    }

    [Test]
    public async Task ComputePermissions_UserSpecificOverwrite_TakesPrecedenceOverRole()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole());
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        // Role denies ViewChannel; user-specific overwrite grants it.
        _context.ChannelPermissions.AddRange(
            MakePermission("cp-role", channelId: ChannelId, roleId: RoleId,
                deny: Permissions.ViewChannel),
            MakePermission("cp-user", channelId: ChannelId, memberId: MemberId,
                allow: Permissions.ViewChannel));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True,
            "User-specific overwrite must override role overwrite");
    }

    [Test]
    public async Task ComputePermissions_UserSpecificDenyOverwrite_RemovesPermissionGrantedByRole()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        // Role has ViewChannel + SendMessages globally; user-specific overwrite strips SendMessages.
        _context.Roles.Add(MakeRole(permissions: Permissions.ViewChannel | Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.ChannelPermissions.Add(
            MakePermission("cp-user", channelId: ChannelId, memberId: MemberId,
                deny: Permissions.SendMessages));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "ViewChannel retained");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False, "SendMessages stripped by user overwrite");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ExpandImpliedPermissions Each test grants exactly one "root" permission via a base role and
    // then asserts the permissions that must be implied by it.
    // ══════════════════════════════════════════════════════════════════════════

    // Computes permissions for a single base role flag and returns the effective
    // Permissions value for ChannelId.  Creates fresh DB data in the current context.
    private async Task<Permissions> ComputeImplied(Permissions root)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: root));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-implied", RoleId, MemberId));
        await _context.SaveChangesAsync();
        return await GetComputedForChannel();
    }

    [Test]
    public async Task Implied_EditAnyMessage_GrantsEditOwnMessages()
    {
        var perms = await ComputeImplied(Permissions.EditAnyMessage);
        Assert.That(perms.HasFlag(Permissions.EditOwnMessages), Is.True);
    }

    [Test]
    public async Task Implied_DeleteAnyMessage_GrantsDeleteOwnMessages()
    {
        var perms = await ComputeImplied(Permissions.DeleteAnyMessage);
        Assert.That(perms.HasFlag(Permissions.DeleteOwnMessages), Is.True);
    }

    [Test]
    public async Task Implied_ManageAnyThread_GrantsManageOwnThreads()
    {
        var perms = await ComputeImplied(Permissions.ManageAnyThread);
        Assert.That(perms.HasFlag(Permissions.ManageOwnThreads), Is.True);
    }

    [Test]
    public async Task Implied_Stream_GrantsSpeakAndConnect()
    {
        var perms = await ComputeImplied(Permissions.Stream);
        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.Speak), Is.True, "Stream → Speak");
            Assert.That(perms.HasFlag(Permissions.Connect), Is.True, "Stream → Speak → Connect");
        });
    }

    [Test]
    public async Task Implied_Speak_GrantsConnect()
    {
        var perms = await ComputeImplied(Permissions.Speak);
        Assert.That(perms.HasFlag(Permissions.Connect), Is.True);
    }

    [Test]
    public async Task Implied_MuteMembers_GrantsConnect()
    {
        var perms = await ComputeImplied(Permissions.MuteMembers);
        Assert.That(perms.HasFlag(Permissions.Connect), Is.True);
    }

    [Test]
    public async Task Implied_DeafenMembers_GrantsConnect()
    {
        var perms = await ComputeImplied(Permissions.DeafenMembers);
        Assert.That(perms.HasFlag(Permissions.Connect), Is.True);
    }

    [Test]
    public async Task Implied_MoveMembers_GrantsConnect()
    {
        var perms = await ComputeImplied(Permissions.MoveMembers);
        Assert.That(perms.HasFlag(Permissions.Connect), Is.True);
    }

    [Test]
    public async Task Implied_Connect_GrantsViewChannel()
    {
        var perms = await ComputeImplied(Permissions.Connect);
        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Implied_PinMessages_GrantsSendMessagesAndViewChannel()
    {
        var perms = await ComputeImplied(Permissions.PinMessages);
        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True, "PinMessages → SendMessages");
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "PinMessages → ViewChannel");
        });
    }

    [Test]
    public async Task Implied_AttachFiles_GrantsSendMessagesAndViewChannel()
    {
        var perms = await ComputeImplied(Permissions.AttachFiles);
        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    [Test]
    public async Task Implied_EmbedLinks_GrantsSendMessagesAndViewChannel()
    {
        var perms = await ComputeImplied(Permissions.EmbedLinks);
        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    [Test]
    public async Task Implied_AddReactions_GrantsSendMessagesAndViewChannel()
    {
        var perms = await ComputeImplied(Permissions.AddReactions);
        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    [Test]
    public async Task Implied_CreateThreads_GrantsSendMessagesAndViewChannel()
    {
        var perms = await ComputeImplied(Permissions.CreateThreads);
        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    [Test]
    public async Task Implied_SendMessages_GrantsViewChannel()
    {
        var perms = await ComputeImplied(Permissions.SendMessages);
        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Implied_SendMessagesInThreads_GrantsViewChannel()
    {
        var perms = await ComputeImplied(Permissions.SendMessagesInThreads);
        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Implied_EditOwnMessages_GrantsViewChannel()
    {
        var perms = await ComputeImplied(Permissions.EditOwnMessages);
        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Implied_DeleteOwnMessages_GrantsViewChannel()
    {
        var perms = await ComputeImplied(Permissions.DeleteOwnMessages);
        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Implied_ManageOwnThreads_GrantsViewChannel()
    {
        var perms = await ComputeImplied(Permissions.ManageOwnThreads);
        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Implied_ManageAnyThread_GrantsViewChannel()
    {
        var perms = await ComputeImplied(Permissions.ManageAnyThread);
        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Implied_ManagePermissions_GrantsViewChannel()
    {
        var perms = await ComputeImplied(Permissions.ManagePermissions);
        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Implied_ManageChannel_GrantsManagePermissionsAndViewChannel()
    {
        var perms = await ComputeImplied(Permissions.ManageChannel);
        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ManagePermissions), Is.True, "ManageChannel → ManagePermissions");
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "ManageChannel → ViewChannel");
        });
    }

    [Test]
    public async Task Implied_Superadmin_GrantsEveryDefinedPermission()
    {
        var perms = await ComputeImplied(Permissions.Superadmin);

        foreach (var flag in Enum.GetValues<Permissions>().Where(p => p != Permissions.None))
        {
            Assert.That(perms.HasFlag(flag), Is.True, $"Superadmin must imply {flag}");
        }
    }

    [Test]
    public async Task Implied_NoRootPermission_DoesNotGrantViewChannel()
    {
        // Sanity check: a completely empty base must not accidentally imply ViewChannel.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();
        Assert.That(perms, Is.EqualTo(Permissions.None));
    }

    // ── Role.Permissions is the sole source of base permissions ──────────────

    [Test]
    public async Task ComputePermissions_ChannelPermissionWithOnlyRoleId_IsIgnored()
    {
        // A ChannelPermission that has a RoleId but neither a ChannelId nor a CategoryId was the
        // old way to express base role permissions.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        // A ChannelPermission with only a RoleId — this must not grant anything.
        _context.ChannelPermissions.Add(
            MakePermission("cp-legacy", roleId: RoleId, allow: Permissions.ViewChannel));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms, Is.EqualTo(Permissions.None),
            "ChannelPermission records without a ChannelId or CategoryId must not contribute to base permissions");
    }

    [Test]
    public async Task CanUserPerformActionOnGuild_ChannelPermissionWithOnlyRoleId_IsIgnored()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.ChannelPermissions.Add(
            MakePermission("cp-legacy", roleId: RoleId, allow: Permissions.ManageChannel));
        await _context.SaveChangesAsync();

        var result = await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ManageChannel);

        Assert.That(result, Is.False,
            "ChannelPermission records without a ChannelId or CategoryId must not contribute to guild-level base permissions");
    }

    // ══════════════════════════════════════════════════════════════════════════ Cache invalidation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task InvalidateCache_RemovesExistingCacheEntry()
    {
        var key = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        _cache.SetEntry(key, "stale-data");

        await _service.InvalidateUserPermissionsCacheAsync(GuildId, UserId);

        Assert.That(_cache.HasEntry(key), Is.False);
    }

    [Test]
    public async Task InvalidateCache_DoesNotThrow_WhenEntryAbsent()
    {
        // Should be a no-op when there is nothing to remove.
        Assert.DoesNotThrowAsync(
            () => _service.InvalidateUserPermissionsCacheAsync(GuildId, UserId));
    }

    [Test]
    public async Task InvalidateCache_SubsequentCompute_RepopulatesCache()
    {
        await SeedWithRolePermission(Permissions.ViewChannel);

        // First compute → populates cache.
        await _service.ComputePermissionsForUserAsync(UserId, GuildId);

        await _service.InvalidateUserPermissionsCacheAsync(GuildId, UserId);

        // Second compute → recomputes from DB and repopulates cache.
        await _service.ComputePermissionsForUserAsync(UserId, GuildId);

        var key = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        Assert.That(_cache.HasEntry(key), Is.True, "Cache must be repopulated after invalidation");
    }

    [Test]
    public async Task InvalidateCache_OnlyRemovesTargetUser_OtherUsersUnaffected()
    {
        const string otherUser = "user-other";
        var keyUser = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        var keyOther = GuildPermissionsForUser.GetCacheKey(GuildId, otherUser);

        _cache.SetEntry(keyUser, "user-data");
        _cache.SetEntry(keyOther, "other-data");

        await _service.InvalidateUserPermissionsCacheAsync(GuildId, UserId);

        Assert.Multiple(() =>
        {
            Assert.That(_cache.HasEntry(keyUser), Is.False, "Target user cache removed");
            Assert.That(_cache.HasEntry(keyOther), Is.True, "Other user cache untouched");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Cache key helpers
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void GuildChannelPermission_GetCacheKey_IsDeterministic()
    {
        var k1 = GuildChannelPermission.GetCacheKey(GuildId, ChannelId, UserId);
        var k2 = GuildChannelPermission.GetCacheKey(GuildId, ChannelId, UserId);
        Assert.That(k1, Is.EqualTo(k2));
    }

    [Test]
    public void GuildChannelPermission_GetCacheKey_DifferentChannels_ProduceDifferentKeys()
    {
        var k1 = GuildChannelPermission.GetCacheKey(GuildId, "chan-a", UserId);
        var k2 = GuildChannelPermission.GetCacheKey(GuildId, "chan-b", UserId);
        Assert.That(k1, Is.Not.EqualTo(k2));
    }

    [Test]
    public void GuildChannelPermission_GetCacheKey_DifferentUsers_ProduceDifferentKeys()
    {
        var k1 = GuildChannelPermission.GetCacheKey(GuildId, ChannelId, "user-a");
        var k2 = GuildChannelPermission.GetCacheKey(GuildId, ChannelId, "user-b");
        Assert.That(k1, Is.Not.EqualTo(k2));
    }

    [Test]
    public void GuildChannelPermission_GetCacheKey_DifferentGuilds_ProduceDifferentKeys()
    {
        var k1 = GuildChannelPermission.GetCacheKey("guild-a", ChannelId, UserId);
        var k2 = GuildChannelPermission.GetCacheKey("guild-b", ChannelId, UserId);
        Assert.That(k1, Is.Not.EqualTo(k2));
    }

    [Test]
    public void GuildChannelPermission_InstanceGetCacheKey_MatchesStaticGetCacheKey()
    {
        var instance = new GuildChannelPermission
        {
            GuildId = GuildId, ChannelId = ChannelId, UserId = UserId,
            Permissions = Permissions.ViewChannel
        };
        Assert.That(
            instance.GetCacheKey(),
            Is.EqualTo(GuildChannelPermission.GetCacheKey(GuildId, ChannelId, UserId)));
    }

    [Test]
    public void GuildPermissionsForUser_GetCacheKey_IsDeterministic()
    {
        var k1 = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        var k2 = GuildPermissionsForUser.GetCacheKey(GuildId, UserId);
        Assert.That(k1, Is.EqualTo(k2));
    }

    [Test]
    public void GuildPermissionsForUser_GetCacheKey_DifferentUsers_ProduceDifferentKeys()
    {
        var k1 = GuildPermissionsForUser.GetCacheKey(GuildId, "user-a");
        var k2 = GuildPermissionsForUser.GetCacheKey(GuildId, "user-b");
        Assert.That(k1, Is.Not.EqualTo(k2));
    }

    [Test]
    public void GuildPermissionsForUser_GetCacheKey_DifferentGuilds_ProduceDifferentKeys()
    {
        var k1 = GuildPermissionsForUser.GetCacheKey("guild-a", UserId);
        var k2 = GuildPermissionsForUser.GetCacheKey("guild-b", UserId);
        Assert.That(k1, Is.Not.EqualTo(k2));
    }

    [Test]
    public void GuildPermissionsForUser_InstanceGetCacheKey_MatchesStaticGetCacheKey()
    {
        var instance = new GuildPermissionsForUser { GuildId = GuildId, UserId = UserId };
        Assert.That(
            instance.GetCacheKey(),
            Is.EqualTo(GuildPermissionsForUser.GetCacheKey(GuildId, UserId)));
    }

    // ══════════════════════════════════════════════════════════════════════════ Member-level guild
    // permission overrides GuildMember.AllowPermissions / DenyPermissions sit between role
    // aggregation and channel/category overwrites in the resolution stack.
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MemberAllow_GrantsPermissionAbsentFromAllRoles()
    {
        // Role grants nothing; member allow grants ViewChannel → user should see the channel.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember(allow: Permissions.ViewChannel));
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True,
            "Member AllowPermissions must grant a permission that no role provides");
    }

    [Test]
    public async Task MemberDeny_RevokesPermissionGrantedByRole()
    {
        // Role grants ViewChannel + SendMessages; member deny revokes SendMessages.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: Permissions.ViewChannel | Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember(deny: Permissions.SendMessages));
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "ViewChannel retained");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False,
                "Member DenyPermissions must revoke a permission granted by a role");
        });
    }

    [Test]
    public async Task MemberAllow_TakesPrecedenceOverRoleOnGuildCheck()
    {
        // Role has no permissions; member allow grants ManageChannel.
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember(allow: Permissions.ManageChannel));
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var result = await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.ManageChannel);

        Assert.That(result, Is.True,
            "Guild-level check must reflect member AllowPermissions");
    }

    [Test]
    public async Task MemberDeny_TakesPrecedenceOverRoleOnGuildCheck()
    {
        // Role grants SendMessages; member deny revokes it.
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(permissions: Permissions.SendMessages));
        _context.GuildMembers.Add(MakeGuildMember(deny: Permissions.SendMessages));
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var result = await _service.CanUserPerformActionOnGuildAsync(
            UserId, GuildId, Permissions.SendMessages);

        Assert.That(result, Is.False,
            "Guild-level check must reflect member DenyPermissions");
    }

    [Test]
    public async Task MemberAllow_IsOverriddenByChannelRoleDeny()
    {
        // Member allow grants SendMessages at guild level, but a channel-level role
        // deny overwrite strips it for this channel — channel overwrite wins.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember(allow: Permissions.SendMessages));
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.ChannelPermissions.Add(
            MakePermission("cp-chan", channelId: ChannelId, roleId: RoleId,
                deny: Permissions.SendMessages));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False,
            "Channel role-deny overwrite must override a member guild-level allow");
    }

    [Test]
    public async Task MemberAllow_NotOverriddenByChannelRoleDeny_WhenMemberSpecificAllowPresent()
    {
        // Member allow at guild level + a member-specific channel allow both present.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember(allow: Permissions.SendMessages));
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.ChannelPermissions.AddRange(
            MakePermission("cp-role", channelId: ChannelId, roleId: RoleId,
                deny: Permissions.SendMessages),
            MakePermission("cp-member", channelId: ChannelId, memberId: MemberId,
                allow: Permissions.SendMessages));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True,
            "Member-specific channel allow must restore a permission denied by a role-level channel overwrite");
    }

    [Test]
    public async Task MemberAllow_MultiplePermissions_AllGranted()
    {
        // Verify that setting multiple flags in AllowPermissions all take effect.
        var granted = Permissions.ViewChannel | Permissions.SendMessages | Permissions.AttachFiles;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember(allow: granted));
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(perms.HasFlag(Permissions.AttachFiles), Is.True);
        });
    }

    [Test]
    public async Task MemberDeny_MultiplePermissions_AllRevoked()
    {
        // Role grants a broad set; member deny removes two of them.
        var rolePerms = Permissions.ViewChannel | Permissions.SendMessages | Permissions.AttachFiles;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: rolePerms));
        _context.GuildMembers.Add(MakeGuildMember(
            deny: Permissions.SendMessages | Permissions.AttachFiles));
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "ViewChannel untouched");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False, "SendMessages revoked");
            Assert.That(perms.HasFlag(Permissions.AttachFiles), Is.False, "AttachFiles revoked");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CanGrantPermissionsAsync — privilege-escalation guard
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CanGrantPermissions_Owner_CanGrantAnything()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        await _context.SaveChangesAsync();

        var result = await _service.CanGrantPermissionsAsync(UserId, GuildId, Permissions.Superadmin);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanGrantPermissions_NonOwner_CannotGrantPermissionTheyDontHold()
    {
        await SeedWithRolePermission(Permissions.ViewChannel);

        var result = await _service.CanGrantPermissionsAsync(UserId, GuildId, Permissions.Superadmin);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanGrantPermissions_NonOwner_CanGrantSubsetOfOwnPermissions()
    {
        await SeedWithRolePermission(Permissions.ViewChannel | Permissions.SendMessages | Permissions.ManagePermissions);

        var result = await _service.CanGrantPermissionsAsync(UserId, GuildId, Permissions.ViewChannel | Permissions.SendMessages);

        Assert.That(result, Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Role hierarchy — GetHighestRolePositionAsync / CanManageRoleAsync / CanModerateTargetAsync
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetHighestRolePosition_Owner_ReturnsMaxValue()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        await _context.SaveChangesAsync();

        var position = await _service.GetHighestRolePositionAsync(UserId, GuildId);

        Assert.That(position, Is.EqualTo(int.MaxValue));
    }

    [Test]
    public async Task GetHighestRolePosition_MemberWithNoRoles_ReturnsMinValue()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(MakeGuildMember());
        await _context.SaveChangesAsync();

        var position = await _service.GetHighestRolePositionAsync(UserId, GuildId);

        Assert.That(position, Is.EqualTo(int.MinValue));
    }

    [Test]
    public async Task GetHighestRolePosition_ReturnsMaxAcrossHeldRoles()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(MakeGuildMember());
        var lowRole = MakeRole(id: "role-low");
        var highRole = MakeRole(id: "role-high");
        lowRole.Position = 1;
        highRole.Position = 5;
        _context.Roles.Add(lowRole);
        _context.Roles.Add(highRole);
        _context.RoleMembers.Add(MakeRoleMember("rm-1", "role-low", MemberId));
        _context.RoleMembers.Add(MakeRoleMember("rm-2", "role-high", MemberId));
        await _context.SaveChangesAsync();

        var position = await _service.GetHighestRolePositionAsync(UserId, GuildId);

        Assert.That(position, Is.EqualTo(5));
    }

    [Test]
    public async Task CanManageRole_Owner_CanManageAnyRole()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        _context.Roles.Add(MakeRole());
        await _context.SaveChangesAsync();

        var result = await _service.CanManageRoleAsync(UserId, GuildId, RoleId);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanManageRole_NonOwner_CannotManageRoleAtOrAboveOwnPosition()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(MakeGuildMember());
        var actorRole = MakeRole(id: "actor-role");
        var targetRole = MakeRole(id: "target-role");
        _context.Roles.Add(actorRole);
        _context.Roles.Add(targetRole);
        await _context.SaveChangesAsync();
        actorRole.Position = 2;
        targetRole.Position = 2;
        _context.RoleMembers.Add(MakeRoleMember("rm-1", "actor-role", MemberId));
        await _context.SaveChangesAsync();

        var result = await _service.CanManageRoleAsync(UserId, GuildId, "target-role");

        Assert.That(result, Is.False, "equal position must not be manageable");
    }

    [Test]
    public async Task CanManageRole_NonOwner_CanManageStrictlyLowerRole()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(MakeGuildMember());
        var actorRole = MakeRole(id: "actor-role");
        var targetRole = MakeRole(id: "target-role");
        _context.Roles.Add(actorRole);
        _context.Roles.Add(targetRole);
        await _context.SaveChangesAsync();
        actorRole.Position = 5;
        targetRole.Position = 1;
        _context.RoleMembers.Add(MakeRoleMember("rm-1", "actor-role", MemberId));
        await _context.SaveChangesAsync();

        var result = await _service.CanManageRoleAsync(UserId, GuildId, "target-role");

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanModerateTarget_NobodyCanModerateOwner()
    {
        _context.Guilds.Add(MakeGuild(ownerId: "target-owner"));
        await _context.SaveChangesAsync();

        var result = await _service.CanModerateTargetAsync(UserId, "target-owner", GuildId);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task CanModerateTarget_Owner_CanModerateAnyNonOwner()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        _context.GuildMembers.Add(MakeGuildMember(id: "target-member", userId: "target-user"));
        await _context.SaveChangesAsync();

        var result = await _service.CanModerateTargetAsync(UserId, "target-user", GuildId);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanModerateTarget_HigherRoleCanModerateLowerRole()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(MakeGuildMember(id: MemberId, userId: UserId));
        _context.GuildMembers.Add(MakeGuildMember(id: "target-member", userId: "target-user"));
        var actorRole = MakeRole(id: "actor-role");
        var targetRole = MakeRole(id: "target-role");
        _context.Roles.Add(actorRole);
        _context.Roles.Add(targetRole);
        await _context.SaveChangesAsync();
        actorRole.Position = 5;
        targetRole.Position = 1;
        _context.RoleMembers.Add(MakeRoleMember("rm-1", "actor-role", MemberId));
        _context.RoleMembers.Add(MakeRoleMember("rm-2", "target-role", "target-member"));
        await _context.SaveChangesAsync();

        var result = await _service.CanModerateTargetAsync(UserId, "target-user", GuildId);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanModerateTarget_EqualOrLowerRoleCannotModerate()
    {
        _context.Guilds.Add(MakeGuild());
        _context.GuildMembers.Add(MakeGuildMember(id: MemberId, userId: UserId));
        _context.GuildMembers.Add(MakeGuildMember(id: "target-member", userId: "target-user"));
        var actorRole = MakeRole(id: "actor-role");
        var targetRole = MakeRole(id: "target-role");
        _context.Roles.Add(actorRole);
        _context.Roles.Add(targetRole);
        await _context.SaveChangesAsync();
        actorRole.Position = 1;
        targetRole.Position = 5;
        _context.RoleMembers.Add(MakeRoleMember("rm-1", "actor-role", MemberId));
        _context.RoleMembers.Add(MakeRoleMember("rm-2", "target-role", "target-member"));
        await _context.SaveChangesAsync();

        var result = await _service.CanModerateTargetAsync(UserId, "target-user", GuildId);

        Assert.That(result, Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Mute (MutedUntil) — permission stripping
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Muted_StripsSendMessagesAndConnect_ButKeepsViewChannel()
    {
        var rolePerms = Permissions.ViewChannel | Permissions.SendMessages | Permissions.Connect | Permissions.AddReactions;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: rolePerms));
        var member = MakeGuildMember();
        member.MutedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "ViewChannel untouched by mute");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False, "SendMessages stripped while muted");
            Assert.That(perms.HasFlag(Permissions.Connect), Is.False, "Connect stripped while muted");
            Assert.That(perms.HasFlag(Permissions.AddReactions), Is.False, "AddReactions stripped while muted");
        });
    }

    [Test]
    public async Task ExpiredMute_DoesNotStripPermissions()
    {
        var rolePerms = Permissions.ViewChannel | Permissions.SendMessages;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: rolePerms));
        var member = MakeGuildMember();
        member.MutedUntil = DateTimeOffset.UtcNow.AddMinutes(-10); // expired
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True, "expired mute must not strip permissions");
    }

    [Test]
    public async Task Muted_Owner_KeepsAllPermissions()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        _context.Channels.Add(MakeChannel());
        var member = MakeGuildMember();
        member.MutedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();

        var result = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.SendMessages);

        Assert.That(result, Is.True, "owner short-circuit bypasses mute entirely");
    }

    // ══════════════════════════════════════════════════════════════════════════ Onboarding
    // (OnboardingCompletedAt) — permission stripping

    private static GuildOnboardingConfig MakeOnboardingConfig(bool enabled = true, string guildId = GuildId) => new()
    {
        GuildId = guildId, Enabled = enabled, RulesText = "Be nice", UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task OnboardingPending_StripsSendMessagesAndConnect_ButKeepsViewChannel()
    {
        var rolePerms = Permissions.ViewChannel | Permissions.SendMessages | Permissions.Connect | Permissions.AddReactions;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: rolePerms));
        _context.Set<GuildOnboardingConfig>().Add(MakeOnboardingConfig());
        var member = MakeGuildMember();
        member.OnboardingCompletedAt = null; // pending
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True, "ViewChannel untouched while onboarding pending");
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False, "SendMessages stripped while onboarding pending");
            Assert.That(perms.HasFlag(Permissions.Connect), Is.False, "Connect stripped while onboarding pending");
            Assert.That(perms.HasFlag(Permissions.AddReactions), Is.False, "AddReactions stripped while onboarding pending");
        });
    }

    [Test]
    public async Task OnboardingCompleted_DoesNotStripPermissions()
    {
        var rolePerms = Permissions.ViewChannel | Permissions.SendMessages;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: rolePerms));
        var member = MakeGuildMember();
        member.OnboardingCompletedAt = DateTimeOffset.UtcNow;
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True, "completed onboarding must not strip permissions");
    }

    [Test]
    public async Task OnboardingPending_ButOnboardingDisabled_DoesNotStripPermissions()
    {
        // A member can be un-accepted while the guild's onboarding is switched off - they joined
        // while it was on and an admin turned it off before they got around to accepting.
        var rolePerms = Permissions.ViewChannel | Permissions.SendMessages | Permissions.Connect;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: rolePerms));
        _context.Set<GuildOnboardingConfig>().Add(MakeOnboardingConfig(enabled: false));
        var member = MakeGuildMember();
        member.OnboardingCompletedAt = null;
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(perms.HasFlag(Permissions.Connect), Is.True);
        });
    }

    [Test]
    public async Task OnboardingPending_NoConfigRowAtAll_DoesNotStripPermissions()
    {
        var rolePerms = Permissions.ViewChannel | Permissions.SendMessages;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: rolePerms));
        var member = MakeGuildMember();
        member.OnboardingCompletedAt = null;
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True,
            "a guild that never configured onboarding must not gate anyone");
    }

    [Test]
    public async Task OnboardingPending_Owner_KeepsAllPermissions()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        _context.Channels.Add(MakeChannel());
        var member = MakeGuildMember();
        member.OnboardingCompletedAt = null;
        _context.GuildMembers.Add(member);
        await _context.SaveChangesAsync();

        var result = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.SendMessages);

        Assert.That(result, Is.True, "owner short-circuit bypasses onboarding gating entirely");
    }

    [Test]
    public async Task OnboardingPending_AndMuted_BothStripSamePermissions_NoDoubleCounting()
    {
        // Both conditions active at once must still just strip the one permission set,
        // not throw or behave differently than either alone.
        var rolePerms = Permissions.ViewChannel | Permissions.SendMessages;
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Roles.Add(MakeRole(permissions: rolePerms));
        _context.Set<GuildOnboardingConfig>().Add(MakeOnboardingConfig());
        var member = MakeGuildMember();
        member.OnboardingCompletedAt = null;
        member.MutedUntil = DateTimeOffset.UtcNow.AddMinutes(10);
        _context.GuildMembers.Add(member);
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var perms = await GetComputedForChannel();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Threads — inherit parent's resolved permissions; SendMessages checks
    // substitute SendMessagesInThreads for Thread-type channels.
    // ══════════════════════════════════════════════════════════════════════════

    private const string ThreadId = "thread-1";

    private static Channel MakeThread(string parentChannelId = ChannelId, string id = ThreadId, string guildId = GuildId) => new()
    {
        Id = id, GuildId = guildId, Name = "test-thread", Description = "",
        Type = ChannelType.Thread, ParentChannelId = parentChannelId,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task Thread_InheritsParentChannelOverwrite()
    {
        // Role alone would not grant ViewChannel, but a channel-level overwrite on the
        // parent grants it — the thread must see the same resolved result as its parent.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Channels.Add(MakeThread());
        _context.Roles.Add(MakeRole(permissions: Permissions.None));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        _context.Set<ChannelPermission>().Add(MakePermission("perm-1", channelId: ChannelId, roleId: RoleId, allow: Permissions.ViewChannel));
        await _context.SaveChangesAsync();

        var result = await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        var threadPerms = result.Permissions.First(p => p.ChannelId == ThreadId).Permissions;
        var parentPerms = result.Permissions.First(p => p.ChannelId == ChannelId).Permissions;

        Assert.That(threadPerms, Is.EqualTo(parentPerms));
        Assert.That(threadPerms.HasFlag(Permissions.ViewChannel), Is.True);
    }

    [Test]
    public async Task Thread_SendMessagesCheck_SubstitutesSendMessagesInThreads()
    {
        // Role grants SendMessagesInThreads but not plain SendMessages — posting in the
        // thread must still be allowed.
        _context.Guilds.Add(MakeGuild());
        _context.Channels.Add(MakeChannel());
        _context.Channels.Add(MakeThread());
        _context.Roles.Add(MakeRole(permissions: Permissions.ViewChannel | Permissions.SendMessagesInThreads));
        _context.GuildMembers.Add(MakeGuildMember());
        _context.RoleMembers.Add(MakeRoleMember("rm-1", RoleId, MemberId));
        await _context.SaveChangesAsync();

        var canPostInThread = await _service.CanUserPerformActionAsync(UserId, ThreadId, Permissions.SendMessages);
        var canPostInParent = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.SendMessages);

        Assert.Multiple(() =>
        {
            Assert.That(canPostInThread, Is.True, "SendMessagesInThreads must satisfy a SendMessages check on a thread");
            Assert.That(canPostInParent, Is.False, "the parent channel itself still requires plain SendMessages");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ClampToGrantableAsync / CanGrantPermissionsAsync — bot-install privilege clamp Extracted so
    // the install flow can silently downgrade an over-broad request instead of rejecting it
    // outright; CanGrantPermissionsAsync must keep its original boolean semantics on top of the
    // same shared math. ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ClampToGrantable_Owner_ReturnsRequestedUnchanged()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        await _context.SaveChangesAsync();

        var requested = Permissions.ManageGuild | Permissions.BanMembers | Permissions.SendMessages;
        var clamped = await _service.ClampToGrantableAsync(UserId, GuildId, requested);

        Assert.That(clamped, Is.EqualTo(requested), "Owner's expanded base permissions cover every bit");
    }

    [Test]
    public async Task ClampToGrantable_NonOwner_IntersectsWithActorsBasePermissions()
    {
        // Actor holds only SendMessages; requests SendMessages + BanMembers.
        await SeedWithRolePermission(Permissions.SendMessages);

        var clamped = await _service.ClampToGrantableAsync(
            UserId, GuildId, Permissions.SendMessages | Permissions.BanMembers);

        Assert.That(clamped, Is.EqualTo(Permissions.SendMessages),
            "Only the bit the actor actually holds should survive the clamp");
    }

    [Test]
    public async Task ClampToGrantable_NoOverlap_ReturnsNone()
    {
        await SeedWithRolePermission(Permissions.ViewChannel);

        var clamped = await _service.ClampToGrantableAsync(UserId, GuildId, Permissions.ManageGuild);

        Assert.That(clamped, Is.EqualTo(Permissions.None));
    }

    [Test]
    public async Task ClampToGrantable_ActorsImpliedPermissionsCountTowardClamp()
    {
        // Actor holds Stream, which implies Speak + Connect; requesting Speak should
        // survive the clamp even though no role grants Speak directly.
        await SeedWithRolePermission(Permissions.Stream);

        var clamped = await _service.ClampToGrantableAsync(UserId, GuildId, Permissions.Speak);

        Assert.That(clamped, Is.EqualTo(Permissions.Speak),
            "Implied permissions from ExpandImpliedPermissions must count as grantable");
    }

    [Test]
    public async Task CanGrantPermissions_ActorHasAllRequestedBits_ReturnsTrue()
    {
        await SeedWithRolePermission(Permissions.SendMessages | Permissions.ViewChannel);

        var result = await _service.CanGrantPermissionsAsync(
            UserId, GuildId, Permissions.SendMessages | Permissions.ViewChannel);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task CanGrantPermissions_ActorMissingABit_ReturnsFalse()
    {
        await SeedWithRolePermission(Permissions.SendMessages);

        var result = await _service.CanGrantPermissionsAsync(
            UserId, GuildId, Permissions.SendMessages | Permissions.BanMembers);

        Assert.That(result, Is.False, "Actor must not be able to grant a bit it doesn't hold itself");
    }
}
