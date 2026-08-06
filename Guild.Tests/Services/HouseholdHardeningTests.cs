using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// The household fixes: broadcast scoping, rotation fairness under load, backlog collapse, chore
/// reminders through quiet hours, ledger member validation, and the move-out flow.
/// </summary>
[TestFixture]
public class HouseholdHardeningTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string ChoresChannelId = "chan-chores";
    private const string LedgerChannelId = "chan-ledger";
    private const string ListChannelId = "chan-list";
    private const string RoleId = "role-flatmates";

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
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _rotation = new ChoreRotationService(_context);
        _ledger = new LedgerService(_context);
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
            Id = RoleId, GuildId = GuildId, Type = RoleType.None, Name = "Flatmates",
            Permissions = Role.FlatmatePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var (id, type) in new[]
                 {
                     (ChoresChannelId, ChannelType.Chores),
                     (LedgerChannelId, ChannelType.Ledger),
                     (ListChannelId, ChannelType.List),
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

    private async Task<string> AddMemberAsync(string userId, bool inRotationRole = true)
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
                Id = $"rm-{userId}", RoleId = RoleId, MemberId = memberId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
        return memberId;
    }

    private async Task<Chore> AddChoreAsync(string title = "Bathroom", int effortMinutes = 30,
        DateTimeOffset? anchorAt = null, int intervalDays = 7)
    {
        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChoresChannelId, GuildId = GuildId, Title = title,
            IntervalDays = intervalDays, AnchorAt = anchorAt ?? DateTimeOffset.UtcNow,
            EffortMinutes = effortMinutes, RotationRoleId = RoleId,
        });
        _context.Chores.Add(chore);
        await _context.SaveChangesAsync();
        return chore;
    }

    private async Task<Expense> AddExpenseAsync(string payerUserId, long amountMinor,
        params (string UserId, long AmountMinor)[] shares)
    {
        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = LedgerChannelId, GuildId = GuildId, PayerUserId = payerUserId,
            Description = "Shop", AmountMinor = amountMinor, OccurredAt = DateTimeOffset.UtcNow,
            SplitKind = ExpenseSplitKind.Exact, CreatedByUserId = payerUserId,
        });

        foreach (var (userId, share) in shares)
            expense.Shares.Add(new ExpenseShare { ExpenseId = expense.Id, UserId = userId, AmountMinor = share });

        _context.Expenses.Add(expense);
        await _context.SaveChangesAsync();
        return expense;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Rotation counts outstanding work, not just completed work
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The bug this replaces: assignment weighed only completed occurrences, so two
    /// chores generated in the same sweep both went to whoever was lightest, because no completion
    /// happens between two iterations of a loop.</summary>
    [Test]
    public async Task PickNextAssignee_CountsWorkAlreadyAssignedButNotDone()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync(effortMinutes: 60);

        // Nobody has completed anything, so on completed-minutes alone the two are tied and the
        // ordinal tiebreak puts Anna first every time.
        var first = await _rotation.PickNextAssigneeAsync(chore);
        Assert.That(first, Is.EqualTo("anna"));

        var occurrence = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow, "anna");
        _context.ChoreOccurrences.Add(occurrence);
        await _context.SaveChangesAsync();

        Assert.That(await _rotation.PickNextAssigneeAsync(chore), Is.EqualTo("ben"),
            "Anna is holding an unfinished 60-minute chore and is no longer the lightest");
    }

    /// <summary>Same property one step earlier: within a single unit of work, before anything is
    /// committed. This is the case the reconcile sweep actually hits.</summary>
    [Test]
    public async Task StageNextOccurrence_SpreadsABatchAcrossTheHouse()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var bins = await AddChoreAsync("Bins", effortMinutes: 20);
        var bathroom = await AddChoreAsync("Bathroom", effortMinutes: 20);

        var first = await _rotation.StageNextOccurrenceAsync(bins);
        var second = await _rotation.StageNextOccurrenceAsync(bathroom);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(second!.AssignedUserId, Is.Not.EqualTo(first!.AssignedUserId),
                "two chores staged in one sweep must not both land on the same person");
        });
    }

    [Test]
    public async Task OutstandingLoad_IgnoresCompletedAndSkippedWork()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var chore = await AddChoreAsync(effortMinutes: 45);

        var done = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddDays(-2), "anna");
        done.CompletedAt = DateTimeOffset.UtcNow.AddDays(-2);

        var skipped = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddDays(-1), "anna");
        skipped.SkippedAt = DateTimeOffset.UtcNow.AddDays(-1);

        _context.ChoreOccurrences.AddRange(done, skipped);
        await _context.SaveChangesAsync();

        var load = await _rotation.GetOutstandingLoadAsync(GuildId, ["anna"]);

        Assert.That(load.GetValueOrDefault("anna"), Is.Zero,
            "outstanding means still owed, not merely resolved one way or the other");
    }

    // ══════════════════════════════════════════════════════════════════════════ Backlog collapse
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A chore entered with a months-old anchor - the natural thing to type when the house
    /// has been doing it for months - used to emit one past-dated occurrence per five-minute sweep
    /// until it caught up.</summary>
    [Test]
    public async Task StageNextOccurrence_OldAnchor_ProducesOneOccurrenceNotABacklog()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        var chore = await AddChoreAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(-180), intervalDays: 1);

        var staged = await _rotation.StageNextOccurrenceAsync(chore);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(staged, Is.Not.Null);
            Assert.That(_context.ChoreOccurrences.Count(), Is.EqualTo(1));
            Assert.That(staged!.DueAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddDays(-1)),
                "the occurrence generated is the current period's, not one from six months ago");
            Assert.That(chore.NextDueAt, Is.GreaterThan(DateTimeOffset.UtcNow),
                "and the schedule is now in the future rather than 179 slots behind");
        });
    }

    [Test]
    public async Task StageNextOccurrence_FutureAnchor_IsLeftAlone()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        var anchor = DateTimeOffset.UtcNow.AddDays(3);
        var chore = await AddChoreAsync(anchorAt: anchor);

        var staged = await _rotation.StageNextOccurrenceAsync(chore);
        await _context.SaveChangesAsync();

        Assert.That(staged!.DueAt, Is.EqualTo(anchor),
            "a chore that starts next week still shows on the board for next week");
    }

    // ══════════════════════════════════════════════════════════════════════════ Broadcast scoping
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The leak this closes: household mutations fanned out to every online member of the guild, so
    /// a guest with access to one channel received every expense posted in a ledger channel that
    /// returns 403 to them over REST.
    /// </summary>
    [Test]
    public async Task ChannelAudience_ExcludesAMemberWhoCannotSeeTheLedger()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("guest", inRotationRole: false);
        await SeedEveryoneRoleAsync("anna", "guest");

        // The ledger denies @everyone and allows only the Flatmates role, which the guest lacks.
        _context.Set<ChannelPermission>().AddRange(
            new ChannelPermission
            {
                Id = "cp-deny", ChannelId = LedgerChannelId, RoleId = "role-everyone",
                DenyPermissions = Permissions.ViewChannel,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            },
            new ChannelPermission
            {
                Id = "cp-allow", ChannelId = LedgerChannelId, RoleId = RoleId,
                AllowPermissions = Permissions.ViewChannel,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

        await _context.SaveChangesAsync();

        var audience = new ChannelAudienceService(_permissions, new MemoryCache(new MemoryCacheOptions()));
        var viewers = await audience.FilterToViewersAsync(LedgerChannelId, ["anna", "guest"]);

        Assert.That(viewers, Is.EquivalentTo(new[] { "anna" }),
            "the guest gets a 403 opening the ledger over REST and must not receive its realtime events");
    }

    /// <summary>The other half: the filter must not quietly cost people events they are entitled
    /// to. A regression here is a shopping list that stops syncing.</summary>
    [Test]
    public async Task ChannelAudience_KeepsEveryoneOnAnUnrestrictedList()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("guest", inRotationRole: false);
        await SeedEveryoneRoleAsync("anna", "guest");

        var audience = new ChannelAudienceService(_permissions, new MemoryCache(new MemoryCacheOptions()));
        var viewers = await audience.FilterToViewersAsync(ListChannelId, ["anna", "guest"]);

        Assert.That(viewers, Is.EquivalentTo(new[] { "anna", "guest" }));
    }

    private async Task SeedEveryoneRoleAsync(params string[] userIds)
    {
        _context.Roles.Add(new Role
        {
            Id = "role-everyone", GuildId = GuildId, Type = RoleType.Everyone, Name = "Everyone",
            Permissions = Permissions.ViewChannel,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var userId in userIds)
        {
            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-everyone-{userId}", RoleId = "role-everyone", MemberId = $"member-{userId}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Chore reminders, and the quiet hours that were previously inert
    // ══════════════════════════════════════════════════════════════════════════

    private (ChoreReminderService Service, FakeHubContext Hub, FakeMessageBus Bus) BuildReminder()
    {
        var hub = new FakeHubContext();
        var bus = new FakeMessageBus();
        var notifier = new HouseholdNotifier(
            _context, new NotificationResolutionService(_context), hub, bus);

        return (new ChoreReminderService(_context, notifier, NullLogger<ChoreReminderService>.Instance), hub, bus);
    }

    private async Task<ChoreOccurrence> AddDueOccurrenceAsync(string assignee, TimeSpan dueAgo)
    {
        var chore = await AddChoreAsync();
        var occurrence = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow - dueAgo, assignee);
        _context.ChoreOccurrences.Add(occurrence);
        await _context.SaveChangesAsync();
        return occurrence;
    }

    private async Task SetQuietHoursAsync(int startMinute, int endMinute)
    {
        _context.GuildQuietHoursConfigs.Add(new GuildQuietHoursConfig
        {
            GuildId = GuildId, Enabled = true, TimeZoneId = "UTC",
            StartMinuteLocal = startMinute, EndMinuteLocal = endMinute,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task Reminder_NotifiesTheAssigneeAndStampsTheOccurrence()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var occurrence = await AddDueOccurrenceAsync("anna", TimeSpan.FromMinutes(5));

        var (service, hub, bus) = BuildReminder();
        var sent = await service.SendDueRemindersAsync();

        var clients = (FakeHubClients)hub.Clients;

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.EqualTo(1));
            Assert.That(occurrence.RemindedAt, Is.Not.Null);
            Assert.That(clients.RecipientsOf("guild.ChoreReminder"), Is.EquivalentTo(new[] { "anna" }));
            Assert.That(bus.Published.OfType<Guild.Contracts.Bus.Events.HouseholdPushRequested>().Single().UserIds,
                Is.EquivalentTo(new[] { "anna" }));
        });
    }

    [Test]
    public async Task Reminder_IsSentOnlyOnce()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddDueOccurrenceAsync("anna", TimeSpan.FromMinutes(5));

        var (service, _, _) = BuildReminder();

        Assert.Multiple(async () =>
        {
            Assert.That(await service.SendDueRemindersAsync(), Is.EqualTo(1));
            Assert.That(await service.SendDueRemindersAsync(), Is.Zero,
                "RemindedAt is what makes the sweep at-most-once");
        });
    }

    /// <summary>The behaviour quiet hours claimed to have and did not: DeferPast had no production
    /// caller at all, so a configured 22:00-07:00 window changed nothing.</summary>
    [Test]
    public async Task Reminder_InsideTheQuietWindow_IsHeldRatherThanSent()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var occurrence = await AddDueOccurrenceAsync("anna", TimeSpan.FromMinutes(5));

        // A window covering the whole day, so "now" is inside it whenever this test runs.
        await SetQuietHoursAsync(startMinute: 0, endMinute: 1439);

        var (service, hub, _) = BuildReminder();
        var sent = await service.SendDueRemindersAsync();

        var clients = (FakeHubClients)hub.Clients;

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.Zero);
            Assert.That(clients.RecipientsOf("guild.ChoreReminder"), Is.Empty);
            Assert.That(occurrence.RemindedAt, Is.Null,
                "left unstamped so the sweep after the window closes still delivers it");
        });
    }

    [Test]
    public async Task Reminder_OutsideTheQuietWindow_IsSentNormally()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddDueOccurrenceAsync("anna", TimeSpan.FromMinutes(5));

        // A one-minute window that "now" is essentially certain not to fall inside.
        var nowMinute = DateTimeOffset.UtcNow.Hour * 60 + DateTimeOffset.UtcNow.Minute;
        var start = (nowMinute + 120) % 1440;
        await SetQuietHoursAsync(start, (start + 60) % 1440);

        var (service, _, _) = BuildReminder();

        Assert.That(await service.SendDueRemindersAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task Reminder_NotYetDue_IsNotSent()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddDueOccurrenceAsync("anna", TimeSpan.FromHours(-6));   // due in six hours

        var (service, _, _) = BuildReminder();

        Assert.That(await service.SendDueRemindersAsync(), Is.Zero);
    }

    /// <summary>A guild coming back from a long outage must not buzz everyone about chores from
    /// last week - but the rows still have to leave the candidate set, or the sweep re-examines
    /// them forever.</summary>
    [Test]
    public async Task Reminder_LongOverdue_IsStampedWithoutSending()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var occurrence = await AddDueOccurrenceAsync("anna", TimeSpan.FromDays(4));

        var (service, hub, _) = BuildReminder();
        var sent = await service.SendDueRemindersAsync();

        var clients = (FakeHubClients)hub.Clients;

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.Zero);
            Assert.That(clients.RecipientsOf("guild.ChoreReminder"), Is.Empty);
            Assert.That(occurrence.RemindedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Reminder_MutedMember_GetsTheRealtimeEventButNoPush()
    {
        await SeedGuildAsync();
        var memberId = await AddMemberAsync("anna");
        await AddDueOccurrenceAsync("anna", TimeSpan.FromMinutes(5));

        _context.GuildNotificationSettings.Add(new GuildNotificationSetting
        {
            Id = GuildNotificationSetting.GenerateId(),
            MemberId = memberId,
            MutedUntil = DateTimeOffset.UtcNow.AddDays(1),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var (service, hub, bus) = BuildReminder();
        await service.SendDueRemindersAsync();

        var clients = (FakeHubClients)hub.Clients;

        Assert.Multiple(() =>
        {
            Assert.That(clients.RecipientsOf("guild.ChoreReminder"), Is.EquivalentTo(new[] { "anna" }),
                "an open app still updates - muting is about being interrupted, not about being wrong");
            Assert.That(bus.Published.OfType<Guild.Contracts.Bus.Events.HouseholdPushRequested>(), Is.Empty,
                "someone who muted the house does not get their phone buzzed by it");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Ledger: membership validation and aggregated balances
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task AreMembers_RejectsAnIdThatIsNotInTheGuild()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        Assert.Multiple(async () =>
        {
            Assert.That(await _ledger.AreMembersAsync(GuildId, "anna"), Is.True);
            Assert.That(await _ledger.AreMembersAsync(GuildId, "anna", "ghost"), Is.False,
                "a payer who does not live here becomes a creditor nobody can ever pay off");
            Assert.That(await _ledger.AreMembersAsync(GuildId), Is.True, "nothing to check is not a failure");
        });
    }

    /// <summary>The aggregation rewrite must produce exactly what the in-memory version did. The
    /// invariant that proves it: balances sum to zero.</summary>
    [Test]
    public async Task GetBalances_SumToZeroAndOmitSettledMembers()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");

        // Anna fronted 9000 split three ways; Ben fronted 3000 split between him and Anna.
        //   anna: +9000 - 3000 - 1500 = +4500
        //   ben:  +3000 - 3000 - 1500 = -1500
        //   cara:        - 3000        = -3000
        await AddExpenseAsync("anna", 9000, ("anna", 3000), ("ben", 3000), ("cara", 3000));
        await AddExpenseAsync("ben", 3000, ("anna", 1500), ("ben", 1500));

        var balances = await _ledger.GetBalancesAsync(LedgerChannelId);

        Assert.Multiple(() =>
        {
            Assert.That(balances.Sum(b => b.NetMinor), Is.Zero);
            Assert.That(balances.Single(b => b.UserId == "anna").NetMinor, Is.EqualTo(4500));
            Assert.That(balances.Single(b => b.UserId == "ben").NetMinor, Is.EqualTo(-1500));
            Assert.That(balances.Single(b => b.UserId == "cara").NetMinor, Is.EqualTo(-3000));
        });
    }

    [Test]
    public async Task GetBalances_OmitsAMemberWhoIsExactlySquare()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");

        // Ben paid exactly his own share, so he nets to zero and should not appear at all.
        await AddExpenseAsync("anna", 4000, ("anna", 2000), ("cara", 2000));
        await AddExpenseAsync("ben", 1000, ("ben", 1000));

        var balances = await _ledger.GetBalancesAsync(LedgerChannelId);

        Assert.Multiple(() =>
        {
            Assert.That(balances.Any(b => b.UserId == "ben"), Is.False);
            Assert.That(balances.Sum(b => b.NetMinor), Is.Zero);
        });
    }

    [Test]
    public async Task GetBalances_SettlementsMoveTheBalanceAndStillSumToZero()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await AddExpenseAsync("anna", 4000, ("anna", 2000), ("ben", 2000));

        _context.Settlements.Add(new Settlement
        {
            Id = Settlement.GenerateId(), ChannelId = LedgerChannelId, GuildId = GuildId,
            FromUserId = "ben", ToUserId = "anna", AmountMinor = 2000,
            SettledAt = DateTimeOffset.UtcNow, RecordedByUserId = "ben",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        Assert.That(await _ledger.GetBalancesAsync(LedgerChannelId), Is.Empty,
            "paid in full - an empty list is what 'the house is settled' looks like");
    }

    [Test]
    public async Task GetBalances_EmptyChannel_IsEmptyRatherThanThrowing()
    {
        await SeedGuildAsync();
        Assert.That(await _ledger.GetBalancesAsync(LedgerChannelId), Is.Empty);
    }

    // ══════════════════════════════════════════════════════════════════════════ Move-out
    // ══════════════════════════════════════════════════════════════════════════

    private MoveOutService BuildMoveOut() => new(_context, _rotation, _ledger);

    [Test]
    public async Task OutstandingBalances_BlockAMoveOutUntilTheyAreDealtWith()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddExpenseAsync("anna", 4000, ("anna", 2000), ("ben", 2000));

        var outstanding = await BuildMoveOut().GetOutstandingBalancesAsync(GuildId, "ben");

        Assert.Multiple(() =>
        {
            Assert.That(outstanding, Has.Count.EqualTo(1));
            Assert.That(outstanding[0].NetMinor, Is.EqualTo(-2000));
            Assert.That(outstanding[0].ChannelId, Is.EqualTo(LedgerChannelId));
        });
    }

    [Test]
    public async Task MoveOut_WriteOff_ZeroesTheLeaverAndLeavesTheRestSummingToZero()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddExpenseAsync("anna", 4000, ("anna", 2000), ("ben", 2000));

        var summary = await BuildMoveOut().StageAsync(GuildId, "ben", OwnerId, writeOffBalances: true);
        await _context.SaveChangesAsync();

        var balances = await _ledger.GetBalancesAsync(LedgerChannelId);

        Assert.Multiple(() =>
        {
            Assert.That(summary.BalancesWrittenOff, Has.Count.EqualTo(1));
            Assert.That(summary.BalancesWrittenOff[0].AmountMinor, Is.EqualTo(2000));
            Assert.That(balances.Any(b => b.UserId == "ben"), Is.False, "the leaver is square");
            Assert.That(balances.Sum(b => b.NetMinor), Is.Zero, "and the ledger still reconciles");
        });
    }

    [Test]
    public async Task MoveOut_HandsUnfinishedChoresToSomebodyElse()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        var occurrence = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddDays(1), "ben");
        occurrence.RemindedAt = DateTimeOffset.UtcNow;
        _context.ChoreOccurrences.Add(occurrence);
        await _context.SaveChangesAsync();

        var summary = await BuildMoveOut().StageAsync(GuildId, "ben", OwnerId, writeOffBalances: false);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(summary.ChoresReassigned, Is.EqualTo(1));
            Assert.That(occurrence.AssignedUserId, Is.EqualTo("anna"));
            Assert.That(occurrence.RemindedAt, Is.Null,
                "the new assignee has to actually be told, not inherit the leaver's reminder");
        });
    }

    [Test]
    public async Task MoveOut_DropsChoresWhenNobodyIsLeftInTheRota()
    {
        await SeedGuildAsync();
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        _context.ChoreOccurrences.Add(ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddDays(1), "ben"));
        await _context.SaveChangesAsync();

        var summary = await BuildMoveOut().StageAsync(GuildId, "ben", OwnerId, writeOffBalances: false);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(summary.ChoresDropped, Is.EqualTo(1));
            Assert.That(_context.ChoreOccurrences.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task MoveOut_LeavesCompletedHistoryAlone()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        var done = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddDays(-3), "ben");
        done.CompletedAt = DateTimeOffset.UtcNow.AddDays(-3);
        _context.ChoreOccurrences.Add(done);
        await _context.SaveChangesAsync();

        await BuildMoveOut().StageAsync(GuildId, "ben", OwnerId, writeOffBalances: false);
        await _context.SaveChangesAsync();

        Assert.That(done.AssignedUserId, Is.EqualTo("ben"),
            "rewriting history would change everyone else's fairness balance on the day someone leaves");
    }

    [Test]
    public async Task MoveOut_PausesChoresThatNamedThemPersonally()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChoresChannelId, GuildId = GuildId, Title = "Ben's plants",
            IntervalDays = 7, AnchorAt = DateTimeOffset.UtcNow, EffortMinutes = 10,
            FixedAssigneeUserId = "ben",
        });
        _context.Chores.Add(chore);
        await _context.SaveChangesAsync();

        var summary = await BuildMoveOut().StageAsync(GuildId, "ben", OwnerId, writeOffBalances: false);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(summary.ChoresPaused, Is.EqualTo(1));
            Assert.That(chore.IsPaused, Is.True,
                "silently rotating a fixed chore onto someone else invents an obligation nobody agreed to");
        });
    }

    [Test]
    public async Task MoveOut_UnassignsTheirOpenListItems()
    {
        await SeedGuildAsync();
        await AddMemberAsync("ben");

        var item = ListItem.Create(new CreateListItemParams
        {
            ChannelId = ListChannelId, GuildId = GuildId, Text = "Bin bags",
            AssigneeUserId = "ben", AddedByUserId = "ben", Position = 0,
        });
        _context.ListItems.Add(item);
        await _context.SaveChangesAsync();

        var summary = await BuildMoveOut().StageAsync(GuildId, "ben", OwnerId, writeOffBalances: false);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(summary.ListItemsUnassigned, Is.EqualTo(1));
            Assert.That(item.AssigneeUserId, Is.Null);
        });
    }
}
