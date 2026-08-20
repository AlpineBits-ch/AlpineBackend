using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Endpoints.Household;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

namespace Guild.Tests.Services;

/// <summary>The one-request home digest.</summary>
[TestFixture]
public class HouseholdDigestServiceTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string ChoresChannelId = "chan-chores";
    private const string ListChannelId = "chan-list";
    private const string LedgerChannelId = "chan-ledger";
    private const string PantryChannelId = "chan-pantry";
    private const string DecisionsChannelId = "chan-decisions";
    private const string MealsChannelId = "chan-meals";
    private const string MaintenanceChannelId = "chan-maintenance";
    private const string EveryoneRoleId = "role-everyone";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _permissions = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _permissions = PermissionTestFactory.Create(_cache, _context);
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
                     (LedgerChannelId, ChannelType.Ledger),
                     (PantryChannelId, ChannelType.Pantry),
                     (DecisionsChannelId, ChannelType.Decisions),
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

    private HouseholdDigestService Build(params (string UserId, string Kind)[] homeStatuses) =>
        new(_context, _permissions, new LedgerService(_context),
            new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId, homeStatuses)));

    /// <summary>A bill on the ledger channel.</summary>
    private async Task<BillOccurrence> AddBillAsync(
        DateTimeOffset dueAt, long? amountMinor, string description = "Rent",
        params (string UserId, decimal ShareValue)[] shares)
    {
        var template = RecurringExpense.Create(new CreateRecurringExpenseParams
        {
            ChannelId = LedgerChannelId, GuildId = GuildId, Description = description,
            AmountMinor = amountMinor, PayerUserId = "anna", CreatedByUserId = "anna",
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
        DateOnly date, string? cookUserId, string freeText = "Curry", MealSlot slot = MealSlot.Dinner)
    {
        var entry = MealPlanEntry.Create(new CreateMealPlanEntryParams
        {
            ChannelId = MealsChannelId, GuildId = GuildId, Date = date, Slot = slot,
            FreeText = freeText, CookUserId = cookUserId, CreatedByUserId = "anna",
        });

        _context.Add(entry);
        await _context.SaveChangesAsync();
        return entry;
    }

    private async Task<MaintenanceAsset> AddAssetAsync(
        string name, AssetStatus status = AssetStatus.Ok,
        DateTimeOffset? nextServiceAt = null, DateTimeOffset? warrantyUntil = null)
    {
        var asset = MaintenanceAsset.Create(new CreateMaintenanceAssetParams
        {
            ChannelId = MaintenanceChannelId, GuildId = GuildId, Name = name,
            WarrantyUntil = warrantyUntil, AddedByUserId = "anna",
        });

        asset.Status = status;
        asset.NextServiceAt = nextServiceAt;

        _context.Add(asset);
        await _context.SaveChangesAsync();
        return asset;
    }

    private async Task<MemberAbsence> AddAbsenceAsync(
        string userId, DateTimeOffset startAt, DateTimeOffset endAt, string? note = null)
    {
        var absence = MemberAbsence.Create(new CreateMemberAbsenceParams
        {
            GuildId = GuildId, UserId = userId, StartAt = startAt, EndAt = endAt,
            Note = note, CreatedByUserId = userId,
        });

        _context.Add(absence);
        await _context.SaveChangesAsync();
        return absence;
    }

    // ══════════════════════════════════════════════════════════════════════════ Contents
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Chores_SurfacesYourOwnAndCountsTheHousesOverdue()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChoresChannelId, GuildId = GuildId, Title = "Bins",
            AnchorAt = DateTimeOffset.UtcNow, GraceHours = 0,
        });
        _context.Chores.Add(chore);

        _context.ChoreOccurrences.Add(ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddHours(-2), "anna"));
        _context.ChoreOccurrences.Add(ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddHours(-3), "ben"));
        await _context.SaveChangesAsync();

        var digest = await Build().BuildAsync(GuildId, "anna");

        Assert.Multiple(() =>
        {
            Assert.That(digest.Chores!.Mine.Select(c => c.AssignedUserId), Is.EquivalentTo(new[] { "anna" }));
            Assert.That(digest.Chores.Mine[0].Title, Is.EqualTo("Bins"));
            Assert.That(digest.Chores.MineOverdueCount, Is.EqualTo(1));
            Assert.That(digest.Chores.HouseOverdueCount, Is.EqualTo(2), "the whole house, not just you");
        });
    }

    [Test]
    public async Task Chores_IgnoresAnythingFurtherOutThanADay()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChoresChannelId, GuildId = GuildId, Title = "Windows",
            AnchorAt = DateTimeOffset.UtcNow,
        });
        _context.Chores.Add(chore);
        _context.ChoreOccurrences.Add(ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddDays(4), "anna"));
        await _context.SaveChangesAsync();

        var digest = await Build().BuildAsync(GuildId, "anna");

        Assert.That(digest.Chores!.Mine, Is.Empty, "a chore due on Friday is not Tuesday's problem");
    }

    [Test]
    public async Task Lists_CountsTheOpenItemsAndPreviewsTheTop()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        for (var i = 0; i < 8; i++)
        {
            _context.ListItems.Add(ListItem.Create(new CreateListItemParams
            {
                ChannelId = ListChannelId, GuildId = GuildId, Text = $"item-{i}",
                AddedByUserId = "anna", Position = i,
            }));
        }

        var bought = ListItem.Create(new CreateListItemParams
        {
            ChannelId = ListChannelId, GuildId = GuildId, Text = "bought", AddedByUserId = "anna", Position = 99,
        });
        bought.Check("anna");
        _context.ListItems.Add(bought);

        await _context.SaveChangesAsync();

        var digest = await Build().BuildAsync(GuildId, "anna");
        var list = digest.Lists!.Single();

        Assert.Multiple(() =>
        {
            Assert.That(list.OpenCount, Is.EqualTo(8), "checked lines are not waiting on anyone");
            Assert.That(list.Preview, Has.Count.EqualTo(5), "a glance, not the board");
            Assert.That(list.Preview[0].Text, Is.EqualTo("item-0"));
        });
    }

    [Test]
    public async Task Ledger_ReportsTheCallersOwnNetPosition()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = LedgerChannelId, GuildId = GuildId, PayerUserId = "anna",
            Description = "Shop", AmountMinor = 6000, OccurredAt = DateTimeOffset.UtcNow,
            SplitKind = ExpenseSplitKind.Equal, CreatedByUserId = "anna",
        });
        expense.Shares.Add(new ExpenseShare { ExpenseId = expense.Id, UserId = "anna", AmountMinor = 3000 });
        expense.Shares.Add(new ExpenseShare { ExpenseId = expense.Id, UserId = "ben", AmountMinor = 3000 });
        _context.Expenses.Add(expense);
        await _context.SaveChangesAsync();

        var annaDigest = await Build().BuildAsync(GuildId, "anna");
        var benDigest = await Build().BuildAsync(GuildId, "ben");

        Assert.Multiple(() =>
        {
            Assert.That(annaDigest.Ledger!.Single().MyNetMinor, Is.EqualTo(3000), "the house owes Anna");
            Assert.That(benDigest.Ledger!.Single().MyNetMinor, Is.EqualTo(-3000));
            Assert.That(annaDigest.Ledger.Single().Currency, Is.EqualTo("CHF"));
        });
    }

    [Test]
    public async Task Decisions_ListsOnlyTheOnesYouHaveNotVotedOn()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var voted = Decision.Create(new CreateDecisionParams
        {
            ChannelId = DecisionsChannelId, GuildId = GuildId, Title = "Cat", CreatedByUserId = "anna",
        });
        voted.Votes.Add(new DecisionVote
        {
            DecisionId = voted.Id, UserId = "anna", Kind = DecisionVoteKind.Support,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var open = Decision.Create(new CreateDecisionParams
        {
            ChannelId = DecisionsChannelId, GuildId = GuildId, Title = "Sofa", CreatedByUserId = "anna",
        });

        _context.Decisions.AddRange(voted, open);
        await _context.SaveChangesAsync();

        var digest = await Build().BuildAsync(GuildId, "anna");

        Assert.Multiple(() =>
        {
            Assert.That(digest.Decisions!.OpenCount, Is.EqualTo(2));
            Assert.That(digest.Decisions.AwaitingMyVote.Select(d => d.Title), Is.EquivalentTo(new[] { "Sofa" }));
        });
    }

    [Test]
    public async Task Pantry_AppliesEachChannelsOwnHorizon()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        _context.PantryItems.Add(PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = PantryChannelId, GuildId = GuildId, Name = "Milk", Quantity = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), AddedByUserId = "anna",
        }));
        _context.PantryItems.Add(PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = PantryChannelId, GuildId = GuildId, Name = "Jam", Quantity = 1,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(60), AddedByUserId = "anna",
        }));
        await _context.SaveChangesAsync();

        var digest = await Build().BuildAsync(GuildId, "anna");

        Assert.Multiple(() =>
        {
            Assert.That(digest.Pantry!.ExpiringCount, Is.EqualTo(1));
            Assert.That(digest.Pantry.Soonest.Single().Name, Is.EqualTo("Milk"));
        });
    }

    [Test]
    public async Task HomeStatus_IsIncludedWhenPresenceIsOn()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var digest = await Build(("ben", "Out")).BuildAsync(GuildId, "anna");

        Assert.That(digest.HomeStatus!.Single().Kind, Is.EqualTo(HomeStatusKind.Out));
    }

    // ══════════════════════════════════════════════════════════════════════════ Bills
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Bills_ListWhatIsComingAndCountWhatIsLate()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(3), 120_000, "Rent");
        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(-2), 8_000, "Internet");

        var bills = (await Build().BuildAsync(GuildId, "anna")).Bills!;

        Assert.Multiple(() =>
        {
            Assert.That(bills.DueSoon.Select(b => b.Description),
                Is.EqualTo(new[] { "Internet", "Rent" }), "soonest first, and late is soonest");
            Assert.That(bills.OverdueCount, Is.EqualTo(1));
            Assert.That(bills.NeedsAmountCount, Is.Zero);
        });
    }

    [Test]
    public async Task Bills_IgnoreAnythingFurtherOutThanAFortnight()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(40), 120_000);

        Assert.That((await Build().BuildAsync(GuildId, "anna")).Bills!.DueSoon, Is.Empty);
    }

    [Test]
    public async Task Bills_AlreadyPostedOnesHaveStoppedWaitingOnAnybody()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var occurrence = await AddBillAsync(DateTimeOffset.UtcNow.AddDays(-1), 8_000);
        occurrence.Status = BillStatus.Posted;
        await _context.SaveChangesAsync();

        var bills = (await Build().BuildAsync(GuildId, "anna")).Bills!;

        Assert.Multiple(() =>
        {
            Assert.That(bills.DueSoon, Is.Empty);
            Assert.That(bills.OverdueCount, Is.Zero);
        });
    }

    /// <summary>The convention worth being exact about: an Equal split that named nobody means
    /// everybody in the house, resolved now. Three flatmates and 10.00 divides 334/333/333, and the
    /// caller has to be told their own row rather than the average.</summary>
    [Test]
    public async Task Bills_MyShareOfAnEmptySharesEqualSplit_IsResolvedAcrossTheWholeHouse()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(1), 1_000);

        var anna = (await Build().BuildAsync(GuildId, "anna")).Bills!.DueSoon.Single();
        var ben = (await Build().BuildAsync(GuildId, "ben")).Bills!.DueSoon.Single();
        var cara = (await Build().BuildAsync(GuildId, "cara")).Bills!.DueSoon.Single();

        Assert.Multiple(() =>
        {
            Assert.That(anna.MyShareMinor, Is.EqualTo(334), "the remainder goes to the first by ordinal id");
            Assert.That(ben.MyShareMinor, Is.EqualTo(333));
            Assert.That(cara.MyShareMinor, Is.EqualTo(333));
            Assert.That(anna.MyShareMinor + ben.MyShareMinor + cara.MyShareMinor, Is.EqualTo(1_000),
                "the shares are the ledger's, so they still sum to the total");
        });
    }

    [Test]
    public async Task Bills_ANamedShareBeatsTheEveryoneConvention()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(1), 1_000, "Rent", ("ben", 1));

        var bill = (await Build().BuildAsync(GuildId, "anna")).Bills!.DueSoon.Single();

        Assert.That(bill.MyShareMinor, Is.Null, "Anna is not on this one, and zero would read as a share");
    }

    [Test]
    public async Task Bills_AVariableOneThatHasComeDue_CountsAsNeedingAnAmount()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddBillAsync(DateTimeOffset.UtcNow.AddHours(-1), amountMinor: null, "Electricity");

        var bills = (await Build().BuildAsync(GuildId, "anna")).Bills!;
        var bill = bills.DueSoon.Single();

        Assert.Multiple(() =>
        {
            Assert.That(bills.NeedsAmountCount, Is.EqualTo(1));
            Assert.That(bill.AmountMinor, Is.Null);
            Assert.That(bill.MyShareMinor, Is.Null, "there is no total to divide yet");
            Assert.That(bill.Currency, Is.EqualTo("CHF"));
        });
    }

    [Test]
    public async Task Bills_AVariableOneStillInTheFuture_IsNotYetWaitingOnAnAmount()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(4), amountMinor: null);

        Assert.That((await Build().BuildAsync(GuildId, "anna")).Bills!.NeedsAmountCount, Is.Zero,
            "nobody can enter a figure for a letter that has not arrived");
    }

    [Test]
    public async Task Bills_AreCappedWhileTheCountsStayComplete()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        for (var i = 0; i < 8; i++)
            await AddBillAsync(DateTimeOffset.UtcNow.AddDays(-i - 1), 1_000, $"bill-{i}");

        var bills = (await Build().BuildAsync(GuildId, "anna")).Bills!;

        Assert.Multiple(() =>
        {
            Assert.That(bills.DueSoon, Has.Count.EqualTo(5), "a glance, not the board");
            Assert.That(bills.OverdueCount, Is.EqualTo(8), "the count is the whole truth");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Meals
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Meals_ShowTodaysPlanAndWhetherYouAreCooking()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await AddMealAsync(today, "anna", "Curry");
        await AddMealAsync(today, "ben", "Porridge", MealSlot.Breakfast);

        var meals = (await Build().BuildAsync(GuildId, "anna")).Meals!;

        Assert.Multiple(() =>
        {
            Assert.That(meals.Today.Select(m => m.Title), Is.EqualTo(new[] { "Porridge", "Curry" }),
                "board order: breakfast before dinner");
            Assert.That(meals.ImCookingToday, Is.True);
            Assert.That(meals.Today[1].CookUserId, Is.EqualTo("anna"));
        });
    }

    [Test]
    public async Task Meals_TomorrowsPlanIsNotTodaysGlance()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), "anna");

        var meals = (await Build().BuildAsync(GuildId, "anna")).Meals!;

        Assert.Multiple(() =>
        {
            Assert.That(meals.Today, Is.Empty);
            Assert.That(meals.ImCookingToday, Is.False);
        });
    }

    [Test]
    public async Task Meals_SomebodyElseCooking_IsNotYouCooking()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "ben");

        Assert.That((await Build().BuildAsync(GuildId, "anna")).Meals!.ImCookingToday, Is.False);
    }

    /// <summary>The cap must not be able to answer "no" for somebody who is in fact cooking - a
    /// busy Sunday would otherwise hide the one entry that matters to the person reading it.</summary>
    [Test]
    public async Task Meals_ImCookingToday_LooksPastTheCap()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        for (var i = 0; i < 6; i++) await AddMealAsync(today, "ben", $"meal-{i}");
        await AddMealAsync(today, "anna", "yours", MealSlot.Other);

        var meals = (await Build().BuildAsync(GuildId, "anna")).Meals!;

        Assert.Multiple(() =>
        {
            Assert.That(meals.Today, Has.Count.EqualTo(5));
            Assert.That(meals.ImCookingToday, Is.True);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Maintenance
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Maintenance_CountsEachKindOfProblemSeparately()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAssetAsync("Washing machine", AssetStatus.Broken);
        await AddAssetAsync("Boiler", nextServiceAt: DateTimeOffset.UtcNow.AddDays(-3));
        await AddAssetAsync("Dishwasher", warrantyUntil: DateTimeOffset.UtcNow.AddDays(10));
        await AddAssetAsync("Sofa");

        var maintenance = (await Build().BuildAsync(GuildId, "anna")).Maintenance!;

        Assert.Multiple(() =>
        {
            Assert.That(maintenance.BrokenCount, Is.EqualTo(1));
            Assert.That(maintenance.ServiceOverdueCount, Is.EqualTo(1));
            Assert.That(maintenance.WarrantyExpiringCount, Is.EqualTo(1));
            Assert.That(maintenance.Attention.Select(a => a.Name),
                Is.EqualTo(new[] { "Washing machine", "Boiler", "Dishwasher" }),
                "urgency first, so the cap cannot hide the broken one behind a warranty");
            Assert.That(maintenance.Attention[0].Reason, Is.EqualTo("broken"));
            Assert.That(maintenance.Attention[1].Reason, Is.EqualTo("service_overdue"));
            Assert.That(maintenance.Attention[2].Reason, Is.EqualTo("warranty_expiring"));
        });
    }

    [Test]
    public async Task Maintenance_AWarrantyThatHasAlreadyLapsed_IsNotAskingForAnything()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAssetAsync("Kettle", warrantyUntil: DateTimeOffset.UtcNow.AddDays(-1));

        var maintenance = (await Build().BuildAsync(GuildId, "anna")).Maintenance!;

        Assert.Multiple(() =>
        {
            Assert.That(maintenance.WarrantyExpiringCount, Is.Zero);
            Assert.That(maintenance.Attention, Is.Empty, "there is nothing left to do about it");
        });
    }

    [Test]
    public async Task Maintenance_SomethingTakenOutOfServiceOnPurpose_IsADecisionNotAJob()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAssetAsync("Old freezer", AssetStatus.OutOfService);

        Assert.That((await Build().BuildAsync(GuildId, "anna")).Maintenance!.Attention, Is.Empty);
    }

    [Test]
    public async Task Maintenance_IsCappedAtAGlance()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        for (var i = 0; i < 7; i++) await AddAssetAsync($"asset-{i}", AssetStatus.Broken);

        var maintenance = (await Build().BuildAsync(GuildId, "anna")).Maintenance!;

        Assert.Multiple(() =>
        {
            Assert.That(maintenance.Attention, Has.Count.EqualTo(5));
            Assert.That(maintenance.BrokenCount, Is.EqualTo(7));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Away
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Away_ListsOnlyTheAbsencesInEffectRightNow()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAbsenceAsync("ben", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(5), "In Lisbon");
        await AddAbsenceAsync("cara", DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow.AddDays(14));
        await AddAbsenceAsync("dan", DateTimeOffset.UtcNow.AddDays(-9), DateTimeOffset.UtcNow.AddDays(-2));

        var away = (await Build().BuildAsync(GuildId, "anna")).Away!;

        Assert.Multiple(() =>
        {
            Assert.That(away.Select(a => a.UserId), Is.EqualTo(new[] { "ben" }));
            Assert.That(away[0].Note, Is.EqualTo("In Lisbon"));
        });
    }

    /// <summary>The two sit beside each other and answer different questions: home status decays
    /// and an absence does not. Merging them is the obvious move and would either give a fortnight
    /// in Lisbon an expiry or give "back in an hour" a permanence.</summary>
    [Test]
    public async Task Away_AndHomeStatus_AreReportedSeparately()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddAbsenceAsync("ben", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(6));

        var digest = await Build(("cara", "Out")).BuildAsync(GuildId, "anna");

        Assert.Multiple(() =>
        {
            Assert.That(digest.Away!.Single().UserId, Is.EqualTo("ben"));
            Assert.That(digest.HomeStatus!.Single().UserId, Is.EqualTo("cara"));
        });
    }

    [Test]
    public async Task Away_IsNullWithoutThePresenceModule()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Presence);
        await AddMemberAsync("anna");

        await AddAbsenceAsync("ben", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(6));

        var digest = await Build().BuildAsync(GuildId, "anna");

        Assert.Multiple(() =>
        {
            Assert.That(digest.Away, Is.Null);
            Assert.That(digest.HomeStatus, Is.Null, "both hang off the same module");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ What the digest
    // must not leak ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ASectionWhoseModuleIsOff_IsOmitted()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Ledger);
        await AddMemberAsync("anna");

        var digest = await Build().BuildAsync(GuildId, "anna");

        Assert.Multiple(() =>
        {
            Assert.That(digest.Ledger, Is.Null);
            Assert.That(digest.Bills, Is.Null, "a bill is an expense before it is one");
            Assert.That(digest.Lists, Is.Not.Null, "the other modules are unaffected");
        });
    }

    [TestCase(GuildFeatures.Meals)]
    [TestCase(GuildFeatures.Maintenance)]
    public async Task ANewModuleSwitchedOff_TakesItsSectionWithIt(GuildFeatures feature)
    {
        await SeedAsync(GuildFeaturePresets.Household & ~feature);
        await AddMemberAsync("anna");

        var digest = await Build().BuildAsync(GuildId, "anna");

        Assert.Multiple(() =>
        {
            // Cast to object because the two branches are unrelated DTO types, so the conditional has
            // no natural type and binds to Assert.That(bool, string) instead of the constraint overload.
            Assert.That(feature == GuildFeatures.Meals ? (object?)digest.Meals : digest.Maintenance, Is.Null);
            Assert.That(digest.Chores, Is.Not.Null, "the other modules are unaffected");
        });
    }

    /// <summary>Null and empty mean different things, and a client renders them differently: null is
    /// "there is nothing here for you", empty is "there is a board here and it is clear". Collapsing
    /// the second into the first would make a tidy house look like one without the module.</summary>
    [Test]
    public async Task ASectionThatIsVisibleButEmpty_IsEmptyRatherThanNull()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var digest = await Build().BuildAsync(GuildId, "anna");

        Assert.Multiple(() =>
        {
            Assert.That(digest.Bills!.DueSoon, Is.Empty);
            Assert.That(digest.Meals!.Today, Is.Empty);
            Assert.That(digest.Maintenance!.Attention, Is.Empty);
            Assert.That(digest.Away, Is.Empty);
        });
    }

    /// <summary>The whole risk of collapsing six endpoints into one: the digest must show exactly
    /// what the per-module endpoints would, and no more.</summary>
    [Test]
    public async Task AMemberWhoCannotSeeAChannel_GetsNoSectionForIt()
    {
        await SeedAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("guest", canSee: false);

        var digest = await Build().BuildAsync(GuildId, "guest");

        Assert.Multiple(() =>
        {
            Assert.That(digest.Ledger, Is.Null);
            Assert.That(digest.Lists, Is.Null);
            Assert.That(digest.Chores, Is.Null);
            Assert.That(digest.Pantry, Is.Null);
            Assert.That(digest.Decisions, Is.Null);
            Assert.That(digest.Bills, Is.Null);
            Assert.That(digest.Meals, Is.Null);
            Assert.That(digest.Maintenance, Is.Null);
        });
    }

    /// <summary>Away is the one section with no channel to filter on, and that is not an oversight:
    /// an absence is guild-scoped, and GET /guilds/{id}/absences shows it to any member for the same
    /// reason - the rota assigns on it, so it cannot be a private field. The digest showing exactly
    /// what the module endpoint would is the rule being followed here, not broken.</summary>
    [Test]
    public async Task Away_IsGuildScopedAndSoSurvivesSeeingNoChannels()
    {
        await SeedAsync();
        await AddMemberAsync("guest", canSee: false);
        await AddAbsenceAsync("ben", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(6));

        var digest = await Build().BuildAsync(GuildId, "guest");

        Assert.That(digest.Away!.Single().UserId, Is.EqualTo("ben"));
    }

    // ══════════════════════════════════════════════════════════════════════════ Conditional
    // requests ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ETag_IsStableForUnchangedContentAndMovesWhenItChanges()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var first = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));
        var again = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));

        _context.ListItems.Add(ListItem.Create(new CreateListItemParams
        {
            ChannelId = ListChannelId, GuildId = GuildId, Text = "Milk", AddedByUserId = "anna", Position = 0,
        }));
        await _context.SaveChangesAsync();

        var after = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));

        Assert.Multiple(() =>
        {
            Assert.That(again, Is.EqualTo(first), "nothing changed, so a widget's refresh costs no bytes");
            Assert.That(after, Is.Not.EqualTo(first));
            Assert.That(first, Does.StartWith("\"").And.EndWith("\""), "a strong ETag is a quoted string");
        });
    }

    /// <summary>The ETag is a hash of the whole response, so a section added later is covered by it
    /// automatically - which is only true for as long as the new sections are actually on the DTO
    /// being hashed. This is the assertion that notices if one is ever assembled somewhere else.</summary>
    [Test]
    public async Task ETag_MovesWhenOneOfTheNewSectionsChanges()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        var before = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));

        await AddAssetAsync("Boiler", AssetStatus.Broken);
        var afterAsset = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2), 120_000);
        var afterBill = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));

        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "anna");
        var afterMeal = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));

        await AddAbsenceAsync("ben", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));
        var afterAbsence = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));

        Assert.Multiple(() =>
        {
            Assert.That(afterAsset, Is.Not.EqualTo(before));
            Assert.That(afterBill, Is.Not.EqualTo(afterAsset));
            Assert.That(afterMeal, Is.Not.EqualTo(afterBill));
            Assert.That(afterAbsence, Is.Not.EqualTo(afterMeal));
        });
    }

    /// <summary>The other half of the same promise: a widget refreshing into an unchanged household
    /// pays for no bytes, and adding sections must not have broken that with a clock or an id that
    /// churns.</summary>
    [Test]
    public async Task ETag_IsStableAcrossRepeatedReadsOfAFullDigest()
    {
        await SeedAsync();
        await AddMemberAsync("anna");

        await AddBillAsync(DateTimeOffset.UtcNow.AddDays(2), 120_000);
        await AddMealAsync(DateOnly.FromDateTime(DateTime.UtcNow), "anna");
        await AddAssetAsync("Boiler", AssetStatus.Broken);
        await AddAbsenceAsync("ben", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(3));

        var first = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));
        var again = HouseholdDigestEndpoint.ComputeETag(await Build().BuildAsync(GuildId, "anna"));

        Assert.That(again, Is.EqualTo(first));
    }

    [TestCase("\"abc\"", true)]
    [TestCase("W/\"abc\"", true, Description = "some stacks weaken an ETag when they revalidate")]
    [TestCase("*", true)]
    [TestCase("\"xyz\", \"abc\"", true)]
    [TestCase("\"xyz\"", false)]
    [TestCase("", false)]
    public void IfNoneMatch_IsParsedTheWayTheHeaderIsSpecified(string header, bool expected) =>
        Assert.That(
            HouseholdDigestEndpoint.MatchesIfNoneMatch(new StringValues(header), "\"abc\""),
            Is.EqualTo(expected));
}
