using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// That turning <see cref="Permissions.ReadMessageHistory"/> from a stored bit into an enforced one
/// does not take any existing guild's backlog away from it, and that it can now be withheld.
/// </summary>
[TestFixtureSource(typeof(GuildContextProviders))]
public class ReadMessageHistoryTests(IGuildContextProvider provider)
{
    private const string GuildId = "guld-1";
    private const string OwnerId = "user-owner";
    private const string UserId = "user-1";
    private const string MemberId = "memb-1";
    private const string ChannelId = "chan-1";
    private const string EveryoneRoleId = "role-everyone";

    private MicroserviceContext _context = null!;
    private GuildPermissionService _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = await provider.CreateAsync();
        _service = PermissionTestFactory.Create(new FakeDistributedCache(), _context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>An ordinary guild after the back-fill: one @everyone role carrying the real default
    /// mask, one channel, one member with no RoleMember row - which since R12 is how every member
    /// holds @everyone.</summary>
    private async Task SeedGuildAsync(Permissions? everyonePermissions = null)
    {
        _context.Guilds.Add(new global::Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, Name = "Test Guild", OwnerId = OwnerId, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Name = Role.EveryoneRoleName,
            Type = RoleType.Everyone, Position = 0,
            Permissions = everyonePermissions ?? Role.DefaultEveryonePermissions,
            CreatedAt = Now, UpdatedAt = Now,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "general", Description = "d",
            Type = ChannelType.Text, CreatedAt = Now, UpdatedAt = Now,
        });

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            SearchValue = UserId, CreatedAt = Now, UpdatedAt = Now,
        });

        await _context.SaveChangesAsync();
    }

    private void AddOverwrite(
        string id, string? roleId = null, string? memberId = null,
        Permissions allow = Permissions.None, Permissions deny = Permissions.None) =>
        _context.Set<ChannelPermission>().Add(new ChannelPermission
        {
            Id = id, ChannelId = ChannelId, RoleId = roleId, MemberId = memberId,
            AllowPermissions = allow, DenyPermissions = deny, CreatedAt = Now, UpdatedAt = Now,
        });

    private Task<bool> MayReadHistoryAsync(string userId = UserId) =>
        _service.CanUserPerformActionAsync(userId, ChannelId, Permissions.ReadMessageHistory);

    // ── Normal: nothing changes for an ordinary guild ─────────────────────────

    [Test]
    public async Task DefaultEveryone_MemberCanStillReadHistory()
    {
        await SeedGuildAsync();

        Assert.That(await MayReadHistoryAsync(), Is.True,
            "enforcement must be invisible to a guild on the default @everyone mask");
    }

    [Test]
    public async Task DefaultEveryone_OwnerCanReadHistory()
    {
        // The owner has no GuildMember row in this seed at all - Superadmin is synthesised.
        await SeedGuildAsync();

        Assert.That(await MayReadHistoryAsync(OwnerId), Is.True);
    }

    [Test]
    public async Task DefaultEveryone_MemberWithoutAnEveryoneRoleMemberRow_CanReadHistory()
    {
        // R12's two classes - an installed bot's member row and a federated shadow member - are
        // created without a RoleMember row for @everyone.
        await SeedGuildAsync();

        var permissions = await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        var resolved = permissions.Permissions.Single(p => p.ChannelId == ChannelId).Permissions;

        Assert.That(_context.RoleMembers.Any(), Is.False, "the seed writes no membership row at all");

        Assert.Multiple(() =>
        {
            Assert.That(resolved.HasFlag(Permissions.ReadMessageHistory), Is.True);
            Assert.That(resolved.HasFlag(Permissions.ViewChannel), Is.True);
        });
    }

    // ── The capability the bit was added for ──────────────────────────────────

    [Test]
    public async Task EveryoneChannelDeny_WithholdsTheBacklogButKeepsTheChannel()
    {
        // "Can see the channel but not what was said before they arrived" - the configuration R25
        // records as unrepresentable, and the reason ReadMessageHistory is not simply implied by
        // ViewChannel.
        await SeedGuildAsync();
        AddOverwrite("chpr-1", roleId: EveryoneRoleId, deny: Permissions.ReadMessageHistory);
        await _context.SaveChangesAsync();

        var permissions = await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        var resolved = permissions.Permissions.Single(p => p.ChannelId == ChannelId).Permissions;

        Assert.Multiple(() =>
        {
            Assert.That(resolved.HasFlag(Permissions.ReadMessageHistory), Is.False, "the backlog is withheld");
            Assert.That(resolved.HasFlag(Permissions.ViewChannel), Is.True, "the channel is still visible");
            Assert.That(resolved.HasFlag(Permissions.SendMessages), Is.True, "and still writable");
        });
    }

    [Test]
    public async Task MemberOverwriteAllow_RestoresTheBacklogForOneMember()
    {
        await SeedGuildAsync();
        AddOverwrite("chpr-1", roleId: EveryoneRoleId, deny: Permissions.ReadMessageHistory);
        AddOverwrite("chpr-2", memberId: MemberId, allow: Permissions.ReadMessageHistory);
        await _context.SaveChangesAsync();

        Assert.That(await MayReadHistoryAsync(), Is.True,
            "a member allow is the last word, as it is for every other bit");
    }

    // ── Negative ──────────────────────────────────────────────────────────────

    [Test]
    public async Task EveryoneWithoutTheBit_CannotReadHistory()
    {
        // The un-back-filled state, arranged explicitly.
        await SeedGuildAsync(Role.DefaultEveryonePermissions & ~Permissions.ReadMessageHistory);

        Assert.That(await MayReadHistoryAsync(), Is.False);
    }

    [Test]
    public async Task ViewChannelDeny_AlsoTakesTheBacklog()
    {
        // ReadMessageHistory has no edge in the implication table, so a ViewChannel deny leaves the
        // bit set on the resolved mask.
        await SeedGuildAsync();
        AddOverwrite("chpr-1", roleId: EveryoneRoleId, deny: Permissions.ViewChannel);
        await _context.SaveChangesAsync();

        var permissions = await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        var resolved = permissions.Permissions.Single(p => p.ChannelId == ChannelId).Permissions;

        Assert.Multiple(() =>
        {
            Assert.That(resolved.HasFlag(Permissions.ViewChannel), Is.False);
            Assert.That(resolved.HasFlag(Permissions.ReadMessageHistory), Is.True,
                "not implied either way - the call site is what combines them");
        });
    }
}
