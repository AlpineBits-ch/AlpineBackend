using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// What a timed-out member - and a member still sitting on the rules screen - is left holding.
/// </summary>
[TestFixture]
public class TimeoutPermissionTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UserId = "user-1";
    private const string ChannelId = "channel-1";
    private const string RoleId = "role-1";
    private const string MemberId = "member-1";

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

    /// <summary>Seeds one member of one guild holding <paramref name="permissions"/> through one
    /// role, timed out or pending as asked.</summary>
    private async Task SeedAsync(
        Permissions permissions,
        DateTimeOffset? mutedUntil = null,
        bool onboardingPending = false,
        string ownerId = OwnerId)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = ownerId, Name = "Test Guild",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "general", Description = "d",
            Type = ChannelType.Text, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "role", Type = RoleType.None,
            Permissions = permissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            MutedUntil = mutedUntil,
            OnboardingCompletedAt = onboardingPending ? null : DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (onboardingPending)
        {
            _context.Set<GuildOnboardingConfig>().Add(new GuildOnboardingConfig
            {
                GuildId = GuildId, Enabled = true, RulesText = "Be nice", UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    private async Task<Permissions> ResolveAsync()
    {
        var result = await _service.ComputePermissionsForUserAsync(UserId, GuildId);
        return result.Permissions.First(p => p.ChannelId == ChannelId).Permissions;
    }

    private static DateTimeOffset Active => DateTimeOffset.UtcNow.AddMinutes(10);

    // ══════════════════════════════════════════════════════════════════════════
    // Normal - what a timeout leaves behind
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task TimedOut_KeepsViewChannelAndReadMessageHistory()
    {
        await SeedAsync(Role.DefaultEveryonePermissions, mutedUntil: Active);

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.ViewChannel), Is.True,
                "a timed-out member can still see the server");
            Assert.That(perms.HasFlag(Permissions.ReadMessageHistory), Is.True,
                "and can still read its backlog - Discord leaves exactly these two");
        });
    }

    [Test]
    public async Task TimedOut_LosesEverythingElseTheDefaultRoleGranted()
    {
        await SeedAsync(Role.DefaultEveryonePermissions, mutedUntil: Active);

        var perms = await ResolveAsync();

        var expected = Permissions.ViewChannel | Permissions.ReadMessageHistory;
        Assert.That(perms, Is.EqualTo(expected),
            "the retained set is exhaustive, not a floor");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The bits the old hand-written strip list forgot
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task TimedOut_LosesTheSendAdjacentBitsTheOldStripListMissed()
    {
        await SeedAsync(Role.DefaultEveryonePermissions | Permissions.PinMessages, mutedUntil: Active);

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.AttachFiles), Is.False);
            Assert.That(perms.HasFlag(Permissions.EmbedLinks), Is.False);
            Assert.That(perms.HasFlag(Permissions.PinMessages), Is.False);
            Assert.That(perms.HasFlag(Permissions.SendPolls), Is.False);
            Assert.That(perms.HasFlag(Permissions.SendVoiceMessages), Is.False);
        });
    }

    [Test]
    public async Task TimedOut_LosesVoiceTransmissionNotJustConnect()
    {
        await SeedAsync(Permissions.ViewChannel | Permissions.Connect | Permissions.Speak | Permissions.Stream,
            mutedUntil: Active);

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.Connect), Is.False);
            Assert.That(perms.HasFlag(Permissions.Speak), Is.False);
            Assert.That(perms.HasFlag(Permissions.Stream), Is.False);
        });
    }

    [Test]
    public async Task TimedOutModerator_LosesVoiceModerationToo()
    {
        // The behaviour change the closure-over-the-old-list option was rejected for being unable
        // to decide on.
        await SeedAsync(
            Permissions.ViewChannel | Permissions.MoveMembers | Permissions.MuteMembers |
            Permissions.DeafenMembers | Permissions.ManageAnyThread,
            mutedUntil: Active);

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.MoveMembers), Is.False);
            Assert.That(perms.HasFlag(Permissions.MuteMembers), Is.False);
            Assert.That(perms.HasFlag(Permissions.DeafenMembers), Is.False);
            Assert.That(perms.HasFlag(Permissions.ManageAnyThread), Is.False);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Edge - onboarding pending shares the branch
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task OnboardingPending_IsReducedTheSameWay()
    {
        await SeedAsync(Role.DefaultEveryonePermissions, onboardingPending: true);

        var perms = await ResolveAsync();

        Assert.That(perms, Is.EqualTo(Permissions.ViewChannel | Permissions.ReadMessageHistory));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Negative - who and when it does not apply to
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ExpiredTimeout_ChangesNothing()
    {
        await SeedAsync(Role.DefaultEveryonePermissions, mutedUntil: DateTimeOffset.UtcNow.AddMinutes(-1));

        var perms = await ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
            Assert.That(perms.HasFlag(Permissions.AttachFiles), Is.True);
        });
    }

    [Test]
    public async Task NoTimeout_ChangesNothing()
    {
        await SeedAsync(Role.DefaultEveryonePermissions);

        var perms = await ResolveAsync();

        Assert.That(perms.HasFlag(Permissions.SendMessages), Is.True);
    }

    [Test]
    public async Task TimedOutOwner_IsUnaffected()
    {
        await SeedAsync(Permissions.None, mutedUntil: Active, ownerId: UserId);

        var canSend = await _service.CanUserPerformActionAsync(UserId, ChannelId, Permissions.SendMessages);

        Assert.That(canSend, Is.True, "the owner short-circuits before the reduction is reached");
    }

    [Test]
    public async Task TimedOut_ModulePermissionsAreUntouched()
    {
        // Documented limit rather than an oversight - see MuteRetainedPermissions.
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "House", Kind = GuildKind.Household,
            Features = GuildFeaturePresets.For(GuildKind.Household),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Name = "role", Type = RoleType.None,
            Permissions = Permissions.ViewChannel,
            ModulePermissions = ModulePermissions.CompleteChores,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        _context.GuildMembers.Add(new GuildMember
        {
            Id = MemberId, GuildId = GuildId, UserId = UserId, JoinedAt = DateTime.UtcNow,
            MutedUntil = Active, OnboardingCompletedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            SearchValue = $"{UserId}#{GuildId}",
        });
        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-1", RoleId = RoleId, MemberId = MemberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var result = await _service.ComputePermissionsForUserAsync(UserId, GuildId);

        Assert.That(result.BaseModulePermissions.HasFlag(ModulePermissions.CompleteChores), Is.True);
    }
}
