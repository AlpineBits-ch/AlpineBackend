using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>The Waiting-on-you tab.</summary>
[TestFixture]
public class InboxTaskServiceTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string ChoresChannelId = "chan-chores";
    private const string ListChannelId = "chan-list";
    private const string DecisionsChannelId = "chan-decisions";
    private const string LedgerChannelId = "chan-ledger";
    private const string MealsChannelId = "chan-meals";
    private const string MaintenanceChannelId = "chan-maintenance";
    private const string EveryoneRoleId = "role-everyone";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private InboxTaskService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());

        var permissions = new GuildPermissionService(
            _cache, _context, NullLogger<GuildPermissionService>.Instance);

        _service = new InboxTaskService(_context, permissions);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedAsync(GuildFeatures features = GuildFeaturePresets.Household)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "The Flat", Features = features,
            Kind = GuildKind.Household,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Type = RoleType.Everyone, Name = "Everyone",
            Permissions = Role.DefaultEveryonePermissions,
            ModulePermissions = Role.DefaultEveryoneModulePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var (id, type) in new[]
                 {
                     (ChoresChannelId, ChannelType.Chores),
                     (ListChannelId, ChannelType.List),
                     (DecisionsChannelId, ChannelType.Decisions),
                     (LedgerChannelId, ChannelType.Ledger),
                     (MealsChannelId, ChannelType.Meals),
                     (MaintenanceChannelId, ChannelType.Maintenance),
                 })
        {
            _context.Channels.Add(new Channel
            {
                Id = id, GuildId = GuildId, Name = id, Type = type,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>Adds a member.</summary>
    private async Task AddMemberAsync(string userId, bool canSee = true)
    {
        var memberId = $"member-{userId}";

        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId.ToUpperInvariant(),
            DenyPermissions = canSee ? Permissions.None : Permissions.ViewChannel,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rm-{userId}", RoleId = EveryoneRoleId, MemberId = memberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private async Task<ChoreOccurrence> AddChoreForAsync(string userId, TimeSpan dueIn, int graceHours = 24)
    {
        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChoresChannelId, GuildId = GuildId, Title = "Bins",
            AnchorAt = DateTimeOffset.UtcNow, GraceHours = graceHours,
        });
        _context.Chores.Add(chore);

        var occurrence = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow + dueIn, userId);
        _context.ChoreOccurrences.Add(occurrence);

        await _context.SaveChangesAsync();
        return occurrence;
    }

    private async Task<Decision> AddOpenDecisionAsync(string title = "Cat", DateTimeOffset? closesAt = null)
    {
        var decision = Decision.Create(new CreateDecisionParams
        {
            ChannelId = DecisionsChannelId, GuildId = GuildId, Title = title,
            CreatedByUserId = OwnerId, ClosesAt = closesAt,
        });
        _context.Decisions.Add(decision);
        await _context.SaveChangesAsync();
        return decision;
    }

    private async Task<ListItem> AddAssignedItemAsync(string userId, string text = "Lightbulbs")
    {
        var item = ListItem.Create(new CreateListItemParams
        {
            ChannelId = ListChannelId, GuildId = GuildId, Text = text,
            AssigneeUserId = userId, AddedByUserId = OwnerId, Position = 0,
        });
        _context.ListItems.Add(item);
        await _context.SaveChangesAsync();
        return item;
    }

    /// <summary>A bill on the ledger channel.</summary>
    private async Task<BillOccurrence> AddBillAsync(
        DateTimeOffset dueAt, string description = "Rent",
        params (string UserId, decimal ShareValue)[] shares)
    {
        var template = RecurringExpense.Create(new CreateRecurringExpenseParams
        {
            ChannelId = LedgerChannelId, GuildId = GuildId, Description = description,
            AmountMinor = 120_000, PayerUserId = OwnerId, CreatedByUserId = OwnerId,
            AnchorAt = dueAt,
        });

        foreach (var (userId, shareValue) in shares)
        {
            template.Shares.Add(new RecurringExpenseShare
            {
                RecurringExpenseId = template.Id, UserId = userId, ShareValue = shareValue,
            });
        }

        _context.Add(template);

        var occurrence = BillOccurrence.Create(template, dueAt);
        _context.Add(occurrence);

        await _context.SaveChangesAsync();
        return occurrence;
    }

    private async Task<MealPlanEntry> AddMealAsync(
        DateOnly date, string? cookUserId, string freeText = "Curry")
    {
        var entry = MealPlanEntry.Create(new CreateMealPlanEntryParams
        {
            ChannelId = MealsChannelId, GuildId = GuildId, Date = date, Slot = MealSlot.Dinner,
            FreeText = freeText, CookUserId = cookUserId, CreatedByUserId = OwnerId,
        });

        _context.Add(entry);
        await _context.SaveChangesAsync();
        return entry;
    }

    private async Task<MaintenanceAsset> AddAssetAsync(
        string name, AssetStatus status = AssetStatus.Ok, DateTimeOffset? nextServiceAt = null)
    {
        var asset = MaintenanceAsset.Create(new CreateMaintenanceAssetParams
        {
            ChannelId = MaintenanceChannelId, GuildId = GuildId, Name = name, AddedByUserId = OwnerId,
        });

        asset.Status = status;
        asset.NextServiceAt = nextServiceAt;

        _context.Add(asset);
        await _context.SaveChangesAsync();
        return asset;
    }

    // ══════════════════════════════════════════════════════════════════════════ The three original
    // sources ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ChoreDueToday_IsATask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        var occurrence = await AddChoreForAsync("anna", TimeSpan.FromHours(2));

        var page = await _service.GetTasksAsync("anna", 25);

        Assert.Multiple(() =>
        {
            Assert.That(page.Tasks, Has.Count.EqualTo(1));
            Assert.That(page.Tasks[0].Kind, Is.EqualTo(InboxTaskKind.ChoreDue));
            Assert.That(page.Tasks[0].TargetId, Is.EqualTo(occurrence.Id));
            Assert.That(page.Tasks[0].Title, Is.EqualTo("Bins"));
            Assert.That(page.Tasks[0].IsOverdue, Is.False);
        });
    }

    [TestCase(-2, false, Description = "two hours late, well inside a day of grace")]
    [TestCase(-30, true, Description = "past the grace period, so the board says overdue")]
    public async Task AChoreIsOverdueOnlyOnceItsGracePeriodHasRunOut(int dueHoursFromNow, bool expected)
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddChoreForAsync("anna", TimeSpan.FromHours(dueHoursFromNow), graceHours: 24);

        var page = await _service.GetTasksAsync("anna", 25);

        Assert.That(page.Tasks[0].IsOverdue, Is.EqualTo(expected));
    }

    [Test]
    public async Task SomebodyElsesChore_IsNotYourTask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddChoreForAsync("ben", TimeSpan.FromHours(2));

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task ACompletedChore_LeavesTheInbox()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        var occurrence = await AddChoreForAsync("anna", TimeSpan.FromHours(2));

        occurrence.CompletedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task AnUnvotedDecision_IsATask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        var decision = await AddOpenDecisionAsync();

        var page = await _service.GetTasksAsync("anna", 25);

        Assert.Multiple(() =>
        {
            Assert.That(page.Tasks[0].Kind, Is.EqualTo(InboxTaskKind.DecisionVote));
            Assert.That(page.Tasks[0].TargetId, Is.EqualTo(decision.Id));
            Assert.That(page.Tasks[0].Subtitle, Is.EqualTo("Waiting on your vote"));
        });
    }

    [Test]
    public async Task ADecisionYouHaveVotedOn_LeavesTheInbox()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        var decision = await AddOpenDecisionAsync();

        decision.Votes.Add(new DecisionVote
        {
            DecisionId = decision.Id, UserId = "anna", Kind = DecisionVoteKind.Abstain,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty,
            "abstaining is an answer");
    }

    [Test]
    public async Task AClosedDecision_IsNotATask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var decision = await AddOpenDecisionAsync();
        decision.Status = DecisionStatus.Cancelled;
        await _context.SaveChangesAsync();

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task AListItemAssignedToYou_IsATask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        var item = await AddAssignedItemAsync("anna");

        var page = await _service.GetTasksAsync("anna", 25);

        Assert.Multiple(() =>
        {
            Assert.That(page.Tasks[0].Kind, Is.EqualTo(InboxTaskKind.ListAssignment));
            Assert.That(page.Tasks[0].TargetId, Is.EqualTo(item.Id));
            Assert.That(page.Tasks[0].DueAt, Is.Null, "a shopping line has no deadline");
            Assert.That(page.Tasks[0].IsOverdue, Is.False);
        });
    }

    [Test]
    public async Task ACheckedListItem_LeavesTheInbox()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var item = await AddAssignedItemAsync("anna");
        item.Check("anna");
        await _context.SaveChangesAsync();

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task AnUnassignedListItem_IsNobodysTask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        _context.ListItems.Add(ListItem.Create(new CreateListItemParams
        {
            ChannelId = ListChannelId, GuildId = GuildId, Text = "Milk", AddedByUserId = "anna", Position = 0,
        }));
        await _context.SaveChangesAsync();

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ Bills
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ABillYouHaveAShareIn_IsATask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var occurrence = await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2));

        var page = await _service.GetTasksAsync("anna", 25);

        Assert.Multiple(() =>
        {
            Assert.That(page.Tasks, Has.Count.EqualTo(1));
            Assert.That(page.Tasks[0].Kind, Is.EqualTo(InboxTaskKind.BillDue));
            Assert.That(page.Tasks[0].TargetId, Is.EqualTo(occurrence.Id), "the occurrence, not the schedule");
            Assert.That(page.Tasks[0].Title, Is.EqualTo("Rent"));
            Assert.That(page.Tasks[0].DueAt, Is.EqualTo(occurrence.DueAt));
            Assert.That(page.Tasks[0].IsOverdue, Is.False);
        });
    }

    /// <summary>The empty-shares Equal convention is the common case - rent split across the flat -
    /// and resolving it against the house as it is now is what lets somebody who moved in last month
    /// be told about it without anybody editing the schedule.</summary>
    [Test]
    public async Task ABillSplitAcrossEverybody_ReachesAMemberWhoIsNamedNowhere()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("newcomer");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2));

        Assert.That((await _service.GetTasksAsync("newcomer", 25)).Tasks, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ABillSplitAcrossSomebodyElse_IsNotYourTask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2), "Ben's gym", ("ben", 1));

        Assert.Multiple(async () =>
        {
            Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
            Assert.That((await _service.GetTasksAsync("ben", 25)).Tasks, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ABillFurtherOutThanItsLeadWindow_IsNotYetWaitingOnAnybody()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(20));

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [TestCase(BillStatus.Posted, Description = "it is in the balances now, and the balance is not a task")]
    [TestCase(BillStatus.Skipped, Description = "the house decided not to pay it")]
    public async Task ABillThatHasBeenDealtWith_LeavesTheInbox(BillStatus status)
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var occurrence = await AddBillAsync(DateTimeOffset.UtcNow.AddDays(1));
        occurrence.Status = status;
        await _context.SaveChangesAsync();

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task ALateBill_IsOverdueTheMomentItsDateHasPassed()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddBillAsync(DateTimeOffset.UtcNow.AddHours(-2));

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks[0].IsOverdue, Is.True,
            "a bill has no grace period - the money either moved or it did not");
    }

    // ══════════════════════════════════════════════════════════════════════════ Cooking
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AMealYouAreCookingToday_IsATask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var entry = await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "anna");

        var page = await _service.GetTasksAsync("anna", 25);

        Assert.Multiple(() =>
        {
            Assert.That(page.Tasks[0].Kind, Is.EqualTo(InboxTaskKind.CookingToday));
            Assert.That(page.Tasks[0].TargetId, Is.EqualTo(entry.Id));
            Assert.That(page.Tasks[0].Title, Is.EqualTo("Curry"), "free text reads as a title");
            Assert.That(page.Tasks[0].Subtitle, Is.EqualTo("You're cooking"));
            Assert.That(page.Tasks[0].DueAt, Is.Not.Null, "a planned date is a deadline");
        });
    }

    /// <summary>Tonight's dinner must not be reported as already late at one minute past midnight,
    /// which is exactly what an unadorned date-as-deadline would claim.</summary>
    [Test]
    public async Task AMealPlannedForToday_IsNotOverdueDuringToday()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "anna");

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks[0].IsOverdue, Is.False);
    }

    [Test]
    public async Task AMealYouAreCookingTomorrow_IsAlreadyWorthKnowing()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), "anna");

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Has.Count.EqualTo(1),
            "you shop for tomorrow's dinner today");
    }

    [Test]
    public async Task AMealFurtherOutThanTomorrow_IsNotYetYourProblem()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), "anna");

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task AMealWithNoCook_OrSomebodyElsesCook_IsNotYourTask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AddMealAsync(today, cookUserId: null);
        await AddMealAsync(today, "ben");

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ Maintenance
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AnAssetOverdueAService_IsATask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var asset = await AddAssetAsync("Boiler", nextServiceAt: DateTimeOffset.UtcNow.AddDays(-4));

        var page = await _service.GetTasksAsync("anna", 25);

        Assert.Multiple(() =>
        {
            Assert.That(page.Tasks[0].Kind, Is.EqualTo(InboxTaskKind.MaintenanceDue));
            Assert.That(page.Tasks[0].TargetId, Is.EqualTo(asset.Id));
            Assert.That(page.Tasks[0].Title, Is.EqualTo("Boiler"));
            Assert.That(page.Tasks[0].Subtitle, Is.EqualTo("Due for a service"));
            Assert.That(page.Tasks[0].DueAt, Is.EqualTo(asset.NextServiceAt));
            Assert.That(page.Tasks[0].IsOverdue, Is.True);
        });
    }

    /// <summary>A broken machine has no deadline.</summary>
    [Test]
    public async Task ABrokenAsset_IsATaskWithNoDeadline()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAssetAsync("Washing machine", AssetStatus.Broken,
            nextServiceAt: DateTimeOffset.UtcNow.AddDays(-4));

        var page = await _service.GetTasksAsync("anna", 25);

        Assert.Multiple(() =>
        {
            Assert.That(page.Tasks[0].Subtitle, Is.EqualTo("Marked as broken"));
            Assert.That(page.Tasks[0].DueAt, Is.Null, "broken outranks a service date it also has");
            Assert.That(page.Tasks[0].IsOverdue, Is.False);
        });
    }

    [Test]
    public async Task AnAssetWithItsServiceStillAhead_IsNobodysTask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAssetAsync("Boiler", nextServiceAt: DateTimeOffset.UtcNow.AddDays(30));
        await AddAssetAsync("Sofa");

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task AnAssetMerelyNeedingAttention_IsNotAnInboxTask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAssetAsync("Tumble dryer", AssetStatus.NeedsAttention);

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty,
            "the attention board says so; the inbox is for broken and overdue");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Ordering, filtering and the badge
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task DeadlinesSortAheadOfEverythingUndated()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAssignedItemAsync("anna");
        await AddOpenDecisionAsync("Sofa", DateTimeOffset.UtcNow.AddDays(3));
        await AddChoreForAsync("anna", TimeSpan.FromHours(1));

        var kinds = (await _service.GetTasksAsync("anna", 25)).Tasks.Select(t => t.Kind).ToList();

        Assert.That(kinds, Is.EqualTo(new[]
        {
            InboxTaskKind.ChoreDue,        // due in an hour
            InboxTaskKind.DecisionVote,    // closes in three days
            InboxTaskKind.ListAssignment,  // no deadline at all
        }));
    }

    /// <summary>Every kind in one list, to pin the merge order down across the sources.</summary>
    [Test]
    public async Task EveryKindMergesIntoOneDeadlineOrder()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAssignedItemAsync("anna");
        await AddAssetAsync("Washing machine", AssetStatus.Broken);
        await AddOpenDecisionAsync("Sofa", DateTimeOffset.UtcNow.AddDays(3));
        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2));
        await AddChoreForAsync("anna", TimeSpan.FromHours(1));
        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "anna");

        var kinds = (await _service.GetTasksAsync("anna", 25)).Tasks.Select(t => t.Kind).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(kinds.Take(4), Is.EqualTo(new[]
            {
                InboxTaskKind.CookingToday,   // midnight today
                InboxTaskKind.ChoreDue,       // due in an hour
                InboxTaskKind.BillDue,        // due in two days
                InboxTaskKind.DecisionVote,   // closes in three days
            }));

            Assert.That(kinds.Skip(4), Is.EquivalentTo(new[]
            {
                InboxTaskKind.MaintenanceDue,   // broken, so no deadline at all
                InboxTaskKind.ListAssignment,   // a shopping line never had one
            }), "the undated tail sorts by age, which two rows written in the same second do not pin");
        });
    }

    [TestCase(GuildFeatures.Ledger, InboxTaskKind.BillDue)]
    [TestCase(GuildFeatures.Meals, InboxTaskKind.CookingToday)]
    [TestCase(GuildFeatures.Maintenance, InboxTaskKind.MaintenanceDue)]
    public async Task ANewModuleSwitchedOff_TakesItsTasksWithIt(
        GuildFeatures feature, InboxTaskKind kind)
    {
        await SeedAsync(GuildFeaturePresets.Household & ~feature);
        await AddMemberAsync("anna");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2));
        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "anna");
        await AddAssetAsync("Washing machine", AssetStatus.Broken);

        var kinds = (await _service.GetTasksAsync("anna", 25)).Tasks.Select(t => t.Kind).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(kinds, Does.Not.Contain(kind));
            Assert.That(kinds, Has.Count.EqualTo(2), "the other two modules are unaffected");
        });
    }

    [Test]
    public async Task Count_IncludesTheNewKinds()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2));
        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "anna");
        await AddAssetAsync("Washing machine", AssetStatus.Broken);

        Assert.That(await _service.CountAsync("anna"), Is.EqualTo(3));
    }

    /// <summary>The one thing deliberately not here.</summary>
    [Test]
    public async Task OwingTheHouseMoney_IsNotATask()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = LedgerChannelId, GuildId = GuildId, PayerUserId = "ben",
            Description = "Shop", AmountMinor = 6_000, OccurredAt = DateTimeOffset.UtcNow,
            SplitKind = ExpenseSplitKind.Equal, CreatedByUserId = "ben",
        });
        expense.Shares.Add(new ExpenseShare { ExpenseId = expense.Id, UserId = "anna", AmountMinor = 3_000 });
        expense.Shares.Add(new ExpenseShare { ExpenseId = expense.Id, UserId = "ben", AmountMinor = 3_000 });

        _context.Expenses.Add(expense);
        await _context.SaveChangesAsync();

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    /// <summary>A chore assignment survives losing access to the channel it lives in, the same way
    /// a read state does - which is why the inbox re-checks rather than trusting the row.</summary>
    [Test]
    public async Task AChannelTheCallerCanNoLongerSee_DropsOutOfTheInbox()
    {
        await SeedAsync();
        await AddMemberAsync("anna", canSee: false);
        await AddChoreForAsync("anna", TimeSpan.FromHours(2));

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    /// <summary>The same boundary for the new sources.</summary>
    [Test]
    public async Task TheNewKinds_AlsoRespectChannelVisibility()
    {
        await SeedAsync();
        await AddMemberAsync("anna", canSee: false);

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2));
        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "anna");
        await AddAssetAsync("Washing machine", AssetStatus.Broken);

        Assert.That((await _service.GetTasksAsync("anna", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task AModuleSwitchedOff_TakesItsTasksWithIt()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Chores);
        await AddMemberAsync("anna");
        await AddChoreForAsync("anna", TimeSpan.FromHours(2));
        await AddAssignedItemAsync("anna");

        var kinds = (await _service.GetTasksAsync("anna", 25)).Tasks.Select(t => t.Kind).ToList();

        Assert.That(kinds, Is.EqualTo(new[] { InboxTaskKind.ListAssignment }));
    }

    [Test]
    public async Task SomebodyElsesGuild_NeverAppears()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddChoreForAsync("anna", TimeSpan.FromHours(2));

        Assert.That((await _service.GetTasksAsync("stranger", 25)).Tasks, Is.Empty);
    }

    [Test]
    public async Task Truncated_ReportsThatMoreAreWaiting()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        for (var i = 0; i < 4; i++) await AddAssignedItemAsync("anna", $"item-{i}");

        var page = await _service.GetTasksAsync("anna", 2);

        Assert.Multiple(() =>
        {
            Assert.That(page.Tasks, Has.Count.EqualTo(2));
            Assert.That(page.Truncated, Is.True);
        });
    }

    [Test]
    public async Task Count_MatchesWhatTheTabWouldShow()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddChoreForAsync("anna", TimeSpan.FromHours(2));
        await AddOpenDecisionAsync();
        await AddAssignedItemAsync("anna");

        Assert.That(await _service.CountAsync("anna"), Is.EqualTo(3));
    }

    [Test]
    public async Task Breadcrumb_CarriesTheGuildSoAClientNeedsNoCache()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddAssignedItemAsync("anna");

        var breadcrumb = (await _service.GetTasksAsync("anna", 25)).Tasks[0].Breadcrumb;

        Assert.Multiple(() =>
        {
            Assert.That(breadcrumb.GuildName, Is.EqualTo("The Flat"));
            Assert.That(breadcrumb.ChannelId, Is.EqualTo(ListChannelId));
            Assert.That(breadcrumb.GuildIconUrl, Is.EqualTo(InboxService.GuildIconUrl(GuildId)));
        });
    }
}
