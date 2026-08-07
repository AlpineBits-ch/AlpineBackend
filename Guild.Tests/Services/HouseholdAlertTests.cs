using Guild.Application.Services;
using Guild.Contracts;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>Who gets told, and who deliberately does not.</summary>
[TestFixture]
public class HouseholdAlertTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string LedgerChannelId = "chan-ledger";
    private const string DecisionsChannelId = "chan-decisions";
    private const string ListChannelId = "chan-list";
    private const string PantryChannelId = "chan-pantry";
    private const string EveryoneRoleId = "role-everyone";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _permissions = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task SeedGuildAsync(GuildFeatures features = GuildFeaturePresets.Household)
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
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var (id, type) in new[]
                 {
                     (LedgerChannelId, ChannelType.Ledger),
                     (DecisionsChannelId, ChannelType.Decisions),
                     (ListChannelId, ChannelType.List),
                     (PantryChannelId, ChannelType.Pantry),
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
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (canSee)
        {
            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{userId}", RoleId = EveryoneRoleId, MemberId = memberId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    private HouseholdAlertService BuildAlerts(HomeStatusService? homeStatus = null)
    {
        var notifier = new HouseholdNotifier(
            _context, new NotificationResolutionService(_context), _hub, _bus);

        return new HouseholdAlertService(
            _context, notifier, _permissions,
            homeStatus ?? new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId)),
            NullLogger<HouseholdAlertService>.Instance);
    }

    private List<string> Alerted() =>
        ((FakeHubClients)_hub.Clients).RecipientsOf(HouseholdNotifier.AlertEventName);

    private List<HouseholdPushRequested> Pushes() =>
        _bus.Published.OfType<HouseholdPushRequested>().ToList();

    private static Expense MakeExpense(string payer, long total, params (string UserId, long Share)[] shares)
    {
        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = LedgerChannelId, GuildId = GuildId, PayerUserId = payer,
            Description = "Weekly shop", AmountMinor = total, OccurredAt = DateTimeOffset.UtcNow,
            SplitKind = ExpenseSplitKind.Exact, CreatedByUserId = payer,
        });

        foreach (var (userId, share) in shares)
            expense.Shares.Add(new ExpenseShare { ExpenseId = expense.Id, UserId = userId, AmountMinor = share });

        return expense;
    }

    // ══════════════════════════════════════════════════════════════════════════ Expenses
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ExpenseAdded_TellsTheOtherParticipantsAndNotTheActor()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");

        var expense = MakeExpense("anna", 9000, ("anna", 3000), ("ben", 3000), ("cara", 3000));

        await BuildAlerts().ExpenseAddedAsync(expense, "CHF", actorUserId: "anna");

        Assert.Multiple(() =>
        {
            Assert.That(Alerted(), Is.EquivalentTo(new[] { "ben", "cara" }),
                "the person entering the expense already knows about it");
            Assert.That(Pushes().Single().Body, Is.EqualTo("Your share is CHF 30.00."));
        });
    }

    /// <summary>Equal splits are the common case, so the fan-out collapses to one dispatch. An
    /// exact split costs one per distinct amount, which is what makes the body personal.</summary>
    [Test]
    public async Task ExpenseAdded_GroupsRecipientsBySharedAmount()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");

        var expense = MakeExpense("anna", 10000, ("anna", 2000), ("ben", 4000), ("cara", 4000));

        await BuildAlerts().ExpenseAddedAsync(expense, "CHF", actorUserId: "anna");

        var pushes = Pushes();

        Assert.Multiple(() =>
        {
            Assert.That(pushes, Has.Count.EqualTo(1), "ben and cara owe the same, so one dispatch covers both");
            Assert.That(pushes[0].UserIds, Is.EquivalentTo(new[] { "ben", "cara" }));
            Assert.That(pushes[0].Body, Is.EqualTo("Your share is CHF 40.00."));
        });
    }

    /// <summary>Entering an expense on somebody else's behalf is normal and needs ManageLedger; the
    /// person recorded as having paid is told in their own words rather than being told they owe a
    /// share of it.</summary>
    [Test]
    public async Task ExpenseAdded_TellsThePayerSeparatelyWhenSomebodyElseRecordedIt()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var expense = MakeExpense("ben", 8000, ("anna", 4000), ("ben", 4000));

        await BuildAlerts().ExpenseAddedAsync(expense, "CHF", actorUserId: "anna");

        var bodies = Pushes().SelectMany(p => p.UserIds.Select(u => (User: u, p.Body))).ToList();

        Assert.That(bodies, Is.EquivalentTo(new[]
        {
            ("ben", "Your share is CHF 40.00."),
            ("ben", "Recorded as paid by you: CHF 80.00."),
        }));
    }

    [Test]
    public async Task ExpenseAdded_SkipsAParticipantWhoCannotSeeTheLedger()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("guest", canSee: false);

        var expense = MakeExpense("anna", 9000, ("anna", 3000), ("ben", 3000), ("guest", 3000));

        await BuildAlerts().ExpenseAddedAsync(expense, "CHF", actorUserId: "anna");

        Assert.That(Alerted(), Does.Not.Contain("guest"),
            "a notification carrying an amount is subject to the same ViewChannel the GET is");
    }

    [Test]
    public async Task ExpenseAdded_WithNobodyLeftToTell_SendsNothing()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        var expense = MakeExpense("anna", 3000, ("anna", 3000));

        await BuildAlerts().ExpenseAddedAsync(expense, "CHF", actorUserId: "anna");

        Assert.Multiple(() =>
        {
            Assert.That(Alerted(), Is.Empty);
            Assert.That(Pushes(), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Settlements
    // ══════════════════════════════════════════════════════════════════════════

    private static Settlement MakeSettlement(string from, string to, long amount) => new()
    {
        Id = "setl-1", ChannelId = LedgerChannelId, GuildId = GuildId,
        FromUserId = from, ToUserId = to, AmountMinor = amount,
        SettledAt = DateTimeOffset.UtcNow, RecordedByUserId = from,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task SettlementRecorded_TellsOnlyTheCounterparty()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await BuildAlerts().SettlementRecordedAsync(MakeSettlement("anna", "ben", 8400), "CHF", "anna");

        Assert.Multiple(() =>
        {
            Assert.That(Alerted(), Is.EquivalentTo(new[] { "ben" }));
            Assert.That(Pushes().Single().Body, Is.EqualTo("CHF 84.00 was recorded as paid to you."));
        });
    }

    /// <summary>ManageLedger allows recording a payment between two other people, which rewrites
    /// both their balances while neither of them is in the room.</summary>
    [Test]
    public async Task SettlementRecorded_ByAThirdParty_TellsBothSides()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");

        await BuildAlerts().SettlementRecordedAsync(MakeSettlement("anna", "ben", 5000), "CHF", "cara");

        Assert.That(Alerted(), Is.EquivalentTo(new[] { "anna", "ben" }));
    }

    // ══════════════════════════════════════════════════════════════════════════ Decisions
    // ══════════════════════════════════════════════════════════════════════════

    private static Decision MakeDecision(string createdBy) => Decision.Create(new CreateDecisionParams
    {
        ChannelId = DecisionsChannelId, GuildId = GuildId, Title = "Are we getting a cat",
        CreatedByUserId = createdBy, Quorum = 2,
    });

    [Test]
    public async Task DecisionOpened_TellsTheHouseExceptTheAuthor()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");
        await AddMemberAsync("guest", canSee: false);

        await BuildAlerts().DecisionOpenedAsync(MakeDecision("anna"), "anna");

        Assert.That(Alerted(), Is.EquivalentTo(new[] { "ben", "cara" }));
    }

    [Test]
    public async Task DecisionBlocked_TellsTheAuthorAndTheVotersButNotTheBlocker()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");
        await AddMemberAsync("dan");

        var decision = MakeDecision("anna");
        decision.Votes.Add(new DecisionVote
        {
            DecisionId = decision.Id, UserId = "cara", Kind = DecisionVoteKind.Support,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        decision.Votes.Add(new DecisionVote
        {
            DecisionId = decision.Id, UserId = "ben", Kind = DecisionVoteKind.Block,
            Reason = "I'm allergic", CreatedAt = DateTimeOffset.UtcNow,
        });

        await BuildAlerts().DecisionBlockedAsync(decision, "ben", "I'm allergic");

        Assert.Multiple(() =>
        {
            Assert.That(Alerted(), Is.EquivalentTo(new[] { "anna", "cara" }),
                "dan never engaged, so a block is not news to him");
            Assert.That(Pushes().Single().Body, Is.EqualTo("Blocked: I'm allergic"));
        });
    }

    [Test]
    public async Task DecisionBlocked_TruncatesALongReason()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await BuildAlerts().DecisionBlockedAsync(MakeDecision("anna"), "ben", new string('x', 400));

        var body = Pushes().Single().Body;

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.EndWith("..."));
            Assert.That(body, Has.Length.LessThan(140), "a notification is a pointer, not the document");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Running low, whose audience is split by where the recipient is standing
    // ══════════════════════════════════════════════════════════════════════════

    private static ListItem MakeRestockLine() => ListItem.Create(new CreateListItemParams
    {
        ChannelId = ListChannelId, GuildId = GuildId, Text = "Milk", Section = "Restock",
        AddedByUserId = "anna", Position = 0,
    });

    private static PantryItem MakeLowItem() => PantryItem.Create(new CreatePantryItemParams
    {
        ChannelId = PantryChannelId, GuildId = GuildId, Name = "Milk",
        Quantity = 0, LowThreshold = 1, AddedByUserId = "anna",
    });

    /// <summary>Who a push of one kind actually went to.</summary>
    private List<string> PushedFor(string kind) =>
        Pushes().Where(p => p.Kind == kind).SelectMany(p => p.UserIds).ToList();

    private HouseholdPushRequested PushOf(string kind) =>
        Pushes().Single(p => p.Kind == kind);

    [Test]
    public async Task PantryLow_AsksThePeopleWhoAreOutAndTellsEveryoneElse()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");

        var homeStatus = new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(
            GuildId, ("ben", "Out"), ("cara", "Home")));

        await BuildAlerts(homeStatus).PantryLowAsync(MakeLowItem(), MakeRestockLine(), "anna");

        Assert.Multiple(() =>
        {
            Assert.That(PushedFor(HouseholdAlertService.KindRestock), Is.EquivalentTo(new[] { "ben" }),
                "ben is in the shop, so he gets the request rather than the news");
            Assert.That(PushedFor(HouseholdAlertService.KindPantryLow), Is.EquivalentTo(new[] { "cara" }),
                "cara is at home: worth knowing, not worth asking");
            Assert.That(Alerted(), Does.Not.Contain("anna"), "anna is the one who used the last of it");
        });
    }

    /// <summary>One event, one buzz.</summary>
    [Test]
    public async Task PantryLow_NeverBuzzesOnePersonTwice()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var homeStatus = new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId, ("ben", "Out")));

        await BuildAlerts(homeStatus).PantryLowAsync(MakeLowItem(), MakeRestockLine(), "anna");

        Assert.Multiple(() =>
        {
            Assert.That(PushedFor(HouseholdAlertService.KindRestock), Is.EquivalentTo(new[] { "ben" }));
            Assert.That(PushedFor(HouseholdAlertService.KindPantryLow), Is.Empty);
        });
    }

    [Test]
    public async Task PantryLow_CountsOnMyWayAsOut()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var homeStatus = new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId, ("ben", "OnMyWay")));

        await BuildAlerts(homeStatus).PantryLowAsync(MakeLowItem(), MakeRestockLine(), "anna");

        Assert.That(PushedFor(HouseholdAlertService.KindRestock), Is.EquivalentTo(new[] { "ben" }));
    }

    [Test]
    public async Task PantryLow_WithNobodyOut_StillTellsTheHouse()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await BuildAlerts().PantryLowAsync(MakeLowItem(), MakeRestockLine(), "anna");

        Assert.Multiple(() =>
        {
            Assert.That(PushedFor(HouseholdAlertService.KindRestock), Is.Empty);
            Assert.That(PushedFor(HouseholdAlertService.KindPantryLow), Is.EquivalentTo(new[] { "ben" }));
        });
    }

    /// <summary>Without a home-status board there is no way to know who is out, so nobody is asked
    /// to go and buy it - but the house is still told it ran out, which is the part that does not
    /// depend on knowing where anyone is.</summary>
    [Test]
    public async Task PantryLow_WithPresenceDisabled_TellsTheHouseAndAsksNobody()
    {
        await SeedGuildAsync(GuildFeaturePresets.Household & ~GuildFeatures.Presence);
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var homeStatus = new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId, ("ben", "Out")));

        await BuildAlerts(homeStatus).PantryLowAsync(MakeLowItem(), MakeRestockLine(), "anna");

        Assert.Multiple(() =>
        {
            Assert.That(PushedFor(HouseholdAlertService.KindRestock), Is.Empty);
            Assert.That(PushedFor(HouseholdAlertService.KindPantryLow), Is.EquivalentTo(new[] { "ben" }));
        });
    }

    /// <summary>The gap this alert was added for.</summary>
    [Test]
    public async Task PantryLow_WithNoRestockList_StillTellsTheHouse()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await BuildAlerts().PantryLowAsync(MakeLowItem(), restocked: null, "anna");

        var push = PushOf(HouseholdAlertService.KindPantryLow);

        Assert.Multiple(() =>
        {
            Assert.That(push.UserIds, Is.EquivalentTo(new[] { "ben" }));
            Assert.That(push.Title, Is.EqualTo("Milk"));
            Assert.That(push.BodyLocKey, Is.EqualTo(HouseholdLocKeys.PantryLowBody),
                "there is no list to name, so the wording that names one must not be used");
            Assert.That(push.BodyLocArgs, Is.Empty);
        });
    }

    [Test]
    public async Task PantryLow_NamesTheListItWentOn()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await BuildAlerts().PantryLowAsync(MakeLowItem(), MakeRestockLine(), "anna");

        var push = PushOf(HouseholdAlertService.KindPantryLow);

        Assert.Multiple(() =>
        {
            Assert.That(push.BodyLocKey, Is.EqualTo(HouseholdLocKeys.PantryLowListedBody));
            Assert.That(push.BodyLocArgs, Is.EqualTo(new[] { ListChannelId }),
                "the channel's name, which the seed sets to its id");
            Assert.That(push.Body, Does.Contain(ListChannelId),
                "and the English says the same thing, for a client that cannot resolve the key");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Shopping lists
    // ══════════════════════════════════════════════════════════════════════════

    private static ListItem MakeListLine(string text = "Milk", string? assignee = null,
        string addedBy = "anna") =>
        ListItem.Create(new CreateListItemParams
        {
            ChannelId = ListChannelId, GuildId = GuildId, Text = text,
            AssigneeUserId = assignee, AddedByUserId = addedBy, Position = 0,
        });

    private async Task SeedListItemsAsync(params (string Text, string AddedBy)[] items)
    {
        var position = 0;
        foreach (var (text, addedBy) in items)
        {
            var item = MakeListLine(text, addedBy: addedBy);
            item.Position = position++;
            _context.ListItems.Add(item);
        }

        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task ListItemAdded_ReachesThePeopleWhoAreOut()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");

        var homeStatus = new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(
            GuildId, ("ben", "Out"), ("cara", "Home")));

        await BuildAlerts(homeStatus).ListItemAddedAsync(MakeListLine(), "Shopping", "anna");

        Assert.That(PushedFor(HouseholdAlertService.KindListItemAdded), Is.EquivalentTo(new[] { "ben" }),
            "cara will see the list when she next looks at it; ben is in the shop right now");
    }

    /// <summary>Deliberately silent.</summary>
    [Test]
    public async Task ListItemAdded_WithNobodyOutAndNobodyAssigned_SendsNothing()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await BuildAlerts().ListItemAddedAsync(MakeListLine(), "Shopping", "anna");

        Assert.That(Alerted(), Is.Empty);
    }

    /// <summary>Being handed a job is worth knowing wherever you are, so this half needs no
    /// home-status board and survives Presence being off entirely.</summary>
    [Test]
    public async Task ListItemAdded_TellsTheAssigneeWithoutPresence()
    {
        await SeedGuildAsync(GuildFeaturePresets.Household & ~GuildFeatures.Presence);
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await BuildAlerts().ListItemAddedAsync(MakeListLine(assignee: "ben"), "Shopping", "anna");

        var push = PushOf(HouseholdAlertService.KindListItemAdded);

        Assert.Multiple(() =>
        {
            Assert.That(push.UserIds, Is.EquivalentTo(new[] { "ben" }));
            Assert.That(push.BodyLocKey, Is.EqualTo(HouseholdLocKeys.ListItemAssignedBody));
        });
    }

    [Test]
    public async Task ListItemAdded_TellsAnAssigneeWhoIsAlsoOutOnlyOnce()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var homeStatus = new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId, ("ben", "Out")));

        await BuildAlerts(homeStatus).ListItemAddedAsync(MakeListLine(assignee: "ben"), "Shopping", "anna");

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Has.Count.EqualTo(1));
            Assert.That(PushOf(HouseholdAlertService.KindListItemAdded).BodyLocKey,
                Is.EqualTo(HouseholdLocKeys.ListItemAssignedBody),
                "the sentence that names them beats the generic one");
        });
    }

    [Test]
    public async Task ListItemAdded_DoesNotTellTheActorAboutTheirOwnLine()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        var homeStatus = new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId, ("anna", "Out")));

        await BuildAlerts(homeStatus).ListItemAddedAsync(
            MakeListLine(assignee: "anna"), "Shopping", "anna");

        Assert.That(Alerted(), Is.Empty);
    }

    [Test]
    public async Task ListCompleted_TellsThePeopleWhoAddedSomething()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");
        await SeedListItemsAsync(("Milk", "anna"), ("Bread", "ben"));

        await BuildAlerts().ListCompletedAsync(GuildId, ListChannelId, "Shopping", "cara");

        var push = PushOf(HouseholdAlertService.KindListCompleted);

        Assert.Multiple(() =>
        {
            Assert.That(push.UserIds, Is.EquivalentTo(new[] { "anna", "ben" }));
            Assert.That(push.Title, Is.EqualTo("Shopping"));
            Assert.That(push.TitleLocKey, Is.Null, "a channel's name is not translated");
            Assert.That(push.BodyLocKey, Is.EqualTo(HouseholdLocKeys.ListCompletedBody));
        });
    }

    [Test]
    public async Task ListCompleted_LeavesOutWhoeverTickedTheLastBox()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await SeedListItemsAsync(("Milk", "anna"), ("Bread", "ben"));

        await BuildAlerts().ListCompletedAsync(GuildId, ListChannelId, "Shopping", "ben");

        Assert.That(PushedFor(HouseholdAlertService.KindListCompleted),
            Is.EquivalentTo(new[] { "anna" }), "ben did the shopping and knows he finished");
    }

    [Test]
    public async Task ListCompleted_WithNobodyElseWaiting_SendsNothing()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await SeedListItemsAsync(("Milk", "anna"));

        await BuildAlerts().ListCompletedAsync(GuildId, ListChannelId, "Shopping", "anna");

        Assert.That(Alerted(), Is.Empty,
            "ben put nothing on the list and is not waiting on it");
    }

    // ══════════════════════════════════════════════════════════════════════════ Localization keys
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A key that exists in C# and in nobody's string bundle is not a compile error - it is
    /// a notification that arrives on a phone reading "household_pantry_lwo_body". The declared set
    /// is what the Android, iOS and Dart resource files are checked against, so nothing may be sent
    /// under a key that is missing from it.</summary>
    [Test]
    public async Task EveryKeySentIsOneTheClientsHaveBeenToldAbout()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await SeedListItemsAsync(("Milk", "anna"));

        var alerts = BuildAlerts(new HomeStatusService(
            RedisTestFactory.CreateWithHomeStatus(GuildId, ("ben", "Out"))));

        await alerts.PantryLowAsync(MakeLowItem(), MakeRestockLine(), "anna");
        await alerts.PantryLowAsync(MakeLowItem(), restocked: null, "anna");
        await alerts.ListItemAddedAsync(MakeListLine(assignee: "ben"), "Shopping", "anna");
        await alerts.ListCompletedAsync(GuildId, ListChannelId, "Shopping", "ben");
        await alerts.ExpenseAddedAsync(MakeExpense("anna", 6000, ("ben", 3000)), "CHF", "anna");
        await alerts.DecisionBlockedAsync(MakeDecision("anna"), "ben", "no room");
        await alerts.PantryExpiringAsync(GuildId, PantryChannelId, [MakeLowItem()]);

        var keys = Pushes()
            .SelectMany(p => new[] { p.TitleLocKey, p.BodyLocKey })
            .Where(k => k is not null)
            .Distinct()
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(keys, Is.Not.Empty);
            Assert.That(keys, Is.SubsetOf(HouseholdLocKeys.All));
        });
    }

    /// <summary>Arguments without a key are the one combination the Firebase SDK rejects outright,
    /// and a key whose arguments were dropped renders with empty holes in it.</summary>
    [Test]
    public async Task ArgumentsNeverTravelWithoutAKeyToPutThemIn()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await BuildAlerts().ExpenseAddedAsync(MakeExpense("anna", 6000, ("ben", 3000)), "CHF", "anna");
        await BuildAlerts().PantryLowAsync(MakeLowItem(), MakeRestockLine(), "anna");

        Assert.Multiple(() =>
        {
            foreach (var push in Pushes())
            {
                if (push.TitleLocKey is null)
                    Assert.That(push.TitleLocArgs, Is.Empty, $"{push.Kind} title");

                if (push.BodyLocKey is null)
                    Assert.That(push.BodyLocArgs, Is.Empty, $"{push.Kind} body");
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Failure is never the caller's problem
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Alerts fire after the write has committed.</summary>
    [Test]
    public async Task AnAlertThatCannotBeDelivered_DoesNotFailTheCaller()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var alerts = new HouseholdAlertService(
            _context,
            new HouseholdNotifier(_context, new NotificationResolutionService(_context), _hub, new ThrowingMessageBus()),
            _permissions,
            new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId)),
            NullLogger<HouseholdAlertService>.Instance);

        var expense = MakeExpense("anna", 6000, ("anna", 3000), ("ben", 3000));

        Assert.DoesNotThrowAsync(() => alerts.ExpenseAddedAsync(expense, "CHF", "anna"));
    }
}
