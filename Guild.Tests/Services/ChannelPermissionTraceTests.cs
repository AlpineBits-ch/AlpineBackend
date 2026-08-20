using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>What one role or member actually ends up with in one channel, and why.</summary>
[TestFixture]
public class ChannelPermissionTraceTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string CategoryId = "cat-1";
    private const string ChannelId = "chan-1";
    private const string EveryoneRoleId = "role-everyone";
    private const string PlayerRoleId = "role-player";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _service = PermissionTestFactory.Create(_cache, _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g", CreatedAt = now, UpdatedAt = now,
        });
        _context.Categories.Add(new Category
        {
            Id = CategoryId, GuildId = GuildId, Name = "cat", CreatedAt = now, UpdatedAt = now,
        });
        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, CategoryId = CategoryId, Name = "c", Description = "d",
            Type = ChannelType.Text, CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = "everyone", Type = RoleType.Everyone,
            Permissions = Permissions.ViewChannel | Permissions.SendMessages | Permissions.ReadMessageHistory,
            CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = PlayerRoleId, GuildId = GuildId, Name = "player", Type = RoleType.None,
            Permissions = Permissions.AddReactions, CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
    }

    private async Task AddOverwriteAsync(
        string? channelId, string? categoryId, string roleId,
        Permissions allow, Permissions deny)
    {
        var now = DateTimeOffset.UtcNow;
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(),
            ChannelId = channelId, CategoryId = categoryId, RoleId = roleId, MemberId = null,
            AllowPermissions = allow, DenyPermissions = deny,
            AllowModulePermissions = ModulePermissions.None,
            DenyModulePermissions = ModulePermissions.None,
            CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task RoleSubject_UnionsTheRoleWithEveryone()
    {
        await SeedAsync();

        var result = await _service.TraceChannelPermissionsAsync(
            ChannelId, new PermissionSubject(PermissionSubjectKind.Role, PlayerRoleId));

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Permissions.HasFlag(Permissions.AddReactions), Is.True);
            Assert.That(result.Permissions.HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(result.Sources[Permissions.ViewChannel], Is.EqualTo(PermissionSource.Base));
        });
    }

    /// <summary>Record only visits set bits, so a bit nothing ever grants or overwrites has to be
    /// seeded up front or it is missing from Sources entirely, not just defaulted.</summary>
    [Test]
    public async Task UngrantedPermission_StillHasABaseSourceEntry()
    {
        await SeedAsync();

        var result = await _service.TraceChannelPermissionsAsync(
            ChannelId, new PermissionSubject(PermissionSubjectKind.Role, PlayerRoleId));

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Permissions.HasFlag(Permissions.ManageChannel), Is.False);
            Assert.That(result.Sources[Permissions.ManageChannel], Is.EqualTo(PermissionSource.Base));
        });
    }

    [Test]
    public async Task ChannelDenyBeatsCategoryAllow_AndIsAttributed()
    {
        await SeedAsync();
        await AddOverwriteAsync(null, CategoryId, PlayerRoleId, Permissions.SendMessages, Permissions.None);
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.None, Permissions.SendMessages);

        var result = await _service.TraceChannelPermissionsAsync(
            ChannelId, new PermissionSubject(PermissionSubjectKind.Role, PlayerRoleId));

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Permissions.HasFlag(Permissions.SendMessages), Is.False);
            Assert.That(result.Sources[Permissions.SendMessages], Is.EqualTo(PermissionSource.ChannelEveryoneDeny));
        });
    }

    [Test]
    public async Task DenyingViewChannel_MarksTheCollateralAsImplied()
    {
        await SeedAsync();
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.None, Permissions.ViewChannel);

        var result = await _service.TraceChannelPermissionsAsync(
            ChannelId, new PermissionSubject(PermissionSubjectKind.Role, PlayerRoleId));

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Sources[Permissions.ViewChannel], Is.EqualTo(PermissionSource.ChannelEveryoneDeny));
            Assert.That(result.Sources[Permissions.SendMessages], Is.EqualTo(PermissionSource.Implied));
        });
    }

    [Test]
    public async Task UnknownChannel_ReturnsNull()
    {
        await SeedAsync();

        var result = await _service.TraceChannelPermissionsAsync(
            "nope", new PermissionSubject(PermissionSubjectKind.Role, PlayerRoleId));

        Assert.That(result, Is.Null);
    }

    /// <summary>The trace and the gate every endpoint calls have to agree, or the UI shows one
    /// answer while the server enforces another.</summary>
    [Test]
    public async Task MemberSubject_AgreesWithTheEnforcedGate()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-1", GuildId = GuildId, UserId = "user-1", JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1", CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = EveryoneRoleId, MemberId = "member-1", CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();

        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.None, Permissions.SendMessages);

        var traced = await _service.TraceChannelPermissionsAsync(
            ChannelId, new PermissionSubject(PermissionSubjectKind.Member, "member-1"));

        var enforced = await _service.CanUserPerformActionAsync("user-1", ChannelId, Permissions.SendMessages);

        Assert.That(traced, Is.Not.Null);
        Assert.That(traced!.Permissions.HasFlag(Permissions.SendMessages), Is.EqualTo(enforced));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Member-tier sources, which only a subject with a matching overwrite
    // can produce through the real pipeline.
    // ══════════════════════════════════════════════════════════════════════

    private const string MemberId = "member-1";
    private const string UserId = "user-1";

    private async Task SeedMemberAsync(DateTimeOffset? mutedUntil = null)
    {
        var now = DateTimeOffset.UtcNow;

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1", MutedUntil = mutedUntil, CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = EveryoneRoleId, MemberId = MemberId, CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    private async Task AddMemberOverwriteAsync(
        string? channelId, string? categoryId, Permissions allow, Permissions deny)
    {
        var now = DateTimeOffset.UtcNow;
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(),
            ChannelId = channelId, CategoryId = categoryId, RoleId = null, MemberId = MemberId,
            AllowPermissions = allow, DenyPermissions = deny,
            AllowModulePermissions = ModulePermissions.None,
            DenyModulePermissions = ModulePermissions.None,
            CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    private Task<ResolvedChannelPermissions?> TraceMemberAsync(string channelId = ChannelId) =>
        _service.TraceChannelPermissionsAsync(
            channelId, new PermissionSubject(PermissionSubjectKind.Member, MemberId));

    [Test]
    public async Task ACategoryMemberOverwrite_IsAttributedToItsOwnTier()
    {
        await SeedAsync();
        await SeedMemberAsync();
        await AddMemberOverwriteAsync(null, CategoryId, Permissions.ManageChannel, Permissions.SendMessages);

        var result = await TraceMemberAsync();

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Sources[Permissions.ManageChannel], Is.EqualTo(PermissionSource.CategoryMemberAllow));
            Assert.That(result.Sources[Permissions.SendMessages], Is.EqualTo(PermissionSource.CategoryMemberDeny));
        });
    }

    [Test]
    public async Task AChannelMemberOverwrite_IsAttributedToItsOwnTier()
    {
        await SeedAsync();
        await SeedMemberAsync();
        await AddMemberOverwriteAsync(ChannelId, null, Permissions.ManageChannel, Permissions.SendMessages);

        var result = await TraceMemberAsync();

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Sources[Permissions.ManageChannel], Is.EqualTo(PermissionSource.ChannelMemberAllow));
            Assert.That(result.Sources[Permissions.SendMessages], Is.EqualTo(PermissionSource.ChannelMemberDeny));
        });
    }

    [Test]
    public async Task TheMemberGuildLevelMask_HasItsOwnSources()
    {
        await SeedAsync();
        await SeedMemberAsync();

        var member = await _context.GuildMembers.FindAsync(MemberId);
        member!.AllowPermissions = Permissions.ManageChannel;
        member.DenyPermissions = Permissions.SendMessages;
        await _context.SaveChangesAsync();

        var result = await TraceMemberAsync();

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Sources[Permissions.ManageChannel], Is.EqualTo(PermissionSource.MemberGuildAllow));
            Assert.That(result.Sources[Permissions.SendMessages], Is.EqualTo(PermissionSource.MemberGuildDeny));
        });
    }

    [Test]
    public async Task AMutedMember_LosesEverythingButTheRetainedSet()
    {
        await SeedAsync();
        await SeedMemberAsync(DateTimeOffset.UtcNow.AddHours(1));

        var result = await TraceMemberAsync();

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Permissions.HasFlag(Permissions.ViewChannel), Is.True);
            Assert.That(result.Permissions.HasFlag(Permissions.SendMessages), Is.False);
            Assert.That(result.Sources[Permissions.SendMessages], Is.EqualTo(PermissionSource.Muted));
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // The two gates outside the overwrite pipeline.
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ADisabledModulesPermissions_AreReportedAsUnavailableRatherThanGranted()
    {
        await SeedAsync();
        await SeedMemberAsync();

        var guild = await _context.Guilds.FindAsync(GuildId);
        guild!.Features = GuildFeatures.Moderation;
        await _context.SaveChangesAsync();

        // Granted outright by an overwrite, so only the feature gate can take it away.
        await AddMemberOverwriteAsync(ChannelId, null, Permissions.CreateThreads | Permissions.Connect, Permissions.None);

        var result = await TraceMemberAsync();

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Permissions.HasFlag(Permissions.CreateThreads), Is.False);
            Assert.That(result.Permissions.HasFlag(Permissions.Connect), Is.False);
            Assert.That(result.Sources[Permissions.CreateThreads], Is.EqualTo(PermissionSource.ModuleDisabled));
            Assert.That(result.Sources[Permissions.Connect], Is.EqualTo(PermissionSource.ModuleDisabled));
            Assert.That(result.Permissions.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    [Test]
    public async Task ADisabledModule_AlsoClampsTheOwner()
    {
        await SeedAsync();

        var guild = await _context.Guilds.FindAsync(GuildId);
        guild!.Features = GuildFeatures.Moderation;
        await _context.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-owner", GuildId = GuildId, UserId = OwnerId, JoinedAt = DateTime.UtcNow,
            SearchValue = "OWNER-1", CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();

        var result = await _service.TraceChannelPermissionsAsync(
            ChannelId, new PermissionSubject(PermissionSubjectKind.Member, "member-owner"));

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Permissions.HasFlag(Permissions.Connect), Is.False);
            Assert.That(result.Permissions.HasFlag(Permissions.ManageChannel), Is.True);
            Assert.That(result.ModulePermissions.HasFlag(ModulePermissions.ManageScenes), Is.False);
            Assert.That(result.Sources[Permissions.ManageChannel], Is.EqualTo(PermissionSource.Superadmin));
        });
    }

    /// <summary>A cast-only scene is thread-shaped, so the pipeline resolves the parent and would
    /// otherwise report a channel the member cannot see at all as fully readable.</summary>
    [Test]
    public async Task ACastOnlyScene_DeniesEverythingForAMemberWithNobodyInIt()
    {
        await SeedAsync();
        await SeedMemberAsync();
        await SeedCastOnlySceneAsync();

        var result = await TraceMemberAsync(SceneChannelId);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Permissions, Is.EqualTo(Permissions.None));
            Assert.That(result.ModulePermissions, Is.EqualTo(ModulePermissions.None));
            Assert.That(result.Sources[Permissions.ViewChannel], Is.EqualTo(PermissionSource.SceneRestricted));
        });
    }

    /// <summary>Cast membership is a property of a person, so the role answer stays the parent's.</summary>
    [Test]
    public async Task ACastOnlyScene_LeavesARoleSubjectUnclamped()
    {
        await SeedAsync();
        await SeedCastOnlySceneAsync();

        var result = await _service.TraceChannelPermissionsAsync(
            SceneChannelId, new PermissionSubject(PermissionSubjectKind.Role, PlayerRoleId));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Permissions.HasFlag(Permissions.ViewChannel), Is.True);
    }

    private const string SceneChannelId = "scene-1";

    private async Task SeedCastOnlySceneAsync()
    {
        var now = DateTimeOffset.UtcNow;

        var guild = await _context.Guilds.FindAsync(GuildId);
        guild!.Features |= GuildFeatures.Scenes;

        _context.Channels.Add(new Channel
        {
            Id = SceneChannelId, GuildId = GuildId, Name = "the siege", Description = "d",
            Type = ChannelType.Scene, ParentChannelId = ChannelId, CreatedAt = now, UpdatedAt = now,
        });
        _context.Set<SceneState>().Add(SceneState.Create(new CreateSceneStateParams
        {
            ChannelId = SceneChannelId, GuildId = GuildId,
            ParticipantPersonaIds = [], Visibility = SceneVisibility.Cast,
        }));
        await _context.SaveChangesAsync();
    }

    /// <summary>A thread carries no overwrites, so it answers with its parent's resolution.</summary>
    [Test]
    public async Task AThread_AnswersWithItsParentsTrace()
    {
        await SeedAsync();
        await SeedMemberAsync();
        await AddOverwriteAsync(ChannelId, null, EveryoneRoleId, Permissions.None, Permissions.SendMessages);

        var now = DateTimeOffset.UtcNow;
        _context.Channels.Add(new Channel
        {
            Id = "thread-1", GuildId = GuildId, Name = "t", Description = "d",
            Type = ChannelType.Thread, ParentChannelId = ChannelId, CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();

        var parent = await TraceMemberAsync();
        var thread = await TraceMemberAsync("thread-1");

        Assert.That(thread, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(thread!.Permissions, Is.EqualTo(parent!.Permissions));
            Assert.That(thread.Sources[Permissions.SendMessages], Is.EqualTo(PermissionSource.ChannelEveryoneDeny));
        });
    }
}
