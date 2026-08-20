using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>Service-level behaviour for the household modules: the fairness rotation, ledger
/// balances, guest-role expiry, and the feature gate over each new permission.</summary>
[TestFixture]
public class HouseholdServiceTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "chan-1";
    private const string RoleId = "role-1";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _permissions = null!;
    private ChoreRotationService _rotation = null!;
    private LedgerService _ledger = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _permissions = PermissionTestFactory.Create(_cache, _context);
        _rotation = new ChoreRotationService(_context);
        _ledger = new LedgerService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding helpers ──────────────────────────────────────────────────────

    private async Task SeedGuildAsync(GuildFeatures features = GuildFeaturePresets.Household,
        string ownerId = "owner-1")
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = ownerId, Name = "The Flat", Features = features,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<string> AddMemberAsync(string userId, bool inRotationRole = true,
        DateTimeOffset? roleExpiresAt = null)
    {
        var memberId = $"member-{userId}";
        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId.ToUpperInvariant(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (inRotationRole)
        {
            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{userId}", RoleId = RoleId, MemberId = memberId, ExpiresAt = roleExpiresAt,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
        return memberId;
    }

    private Task AddRoleAsync(Permissions permissions) => AddRoleAsync(ModulePermissions.None, permissions);

    private async Task AddRoleAsync(ModulePermissions permissions = ModulePermissions.None, Permissions corePermissions = Permissions.None)
    {
        _context.Roles.Add(new Role
        {
            Id = RoleId, GuildId = GuildId, Type = RoleType.None, Name = "Flatmates",
            ModulePermissions = permissions, Permissions = corePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<Chore> AddChoreAsync(int effortMinutes = 30)
    {
        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChannelId, GuildId = GuildId, Title = "Bathroom",
            IntervalDays = 7, AnchorAt = DateTimeOffset.UtcNow, EffortMinutes = effortMinutes,
            RotationRoleId = RoleId,
        });
        _context.Chores.Add(chore);
        await _context.SaveChangesAsync();
        return chore;
    }

    private async Task CompleteOccurrenceAsync(Chore chore, string userId, int effortMinutes, int daysAgo)
    {
        var occurrence = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddDays(-daysAgo), userId);
        occurrence.EffortMinutes = effortMinutes;
        occurrence.CompletedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        occurrence.CompletedByUserId = userId;
        _context.ChoreOccurrences.Add(occurrence);
        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Chore rotation - the fairness ledger
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The behaviour that separates this from a plain rota: the next turn goes to
    /// whoever has done the least, not to whoever is next in line. A round-robin silently rewards
    /// skipping, because your turn comes round again regardless.</summary>
    [Test]
    public async Task PickNextAssignee_ChoosesTheLightestLoadedMember()
    {
        await SeedGuildAsync();
        await AddRoleAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        await CompleteOccurrenceAsync(chore, "anna", effortMinutes: 120, daysAgo: 3);
        await CompleteOccurrenceAsync(chore, "ben", effortMinutes: 15, daysAgo: 2);

        Assert.That(await _rotation.PickNextAssigneeAsync(chore), Is.EqualTo("ben"));
    }

    [Test]
    public async Task PickNextAssignee_WeightsByEffortNotCount()
    {
        await SeedGuildAsync();
        await AddRoleAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        // Anna did one long job; Ben did three trivial ones. Anna has carried more of the house.
        await CompleteOccurrenceAsync(chore, "anna", effortMinutes: 90, daysAgo: 5);
        await CompleteOccurrenceAsync(chore, "ben", effortMinutes: 10, daysAgo: 4);
        await CompleteOccurrenceAsync(chore, "ben", effortMinutes: 10, daysAgo: 3);
        await CompleteOccurrenceAsync(chore, "ben", effortMinutes: 10, daysAgo: 2);

        Assert.That(await _rotation.PickNextAssigneeAsync(chore), Is.EqualTo("ben"),
            "three quick jobs shouldn't outweigh one long one");
    }

    [Test]
    public async Task PickNextAssignee_IgnoresWorkOlderThanTheWindow()
    {
        await SeedGuildAsync();
        await AddRoleAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        await CompleteOccurrenceAsync(chore, "anna", effortMinutes: 500, daysAgo: 200);
        await CompleteOccurrenceAsync(chore, "ben", effortMinutes: 20, daysAgo: 1);

        Assert.That(await _rotation.PickNextAssigneeAsync(chore), Is.EqualTo("anna"),
            "credit for work six months ago shouldn't excuse you forever");
    }

    [Test]
    public async Task GetRotationPool_ExcludesExpiredGuestRoles()
    {
        await SeedGuildAsync();
        await AddRoleAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("sitter", roleExpiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        var chore = await AddChoreAsync();

        var pool = await _rotation.GetRotationPoolAsync(chore);

        Assert.That(pool, Is.EquivalentTo(new[] { "anna" }),
            "a house sitter whose access lapsed shouldn't inherit the bins");
    }

    [Test]
    public async Task StageNextOccurrence_IsIdempotentForTheSameSlot()
    {
        await SeedGuildAsync();
        await AddRoleAsync();
        await AddMemberAsync("anna");
        var chore = await AddChoreAsync();

        var first = await _rotation.StageNextOccurrenceAsync(chore);
        await _context.SaveChangesAsync();

        // Both the scheduled message and the reconcile sweep can fire for one slot; exactly one
        // must produce an occurrence.
        chore.NextDueAt = first!.DueAt;
        var second = await _rotation.StageNextOccurrenceAsync(chore);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Null, "the same due date must not generate twice");
        });
    }

    [Test]
    public async Task StageNextOccurrence_PausedChore_GeneratesNothing()
    {
        await SeedGuildAsync();
        await AddRoleAsync();
        await AddMemberAsync("anna");
        var chore = await AddChoreAsync();
        chore.IsPaused = true;

        Assert.That(await _rotation.StageNextOccurrenceAsync(chore), Is.Null);
    }

    [Test]
    public async Task StageNextOccurrence_EmptyRotationPool_GeneratesNothing()
    {
        await SeedGuildAsync();
        await AddRoleAsync();
        var chore = await AddChoreAsync();

        Assert.That(await _rotation.StageNextOccurrenceAsync(chore), Is.Null,
            "a chore nobody is in the pool for should wait, not crash");
    }

    // ══════════════════════════════════════════════════════════════════════════ Ledger balances
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task GetBalances_AlwaysSumToZero()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna", inRotationRole: false);
        await AddMemberAsync("ben", inRotationRole: false);
        await AddMemberAsync("cara", inRotationRole: false);

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = ChannelId, GuildId = GuildId, PayerUserId = "anna",
            Description = "Shop", AmountMinor = 1000, OccurredAt = DateTimeOffset.UtcNow,
            SplitKind = ExpenseSplitKind.Equal, CreatedByUserId = "anna",
        });

        var error = await _ledger.ApplySharesAsync(expense, ExpenseSplitKind.Equal, [], GuildId);
        _context.Expenses.Add(expense);
        await _context.SaveChangesAsync();

        var balances = await _ledger.GetBalancesAsync(ChannelId);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null);
            Assert.That(balances.Sum(b => b.NetMinor), Is.EqualTo(0));
            // Anna paid 1000 and owes 334 of it, so the house owes her 666.
            Assert.That(balances.First(b => b.UserId == "anna").NetMinor, Is.EqualTo(666));
        });
    }

    [Test]
    public async Task GetBalances_SettlementClearsTheDebt()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna", inRotationRole: false);
        await AddMemberAsync("ben", inRotationRole: false);

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = ChannelId, GuildId = GuildId, PayerUserId = "anna",
            Description = "Internet", AmountMinor = 1000, OccurredAt = DateTimeOffset.UtcNow,
            SplitKind = ExpenseSplitKind.Equal, CreatedByUserId = "anna",
        });
        await _ledger.ApplySharesAsync(expense, ExpenseSplitKind.Equal, [], GuildId);
        _context.Expenses.Add(expense);

        _context.Settlements.Add(new Settlement
        {
            Id = Settlement.GenerateId(), ChannelId = ChannelId, GuildId = GuildId,
            FromUserId = "ben", ToUserId = "anna", AmountMinor = 500,
            SettledAt = DateTimeOffset.UtcNow, RecordedByUserId = "ben",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var balances = await _ledger.GetBalancesAsync(ChannelId);

        Assert.That(balances, Is.Empty, "paying your exact half should settle the house completely");
    }

    [Test]
    public async Task ApplyShares_RejectsNonMembers()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna", inRotationRole: false);

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = ChannelId, GuildId = GuildId, PayerUserId = "anna",
            Description = "Shop", AmountMinor = 100, OccurredAt = DateTimeOffset.UtcNow,
            SplitKind = ExpenseSplitKind.Equal, CreatedByUserId = "anna",
        });

        var error = await _ledger.ApplySharesAsync(expense, ExpenseSplitKind.Equal,
            [new Guild.Domain.Services.SplitParticipant("stranger", 1)], GuildId);

        Assert.That(error, Does.Contain("member"));
    }

    [Test]
    public async Task ApplyShares_RejectsDuplicateParticipants()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna", inRotationRole: false);

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = ChannelId, GuildId = GuildId, PayerUserId = "anna",
            Description = "Shop", AmountMinor = 100, OccurredAt = DateTimeOffset.UtcNow,
            SplitKind = ExpenseSplitKind.Equal, CreatedByUserId = "anna",
        });

        var error = await _ledger.ApplySharesAsync(expense, ExpenseSplitKind.Equal,
        [
            new Guild.Domain.Services.SplitParticipant("anna", 1),
            new Guild.Domain.Services.SplitParticipant("anna", 1),
        ], GuildId);

        Assert.That(error, Does.Contain("once"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Guest access
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Expiry is enforced when permissions are resolved, not by a sweep - so a lapsed
    /// guest loses access at the instant it expires even if cleanup hasn't run.</summary>
    [Test]
    public async Task ExpiredRoleMembership_GrantsNothing()
    {
        await SeedGuildAsync();
        await AddRoleAsync(Permissions.ViewChannel | Permissions.SendMessages);
        await AddMemberAsync("sitter", roleExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var allowed = await _permissions.CanUserPerformActionOnGuildAsync(
            "sitter", GuildId, Permissions.SendMessages);

        Assert.That(allowed, Is.False);
    }

    [Test]
    public async Task UnexpiredRoleMembership_StillGrants()
    {
        await SeedGuildAsync();
        await AddRoleAsync(Permissions.ViewChannel | Permissions.SendMessages);
        await AddMemberAsync("sitter", roleExpiresAt: DateTimeOffset.UtcNow.AddDays(5));

        var allowed = await _permissions.CanUserPerformActionOnGuildAsync(
            "sitter", GuildId, Permissions.SendMessages);

        Assert.That(allowed, Is.True);
    }

    [Test]
    public async Task RoleMembershipWithoutExpiry_IsUnaffected()
    {
        await SeedGuildAsync();
        await AddRoleAsync(Permissions.ViewChannel | Permissions.SendMessages);
        await AddMemberAsync("anna");

        Assert.That(await _permissions.CanUserPerformActionOnGuildAsync(
            "anna", GuildId, Permissions.SendMessages), Is.True);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Feature gating over the new permissions
    // ══════════════════════════════════════════════════════════════════════════

    private static readonly (GuildFeatures Feature, ModulePermissions Permission)[] ModuleGatedPermissions =
    [
        (GuildFeatures.Lists, ModulePermissions.AddListItems),
        (GuildFeatures.Chores, ModulePermissions.CompleteChores),
        (GuildFeatures.Ledger, ModulePermissions.AddExpenses),
        (GuildFeatures.Pantry, ModulePermissions.ManagePantry),
        (GuildFeatures.Decisions, ModulePermissions.VoteDecisions),
        (GuildFeatures.GuestAccess, ModulePermissions.ManageGuests),
    ];

    [Test]
    public async Task EachModulePermission_IsStrippedWhenItsFeatureIsOff()
    {
        await SeedGuildAsync(GuildFeaturePresets.Household);
        await AddRoleAsync(Permissions.Superadmin);
        await AddMemberAsync("anna");

        foreach (var (feature, permission) in ModuleGatedPermissions)
        {
            var guild = await _context.Guilds.FindAsync(GuildId);
            guild!.Features = GuildFeaturePresets.Household & ~feature;
            await _context.SaveChangesAsync();
            await _permissions.InvalidateGuildFeaturesCacheAsync(GuildId);
            await _permissions.InvalidateUserPermissionsCacheAsync(GuildId, "anna");

            Assert.That(await _permissions.CanUserPerformActionOnGuildAsync("anna", GuildId, permission),
                Is.False, $"{permission} must be unavailable when {feature} is off");
        }
    }

    /// <summary>Household permissions are absent from a Community guild entirely - which is the
    /// whole point of the exercise: a League server never sees a chore rota.</summary>
    [Test]
    public async Task CommunityGuild_HasNoHouseholdPermissions()
    {
        await SeedGuildAsync(GuildFeaturePresets.Community, ownerId: "anna");
        await AddRoleAsync(Permissions.Superadmin);
        await AddMemberAsync("anna");

        foreach (var (_, permission) in ModuleGatedPermissions)
        {
            Assert.That(await _permissions.CanUserPerformActionOnGuildAsync("anna", GuildId, permission),
                Is.False, $"{permission} must not exist in a Community guild - even for the owner");
        }
    }

    [Test]
    public async Task HouseholdGuild_HasItsModulePermissions()
    {
        await SeedGuildAsync(GuildFeaturePresets.Household);
        await AddRoleAsync(ModulePermissions.ManageLists | ModulePermissions.AddListItems | ModulePermissions.CompleteChores);
        await AddMemberAsync("anna");

        Assert.Multiple(async () =>
        {
            Assert.That(await _permissions.CanUserPerformActionOnGuildAsync("anna", GuildId, ModulePermissions.AddListItems), Is.True);
            Assert.That(await _permissions.CanUserPerformActionOnGuildAsync("anna", GuildId, ModulePermissions.CompleteChores), Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Household seeding
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public void HouseholdGuild_SeedsOneChannelPerModule()
    {
        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "The Flat", OwnerId = "anna", OwnerSearchValue = "ANNA", Kind = GuildKind.Household,
        });

        var types = guild.Categories.SelectMany(c => c.Channels).Select(c => c.Type).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(types, Does.Contain(ChannelType.List));
            Assert.That(types, Does.Contain(ChannelType.Chores));
            Assert.That(types, Does.Contain(ChannelType.Ledger));
            Assert.That(types, Does.Contain(ChannelType.Pantry));
            Assert.That(types, Does.Contain(ChannelType.Decisions));
            Assert.That(types, Does.Contain(ChannelType.Text));
            Assert.That(guild.SystemChannelId, Is.Not.Null,
                "join messages still need a text channel to land in");
        });
    }

    [Test]
    public void CommunityGuild_SeedingIsUnchanged()
    {
        var guild = Guild.Domain.Aggregates.Guild.Create(new CreateGuildParams
        {
            Name = "A Server", OwnerId = "anna", OwnerSearchValue = "ANNA",
        });

        Assert.Multiple(() =>
        {
            Assert.That(guild.Categories.Select(c => c.Name),
                Is.EquivalentTo(new[] { "Text Channels", "Voice Channels" }));
            Assert.That(guild.Categories.SelectMany(c => c.Channels).Select(c => c.Type),
                Is.EquivalentTo(new[] { ChannelType.Text, ChannelType.Voice }));
        });
    }
}
