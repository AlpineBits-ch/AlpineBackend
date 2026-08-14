using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Channel = Guild.Domain.Aggregates.Channel;
using Role = Guild.Domain.Aggregates.Role;

namespace Guild.Tests.Services;

/// <summary>
/// The claim that enforcing <see cref="Permissions.UseApplicationCommands"/> at the bot interaction
/// endpoints is invisible to guilds that were working the day before, resolved through the real
/// permission service rather than asserted.
/// </summary>
[TestFixture]
public class UseApplicationCommandsEnforcementTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string MemberId = "member-1";
    private const string ChannelId = "channel-1";
    private const string EveryoneRoleId = "role-everyone";

    private TestGuildContext _context = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _service = new GuildPermissionService(
            new FakeDistributedCache(), _context, NullLogger<GuildPermissionService>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>A guild whose @everyone role carries exactly the shipped defaults, and one plain
    /// member holding nothing else.</summary>
    private async Task SeedOrdinaryMemberAsync(params ChannelPermission[] overwrites)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "general", Description = "d", Type = ChannelType.Text,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Type = RoleType.Everyone, Name = Role.EveryoneRoleName,
            Position = 0,
            Permissions = Role.DefaultEveryonePermissions,
            ModulePermissions = Role.DefaultEveryoneModulePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = EveryoneRoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (overwrites.Length > 0) _context.Set<ChannelPermission>().AddRange(overwrites);

        await _context.SaveChangesAsync();
    }

    private static ChannelPermission Overwrite(string id, string? roleId = null, string? memberId = null,
        Permissions allow = Permissions.None, Permissions deny = Permissions.None) => new()
    {
        Id = id, ChannelId = ChannelId, RoleId = roleId, MemberId = memberId,
        AllowPermissions = allow, DenyPermissions = deny,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ── Normal: nothing breaks for a guild that already worked ────────────────

    [Test]
    public async Task DefaultEveryone_GrantsUseApplicationCommands()
    {
        await SeedOrdinaryMemberAsync();

        var allowed = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.UseApplicationCommands);

        Assert.That(allowed, Is.True,
            "every member of every guild holds this by default, so gating slash commands on it changes nothing");
    }

    [Test]
    public async Task DefaultEveryonePermissions_ContainsTheBit()
    {
        Assert.That(Role.DefaultEveryonePermissions.HasFlag(Permissions.UseApplicationCommands), Is.True,
            "a new guild has to grant it too, or enforcement breaks tomorrow's guilds instead of yesterday's");
    }

    // ── Negative: a deny is a real deny ──────────────────────────────────────

    [Test]
    public async Task EveryoneOverwriteDenyingIt_TakesItAway()
    {
        await SeedOrdinaryMemberAsync(
            Overwrite("cp-1", roleId: EveryoneRoleId, deny: Permissions.UseApplicationCommands));

        var allowed = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.UseApplicationCommands);

        Assert.That(allowed, Is.False);
    }

    [Test]
    public async Task MemberOverwriteDenyingIt_TakesItAwayForThatMemberOnly()
    {
        await SeedOrdinaryMemberAsync(Overwrite("cp-1", memberId: MemberId, deny: Permissions.UseApplicationCommands));

        var canUseCommands = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.UseApplicationCommands);
        var canSend = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.SendMessages);

        Assert.Multiple(() =>
        {
            Assert.That(canUseCommands, Is.False);
            Assert.That(canSend, Is.True, "denying the bot gate must not cost the member the ability to talk");
        });
    }

    // ── Edge: the deny is not undone by anything the member also holds ───────

    [Test]
    public async Task DenyIsNotReImpliedByOtherDefaultPermissions()
    {
        // The failure this guards against is R1's: a bit that several widely-held permissions imply
        // cannot be denied, because the expansion pass puts it straight back.
        await SeedOrdinaryMemberAsync(
            Overwrite("cp-1", roleId: EveryoneRoleId,
                allow: Permissions.ManageChannel | Permissions.ManageAnyThread | Permissions.PinMessages,
                deny: Permissions.UseApplicationCommands));

        var allowed = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.UseApplicationCommands);

        Assert.That(allowed, Is.False);
    }

    [Test]
    public async Task Owner_KeepsItThroughADeny()
    {
        await SeedOrdinaryMemberAsync(
            Overwrite("cp-1", roleId: EveryoneRoleId, deny: Permissions.UseApplicationCommands));

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-owner", GuildId = GuildId, UserId = OwnerId, JoinedAt = DateTime.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{OwnerId}#{GuildId}",
        });
        await _context.SaveChangesAsync();

        var allowed = await _service.CanUserPerformActionAsync(OwnerId, ChannelId, Permissions.UseApplicationCommands);

        Assert.That(allowed, Is.True,
            "Superadmin bypasses overwrites entirely, matching Discord's Administrator");
    }

    [Test]
    public async Task NonMember_DoesNotHoldIt()
    {
        await SeedOrdinaryMemberAsync();

        var allowed = await _service.CanUserPerformActionAsync("user-outsider", ChannelId, Permissions.UseApplicationCommands);

        Assert.That(allowed, Is.False);
    }
}
