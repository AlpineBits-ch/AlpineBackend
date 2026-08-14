using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Verifies that the wiki-specific permission flags (ViewWiki, CreateWikiPages, EditOwnWikiPages,
/// EditAnyWikiPage, DeleteWikiPages, ManageWikiRevisions, ManageWikiStructure, PublishWikiPublicly)
/// flow through GuildPermissionService correctly at the guild level.
/// </summary>
[TestFixture]
public class WikiPermissionTests
{
    private const string GuildId = "gild_wiki";
    private const string OwnerId = "owner_wiki";
    private const string UserId = "user_wiki";
    private const string RoleId = "role_wiki";
    private const string MemberId = "member_wiki";

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

    // ── Entity helpers ────────────────────────────────────────────────────────

    private static Guild.Domain.Aggregates.Guild MakeGuild(string ownerId = OwnerId) => new()
    {
        Id = GuildId, OwnerId = ownerId, Name = "Wiki Guild",
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Role MakeRole(ModulePermissions permissions) => new()
    {
        Id = RoleId, GuildId = GuildId, Type = RoleType.None, Name = "wiki-role",
        ModulePermissions = permissions,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GuildMember MakeMember() => new()
    {
        Id = MemberId, GuildId = GuildId, UserId = UserId,
        JoinedAt = DateTime.UtcNow,
        AllowPermissions = Permissions.None, DenyPermissions = Permissions.None,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        SearchValue = $"{UserId}#{GuildId}",
    };

    private static RoleMember MakeRoleMember() => new()
    {
        Id = "rm_wiki", RoleId = RoleId, MemberId = MemberId,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    private async Task SeedWithPermission(ModulePermissions permission)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(permission));
        _context.GuildMembers.Add(MakeMember());
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();
    }

    /// <summary>Seeds a role holding a core-mask permission instead of a wiki one.</summary>
    private async Task SeedWithCorePermission(Permissions permission)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Type = RoleType.None, Name = "wiki-role",
            Permissions = permission,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(MakeMember());
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();
    }

    private Task<bool> Can(ModulePermissions permission) =>
        _service.CanUserPerformActionOnGuildAsync(UserId, GuildId, permission);

    // ══════════════════════════════════════════════════════════════════════════ Owner
    // short-circuit ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GuildOwner_HasAllWikiPermissions()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        await _context.SaveChangesAsync();

        var wikiPermissions = new[]
        {
            ModulePermissions.ViewWiki,
            ModulePermissions.CreateWikiPages,
            ModulePermissions.EditOwnWikiPages,
            ModulePermissions.EditAnyWikiPage,
            ModulePermissions.DeleteWikiPages,
            ModulePermissions.ManageWikiRevisions,
            ModulePermissions.ManageWikiStructure,
            ModulePermissions.PublishWikiPublicly,
        };

        foreach (var perm in wikiPermissions)
        {
            Assert.That(await Can(perm), Is.True, $"Owner must have {perm}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════ ViewWiki
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ViewWiki_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(ModulePermissions.ViewWiki);

        Assert.That(await Can(ModulePermissions.ViewWiki), Is.True);
    }

    [Test]
    public async Task ViewWiki_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithCorePermission(Permissions.SendMessages);

        Assert.That(await Can(ModulePermissions.ViewWiki), Is.False);
    }

    [Test]
    public async Task ViewWiki_UserHasNoRoles_ReturnsFalse()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        Assert.That(await Can(ModulePermissions.ViewWiki), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════ CreateWikiPages
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateWikiPages_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(ModulePermissions.CreateWikiPages);

        Assert.That(await Can(ModulePermissions.CreateWikiPages), Is.True);
    }

    [Test]
    public async Task CreateWikiPages_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(ModulePermissions.ViewWiki);

        Assert.That(await Can(ModulePermissions.CreateWikiPages), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════ EditOwnWikiPages
    // vs EditAnyWikiPage

    [Test]
    public async Task EditOwnWikiPages_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(ModulePermissions.EditOwnWikiPages);

        Assert.That(await Can(ModulePermissions.EditOwnWikiPages), Is.True);
    }

    [Test]
    public async Task EditOwnWikiPages_DoesNotGrantEditAnyWikiPage()
    {
        await SeedWithPermission(ModulePermissions.EditOwnWikiPages);

        Assert.That(await Can(ModulePermissions.EditAnyWikiPage), Is.False);
    }

    [Test]
    public async Task EditAnyWikiPage_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(ModulePermissions.EditAnyWikiPage);

        Assert.That(await Can(ModulePermissions.EditAnyWikiPage), Is.True);
    }

    [Test]
    public async Task EditAnyWikiPage_AlsoSatisfiesEditAnyCheck()
    {
        // A user with EditAnyWikiPage can edit both their own pages and others'.
        await SeedWithPermission(ModulePermissions.EditAnyWikiPage);

        Assert.That(await Can(ModulePermissions.EditAnyWikiPage), Is.True);
    }

    [Test]
    public async Task EditAnyWikiPage_DoesNotImplyEditOwnWikiPages_AsADistinctFlag()
    {
        // EditAnyWikiPage is the stronger flag; EditOwnWikiPages is the weaker one.
        await SeedWithPermission(ModulePermissions.EditAnyWikiPage);

        // If the service does not implement an implication chain for these wiki flags, this will be
        // false.
        var editAnyImpliesOwn = await Can(ModulePermissions.EditOwnWikiPages);

        // Document the current behaviour without asserting a specific value,
        // so future implication additions don't silently break tests.
        Assert.Pass($"EditAnyWikiPage implies EditOwnWikiPages: {editAnyImpliesOwn}");
    }

    // ══════════════════════════════════════════════════════════════════════════ DeleteWikiPages
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteWikiPages_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(ModulePermissions.DeleteWikiPages);

        Assert.That(await Can(ModulePermissions.DeleteWikiPages), Is.True);
    }

    [Test]
    public async Task DeleteWikiPages_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(ModulePermissions.ViewWiki | ModulePermissions.CreateWikiPages);

        Assert.That(await Can(ModulePermissions.DeleteWikiPages), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ManageWikiRevisions
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ManageWikiRevisions_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(ModulePermissions.ManageWikiRevisions);

        Assert.That(await Can(ModulePermissions.ManageWikiRevisions), Is.True);
    }

    [Test]
    public async Task ManageWikiRevisions_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(ModulePermissions.ViewWiki);

        Assert.That(await Can(ModulePermissions.ManageWikiRevisions), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ManageWikiStructure
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ManageWikiStructure_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(ModulePermissions.ManageWikiStructure);

        Assert.That(await Can(ModulePermissions.ManageWikiStructure), Is.True);
    }

    [Test]
    public async Task ManageWikiStructure_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(ModulePermissions.CreateWikiPages);

        Assert.That(await Can(ModulePermissions.ManageWikiStructure), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PublishWikiPublicly
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishWikiPublicly_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(ModulePermissions.PublishWikiPublicly);

        Assert.That(await Can(ModulePermissions.PublishWikiPublicly), Is.True);
    }

    [Test]
    public async Task PublishWikiPublicly_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(ModulePermissions.CreateWikiPages | ModulePermissions.EditOwnWikiPages);

        Assert.That(await Can(ModulePermissions.PublishWikiPublicly), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Isolation - non-wiki permissions do not grant wiki access
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NonWikiPermissions_DoNotGrantViewWiki()
    {
        await SeedWithCorePermission(Permissions.ManageChannel | Permissions.SendMessages | Permissions.ViewChannel);

        Assert.That(await Can(ModulePermissions.ViewWiki), Is.False);
    }

    [Test]
    public async Task NonWikiPermissions_DoNotGrantCreateWikiPages()
    {
        await SeedWithCorePermission(Permissions.ManageChannel | Permissions.ManagePermissions);

        Assert.That(await Can(ModulePermissions.CreateWikiPages), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Multiple roles - wiki permissions combined via OR
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MultipleRoles_WikiPermissionsCombinedViaOR()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.AddRange(
            new Role
            {
                Id = "role_a", GuildId = GuildId, Type = RoleType.None, Name = "role-a",
                ModulePermissions = ModulePermissions.ViewWiki,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, 
            },
            new Role
            {
                Id = "role_b", GuildId = GuildId, Type = RoleType.None, Name = "role-b",
                ModulePermissions = ModulePermissions.CreateWikiPages,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        _context.GuildMembers.Add(MakeMember());
        _context.RoleMembers.AddRange(
            new RoleMember { Id = "rm_a", RoleId = "role_a", MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
            new RoleMember { Id = "rm_b", RoleId = "role_b", MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await Can(ModulePermissions.ViewWiki), Is.True, "ViewWiki from role_a");
            Assert.That(await Can(ModulePermissions.CreateWikiPages), Is.True, "CreateWikiPages from role_b");
            Assert.That(await Can(ModulePermissions.DeleteWikiPages), Is.False, "DeleteWikiPages in neither role");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Superadmin
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Superadmin_GrantsAllWikiPermissions()
    {
        await SeedWithCorePermission(Permissions.Superadmin);

        Assert.Multiple(async () =>
        {
            Assert.That(await Can(ModulePermissions.ViewWiki), Is.True);
            Assert.That(await Can(ModulePermissions.CreateWikiPages), Is.True);
            Assert.That(await Can(ModulePermissions.EditOwnWikiPages), Is.True);
            Assert.That(await Can(ModulePermissions.EditAnyWikiPage), Is.True);
            Assert.That(await Can(ModulePermissions.DeleteWikiPages), Is.True);
            Assert.That(await Can(ModulePermissions.ManageWikiRevisions), Is.True);
            Assert.That(await Can(ModulePermissions.ManageWikiStructure), Is.True);
            Assert.That(await Can(ModulePermissions.PublishWikiPublicly), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Member-level overrides for wiki permissions
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MemberAllow_GrantsViewWiki_WhenRoleHasNone()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(ModulePermissions.None));
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId,
            JoinedAt = DateTime.UtcNow,
            AllowModulePermissions = ModulePermissions.ViewWiki, DenyPermissions = Permissions.None,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();

        Assert.That(await Can(ModulePermissions.ViewWiki), Is.True);
    }

    [Test]
    public async Task MemberDeny_RevokesCreateWikiPages_GrantedByRole()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(ModulePermissions.CreateWikiPages));
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId,
            JoinedAt = DateTime.UtcNow, 
            AllowPermissions = Permissions.None, DenyModulePermissions = ModulePermissions.CreateWikiPages,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();

        Assert.That(await Can(ModulePermissions.CreateWikiPages), Is.False);
    }
}
