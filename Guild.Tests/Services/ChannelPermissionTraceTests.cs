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
}
