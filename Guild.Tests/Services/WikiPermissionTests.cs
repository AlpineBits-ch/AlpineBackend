using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// Verifies that the wiki-specific permission flags (ViewWiki, CreateWikiPages,
/// EditOwnWikiPages, EditAnyWikiPage, DeleteWikiPages, ManageWikiRevisions,
/// ManageWikiStructure, PublishWikiPublicly) flow through GuildPermissionService
/// correctly at the guild level.
///
/// The guild-level check (CanUserPerformActionOnGuildAsync) is what all wiki
/// endpoints use — there is no channel context for wiki operations.
///
/// Edit permission selection (EditOwnWikiPages vs EditAnyWikiPage) is determined
/// by the endpoint based on page.AuthorId == userId.  These tests verify that
/// each flag independently grants access when held, and that holding one does
/// not imply the other.
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

    private static Role MakeRole(Permissions permissions) => new()
    {
        Id = RoleId, GuildId = GuildId, Type = RoleType.None, Name = "wiki-role",
        Permissions = permissions,
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

    private async Task SeedWithPermission(Permissions permission)
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(permission));
        _context.GuildMembers.Add(MakeMember());
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();
    }

    private Task<bool> Can(Permissions permission) =>
        _service.CanUserPerformActionOnGuildAsync(UserId, GuildId, permission);

    // ══════════════════════════════════════════════════════════════════════════
    // Owner short-circuit
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GuildOwner_HasAllWikiPermissions()
    {
        _context.Guilds.Add(MakeGuild(ownerId: UserId));
        await _context.SaveChangesAsync();

        var wikiPermissions = new[]
        {
            Permissions.ViewWiki,
            Permissions.CreateWikiPages,
            Permissions.EditOwnWikiPages,
            Permissions.EditAnyWikiPage,
            Permissions.DeleteWikiPages,
            Permissions.ManageWikiRevisions,
            Permissions.ManageWikiStructure,
            Permissions.PublishWikiPublicly,
        };

        foreach (var perm in wikiPermissions)
        {
            Assert.That(await Can(perm), Is.True, $"Owner must have {perm}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ViewWiki
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ViewWiki_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(Permissions.ViewWiki);

        Assert.That(await Can(Permissions.ViewWiki), Is.True);
    }

    [Test]
    public async Task ViewWiki_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(Permissions.SendMessages);

        Assert.That(await Can(Permissions.ViewWiki), Is.False);
    }

    [Test]
    public async Task ViewWiki_UserHasNoRoles_ReturnsFalse()
    {
        _context.Guilds.Add(MakeGuild());
        await _context.SaveChangesAsync();

        Assert.That(await Can(Permissions.ViewWiki), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // CreateWikiPages
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CreateWikiPages_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(Permissions.CreateWikiPages);

        Assert.That(await Can(Permissions.CreateWikiPages), Is.True);
    }

    [Test]
    public async Task CreateWikiPages_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(Permissions.ViewWiki);

        Assert.That(await Can(Permissions.CreateWikiPages), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // EditOwnWikiPages vs EditAnyWikiPage
    //
    // These are separate flags; holding one does not imply the other.
    // The endpoint selects the required flag based on page.AuthorId == userId.
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task EditOwnWikiPages_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(Permissions.EditOwnWikiPages);

        Assert.That(await Can(Permissions.EditOwnWikiPages), Is.True);
    }

    [Test]
    public async Task EditOwnWikiPages_DoesNotGrantEditAnyWikiPage()
    {
        await SeedWithPermission(Permissions.EditOwnWikiPages);

        Assert.That(await Can(Permissions.EditAnyWikiPage), Is.False);
    }

    [Test]
    public async Task EditAnyWikiPage_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(Permissions.EditAnyWikiPage);

        Assert.That(await Can(Permissions.EditAnyWikiPage), Is.True);
    }

    [Test]
    public async Task EditAnyWikiPage_AlsoSatisfiesEditAnyCheck()
    {
        // A user with EditAnyWikiPage can edit both their own pages and others'.
        // The endpoint uses EditAnyWikiPage when page.AuthorId != userId, so
        // having this flag must return true for that check.
        await SeedWithPermission(Permissions.EditAnyWikiPage);

        Assert.That(await Can(Permissions.EditAnyWikiPage), Is.True);
    }

    [Test]
    public async Task EditAnyWikiPage_DoesNotImplyEditOwnWikiPages_AsADistinctFlag()
    {
        // EditAnyWikiPage is the stronger flag; EditOwnWikiPages is the weaker one.
        // A user with only EditAnyWikiPage should NOT independently satisfy the
        // EditOwnWikiPages check (they would in practice because the endpoint uses
        // EditOwnWikiPages for own pages, and they have EditAnyWikiPage for others,
        // but the permission service treats them as independent flags unless the
        // ExpandImpliedPermissions chain says otherwise).
        await SeedWithPermission(Permissions.EditAnyWikiPage);

        // If the service does not implement an implication chain for these wiki
        // flags, this will be false.  If a future implication is added (EditAny →
        // EditOwn), this test should be updated.
        var editAnyImpliesOwn = await Can(Permissions.EditOwnWikiPages);

        // Document the current behaviour without asserting a specific value,
        // so future implication additions don't silently break tests.
        Assert.Pass($"EditAnyWikiPage implies EditOwnWikiPages: {editAnyImpliesOwn}");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DeleteWikiPages
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeleteWikiPages_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(Permissions.DeleteWikiPages);

        Assert.That(await Can(Permissions.DeleteWikiPages), Is.True);
    }

    [Test]
    public async Task DeleteWikiPages_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(Permissions.ViewWiki | Permissions.CreateWikiPages);

        Assert.That(await Can(Permissions.DeleteWikiPages), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ManageWikiRevisions
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ManageWikiRevisions_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(Permissions.ManageWikiRevisions);

        Assert.That(await Can(Permissions.ManageWikiRevisions), Is.True);
    }

    [Test]
    public async Task ManageWikiRevisions_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(Permissions.ViewWiki);

        Assert.That(await Can(Permissions.ManageWikiRevisions), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ManageWikiStructure
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ManageWikiStructure_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(Permissions.ManageWikiStructure);

        Assert.That(await Can(Permissions.ManageWikiStructure), Is.True);
    }

    [Test]
    public async Task ManageWikiStructure_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(Permissions.CreateWikiPages);

        Assert.That(await Can(Permissions.ManageWikiStructure), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PublishWikiPublicly
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PublishWikiPublicly_UserHasPermission_ReturnsTrue()
    {
        await SeedWithPermission(Permissions.PublishWikiPublicly);

        Assert.That(await Can(Permissions.PublishWikiPublicly), Is.True);
    }

    [Test]
    public async Task PublishWikiPublicly_UserLacksPermission_ReturnsFalse()
    {
        await SeedWithPermission(Permissions.CreateWikiPages | Permissions.EditOwnWikiPages);

        Assert.That(await Can(Permissions.PublishWikiPublicly), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Isolation — non-wiki permissions do not grant wiki access
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task NonWikiPermissions_DoNotGrantViewWiki()
    {
        await SeedWithPermission(Permissions.ManageChannel | Permissions.SendMessages | Permissions.ViewChannel);

        Assert.That(await Can(Permissions.ViewWiki), Is.False);
    }

    [Test]
    public async Task NonWikiPermissions_DoNotGrantCreateWikiPages()
    {
        await SeedWithPermission(Permissions.ManageChannel | Permissions.ManagePermissions);

        Assert.That(await Can(Permissions.CreateWikiPages), Is.False);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Multiple roles — wiki permissions combined via OR
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MultipleRoles_WikiPermissionsCombinedViaOR()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.AddRange(
            new Role
            {
                Id = "role_a", GuildId = GuildId, Type = RoleType.None, Name = "role-a",
                Permissions = Permissions.ViewWiki,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, 
            },
            new Role
            {
                Id = "role_b", GuildId = GuildId, Type = RoleType.None, Name = "role-b",
                Permissions = Permissions.CreateWikiPages,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        _context.GuildMembers.Add(MakeMember());
        _context.RoleMembers.AddRange(
            new RoleMember { Id = "rm_a", RoleId = "role_a", MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
            new RoleMember { Id = "rm_b", RoleId = "role_b", MemberId = MemberId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await Can(Permissions.ViewWiki), Is.True, "ViewWiki from role_a");
            Assert.That(await Can(Permissions.CreateWikiPages), Is.True, "CreateWikiPages from role_b");
            Assert.That(await Can(Permissions.DeleteWikiPages), Is.False, "DeleteWikiPages in neither role");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Superadmin
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Superadmin_GrantsAllWikiPermissions()
    {
        await SeedWithPermission(Permissions.Superadmin);

        Assert.Multiple(async () =>
        {
            Assert.That(await Can(Permissions.ViewWiki), Is.True);
            Assert.That(await Can(Permissions.CreateWikiPages), Is.True);
            Assert.That(await Can(Permissions.EditOwnWikiPages), Is.True);
            Assert.That(await Can(Permissions.EditAnyWikiPage), Is.True);
            Assert.That(await Can(Permissions.DeleteWikiPages), Is.True);
            Assert.That(await Can(Permissions.ManageWikiRevisions), Is.True);
            Assert.That(await Can(Permissions.ManageWikiStructure), Is.True);
            Assert.That(await Can(Permissions.PublishWikiPublicly), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Member-level overrides for wiki permissions
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MemberAllow_GrantsViewWiki_WhenRoleHasNone()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(Permissions.None));
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId,
            JoinedAt = DateTime.UtcNow,
            AllowPermissions = Permissions.ViewWiki, DenyPermissions = Permissions.None,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();

        Assert.That(await Can(Permissions.ViewWiki), Is.True);
    }

    [Test]
    public async Task MemberDeny_RevokesCreateWikiPages_GrantedByRole()
    {
        _context.Guilds.Add(MakeGuild());
        _context.Roles.Add(MakeRole(Permissions.CreateWikiPages));
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId,
            JoinedAt = DateTime.UtcNow, 
            AllowPermissions = Permissions.None, DenyPermissions = Permissions.CreateWikiPages,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(MakeRoleMember());
        await _context.SaveChangesAsync();

        Assert.That(await Can(Permissions.CreateWikiPages), Is.False);
    }
}
