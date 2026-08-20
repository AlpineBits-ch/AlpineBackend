using Guild.Application.Endpoints.Channel;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;

namespace Guild.Tests.Services;

/// <summary>
/// The trace walks its own copy of the resolution pipeline. Every configuration here asserts the
/// two agree on every bit the endpoint reports, so a layer added to one and not the other fails
/// here rather than in a readout that quietly disagrees with what the server enforces.
/// </summary>
[TestFixture]
public class ChannelPermissionTraceMatrixTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string CategoryId = "cat-1";
    private const string ChannelId = "chan-1";
    private const string EveryoneRoleId = "role-everyone";
    private const string PlayerRoleId = "role-player";
    private const string MemberId = "member-1";
    private const string UserId = "user-1";

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

    private async Task SeedAsync(
        GuildFeatures features = GuildFeaturePresets.Community,
        DateTimeOffset? mutedUntil = null)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "g", Features = features,
            CreatedAt = now, UpdatedAt = now,
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
            Permissions = Permissions.ViewChannel | Permissions.ReadMessageHistory |
                          Permissions.SendMessages | Permissions.AddReactions,
            CreatedAt = now, UpdatedAt = now,
        });
        _context.Roles.Add(new Role
        {
            Id = PlayerRoleId, GuildId = GuildId, Name = "player", Type = RoleType.None,
            Permissions = Permissions.Connect | Permissions.Speak | Permissions.CreateThreads,
            CreatedAt = now, UpdatedAt = now,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            SearchValue = "USER-1", MutedUntil = mutedUntil, CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-everyone", RoleId = EveryoneRoleId, MemberId = MemberId, CreatedAt = now, UpdatedAt = now,
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-player", RoleId = PlayerRoleId, MemberId = MemberId, CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
    }

    private async Task AddRoleOverwriteAsync(
        string? channelId, string? categoryId, string roleId, Permissions allow, Permissions deny)
    {
        var now = DateTimeOffset.UtcNow;
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = ChannelPermission.GenerateId(),
            ChannelId = channelId, CategoryId = categoryId, RoleId = roleId, MemberId = null,
            AllowPermissions = allow, DenyPermissions = deny,
            AllowModulePermissions = ModulePermissions.None, DenyModulePermissions = ModulePermissions.None,
            CreatedAt = now, UpdatedAt = now,
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
            AllowModulePermissions = ModulePermissions.None, DenyModulePermissions = ModulePermissions.None,
            CreatedAt = now, UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    /// <summary>Every bit the endpoint reports, against the gate every other endpoint calls.</summary>
    private async Task AssertAgreesWithTheGateAsync()
    {
        var traced = await _service.TraceChannelPermissionsAsync(
            ChannelId, new PermissionSubject(PermissionSubjectKind.Member, MemberId));

        Assert.That(traced, Is.Not.Null);

        foreach (var permission in EffectivePermissionsEndpoint.ChannelScoped)
        {
            var enforced = await _service.CanUserPerformActionAsync(UserId, ChannelId, permission);

            Assert.That(traced!.Permissions.HasFlag(permission), Is.EqualTo(enforced),
                $"{permission}: trace says {traced.Permissions.HasFlag(permission)}, gate says {enforced}");
        }
    }

    [Test]
    public async Task RoleUnionAlone_Agrees()
    {
        await SeedAsync();

        await AssertAgreesWithTheGateAsync();
    }

    [Test]
    public async Task AMemberOverwrite_Agrees()
    {
        await SeedAsync();
        await AddMemberOverwriteAsync(ChannelId, null, Permissions.ManageWebhooks, Permissions.SendMessages);

        await AssertAgreesWithTheGateAsync();
    }

    [Test]
    public async Task AMutedMember_Agrees()
    {
        await SeedAsync(mutedUntil: DateTimeOffset.UtcNow.AddHours(1));
        await AddRoleOverwriteAsync(ChannelId, null, PlayerRoleId, Permissions.ManageChannel, Permissions.None);

        await AssertAgreesWithTheGateAsync();
    }

    [Test]
    public async Task ADisabledModule_Agrees()
    {
        // Voice and threads off, so ten of the twenty-eight reported bits are unavailable however
        // generously the roles and overwrites grant them.
        await SeedAsync(GuildFeatures.Moderation);
        await AddRoleOverwriteAsync(ChannelId, null, EveryoneRoleId,
            Permissions.Connect | Permissions.Speak | Permissions.Stream |
            Permissions.CreateThreads | Permissions.SendMessagesInThreads |
            Permissions.ManageAnyThread | Permissions.MuteMembers, Permissions.None);

        await AssertAgreesWithTheGateAsync();
    }

    [Test]
    public async Task ACategoryAndChannelConflict_Agrees()
    {
        await SeedAsync();
        await AddRoleOverwriteAsync(null, CategoryId, EveryoneRoleId, Permissions.ManageChannel, Permissions.AddReactions);
        await AddRoleOverwriteAsync(ChannelId, null, PlayerRoleId, Permissions.AddReactions, Permissions.ManageChannel);
        await AddMemberOverwriteAsync(null, CategoryId, Permissions.None, Permissions.SendMessages);

        await AssertAgreesWithTheGateAsync();
    }

    [Test]
    public async Task AMemberGuildLevelMask_Agrees()
    {
        await SeedAsync();

        var member = await _context.GuildMembers.FindAsync(MemberId);
        member!.AllowPermissions = Permissions.ManageWebhooks;
        member.DenyPermissions = Permissions.AddReactions;
        await _context.SaveChangesAsync();

        await AssertAgreesWithTheGateAsync();
    }
}
