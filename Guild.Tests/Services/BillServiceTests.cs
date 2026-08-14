using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>Generation, posting and announcement of recurring bills.</summary>
[TestFixture]
public class BillServiceTests
{
    private const string GuildId = "guild-1";
    private const string ChannelId = "chan-ledger";
    private const string EveryoneRoleId = "role-everyone";
    private const string FlatmateRoleId = "role-flatmates";

    /// <summary>Owner, so also implicitly every permission there is.</summary>
    private const string Anna = "anna";

    private const string Ben = "ben";
    private const string Cara = "cara";

    /// <summary>Holds ManageLedger through the Flatmates role rather than through ownership, which
    /// is what makes the "who can act on this" assertions mean anything.</summary>
    private const string Dan = "dan";

    private FakeDistributedCache _cache = null!;
    private BillTestContext _context = null!;
    private GuildPermissionService _permissions = null!;
    private LedgerService _ledger = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new BillTestContext(Guid.NewGuid().ToString());
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _ledger = new LedgerService(_context);
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>Stands in for the DbSets and configuration the integrator adds to
    /// <see cref="MicroserviceContext"/>. Self-removing: once the real configuration lands, the
    /// entity types are already in the model and this override does nothing, so it never ends up
    /// declaring the same relationship twice.</summary>
    private sealed class BillTestContext : MicroserviceContext
    {
        public BillTestContext(string dbName)
            : base(new DbContextOptionsBuilder<MicroserviceContext>()
                .UseInMemoryDatabase(dbName)
                .Options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Left empty for the same reason TestGuildContext's is: the InMemory provider is
            // configured through the constructor and calling base would add a conflicting one.
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            if (modelBuilder.Model.FindEntityType(typeof(RecurringExpense)) is not null) return;

            modelBuilder.Entity<RecurringExpense>(templateBuilder =>
            {
                templateBuilder.HasOne<Channel>()
                    .WithMany()
                    .HasForeignKey(x => x.ChannelId)
                    .OnDelete(DeleteBehavior.Cascade);

                templateBuilder.HasIndex(x => x.ChannelId);
                templateBuilder.HasIndex(x => x.NextDueAt);
            });

            modelBuilder.Entity<RecurringExpenseShare>(shareBuilder =>
            {
                shareBuilder.HasKey(x => new { x.RecurringExpenseId, x.UserId });

                shareBuilder.HasOne(x => x.RecurringExpense)
                    .WithMany(x => x.Shares)
                    .HasForeignKey(x => x.RecurringExpenseId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<BillOccurrence>(occurrenceBuilder =>
            {
                occurrenceBuilder.HasOne<RecurringExpense>()
                    .WithMany()
                    .HasForeignKey(x => x.RecurringExpenseId)
                    .OnDelete(DeleteBehavior.Cascade);

                occurrenceBuilder.HasIndex(x => new { x.ChannelId, x.DueAt });
                occurrenceBuilder.HasIndex(x => new { x.RecurringExpenseId, x.DueAt }).IsUnique();
            });
        }
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task SeedAsync(GuildFeatures features = GuildFeaturePresets.Household)
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = Anna, Name = "The Flat", Features = features,
            Kind = GuildKind.Household,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Type = RoleType.Everyone, Name = "Everyone",
            Permissions = Role.DefaultEveryonePermissions, ModulePermissions = Role.DefaultEveryoneModulePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = FlatmateRoleId, GuildId = GuildId, Type = RoleType.None, Name = "Flatmates",
            ModulePermissions = Role.FlatmatePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChannelId, GuildId = GuildId, Name = "money", Type = ChannelType.Ledger,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var userId in new[] { Anna, Ben, Cara, Dan })
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = $"member-{userId}", GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
                SearchValue = userId.ToUpperInvariant(),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-everyone-{userId}", RoleId = EveryoneRoleId, MemberId = $"member-{userId}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-flatmate-dan", RoleId = FlatmateRoleId, MemberId = $"member-{Dan}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private async Task<RecurringExpense> AddTemplateAsync(
        long? amountMinor = 85000,
        DateTimeOffset? anchorAt = null,
        bool autoPost = false,
        ExpenseSplitKind splitKind = ExpenseSplitKind.Equal,
        int leadDays = RecurringExpense.DefaultLeadDays,
        params (string UserId, decimal ShareValue)[] shares)
    {
        var template = RecurringExpense.Create(new CreateRecurringExpenseParams
        {
            ChannelId = ChannelId, GuildId = GuildId, Description = "Rent",
            AmountMinor = amountMinor, PayerUserId = Anna, SplitKind = splitKind,
            Category = ExpenseCategory.Rent, RecurrenceUnit = RecurrenceUnit.Month,
            RecurrenceInterval = 1, AnchorAt = anchorAt ?? DateTimeOffset.UtcNow,
            LeadDays = leadDays, AutoPost = autoPost, CreatedByUserId = Anna,
        });

        foreach (var (userId, shareValue) in shares)
        {
            template.Shares.Add(new RecurringExpenseShare
            {
                RecurringExpenseId = template.Id, UserId = userId, ShareValue = shareValue,
            });
        }

        _context.Set<RecurringExpense>().Add(template);
        await _context.SaveChangesAsync();
        return template;
    }

    private BillAlertService BuildAlerts() =>
        new(_context,
            new HouseholdNotifier(_context, new NotificationResolutionService(_context), _hub, _bus),
            _ledger, _permissions, NullLogger<BillAlertService>.Instance);

    private BillService Build() =>
        new(_context, _ledger, BuildAlerts(), _permissions, NullLogger<BillService>.Instance);

    private List<HouseholdPushRequested> Pushes() => _bus.Published.OfType<HouseholdPushRequested>().ToList();

    // ══════════════════════════════════════════════════════════════════════════ Generation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task StageNextOccurrence_InsideTheLeadWindow_GeneratesOneBill()
    {
        await SeedAsync();
        var template = await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(2));

        var occurrence = await Build().StageNextOccurrenceAsync(template, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(occurrence, Is.Not.Null);
            Assert.That(occurrence!.AmountMinor, Is.EqualTo(85000), "a fixed amount is snapshotted");
            Assert.That(occurrence.Description, Is.EqualTo("Rent"), "denormalized for the board");
            Assert.That(occurrence.Status, Is.EqualTo(BillStatus.Pending));
            Assert.That(template.NextDueAt, Is.GreaterThan(occurrence.DueAt), "the schedule moved on");
        });
    }

    [Test]
    public async Task StageNextOccurrence_OutsideTheLeadWindow_GeneratesNothingYet()
    {
        await SeedAsync();
        var template = await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(20), leadDays: 5);

        var occurrence = await Build().StageNextOccurrenceAsync(template, DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(occurrence, Is.Null);
            Assert.That(template.NextDueAt, Is.EqualTo(template.AnchorAt),
                "the slot stays put rather than being consumed");
        });
    }

    /// <summary>The guard the unique (template, due date) index exists for: the sweep and the create
    /// endpoint both generate, and exactly one of them must win.</summary>
    [Test]
    public async Task StageNextOccurrence_RunTwiceForTheSameSlot_IsIdempotent()
    {
        await SeedAsync();
        var template = await AddTemplateAsync();
        var service = Build();

        var first = await service.StageNextOccurrenceAsync(template, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        // Rewound as if a second worker read the template before the first committed.
        template.NextDueAt = first!.DueAt;

        var second = await service.StageNextOccurrenceAsync(template, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Null);
            Assert.That(_context.Set<BillOccurrence>().Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task StageNextOccurrence_PausedTemplate_GeneratesNothing()
    {
        await SeedAsync();
        var template = await AddTemplateAsync();
        template.IsPaused = true;

        Assert.That(await Build().StageNextOccurrenceAsync(template, DateTimeOffset.UtcNow), Is.Null);
    }

    /// <summary>The collapse that stops a schedule entered six months late from emitting six bills -
    /// which with AutoPost set would be six charges nobody agreed to.</summary>
    [Test]
    public async Task StageNextOccurrence_ABacklog_EmitsOnlyTheCurrentPeriod()
    {
        await SeedAsync();
        var template = await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddMonths(-6));

        var occurrence = await Build().StageNextOccurrenceAsync(template, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.Set<BillOccurrence>().Count(), Is.EqualTo(1));
            Assert.That(occurrence!.DueAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddMonths(-1)),
                "and it is this period's, not one from six months ago");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Posting
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<(RecurringExpense Template, BillOccurrence Bill)> AddDueBillAsync(
        long? amountMinor = 85000,
        bool autoPost = false,
        ExpenseSplitKind splitKind = ExpenseSplitKind.Equal,
        params (string UserId, decimal ShareValue)[] shares)
    {
        var template = await AddTemplateAsync(
            amountMinor, DateTimeOffset.UtcNow.AddMinutes(-1), autoPost, splitKind,
            RecurringExpense.DefaultLeadDays, shares);

        var bill = await Build().StageNextOccurrenceAsync(template, DateTimeOffset.UtcNow);
        await _context.SaveChangesAsync();

        return (template, bill!);
    }

    [Test]
    public async Task Post_CreatesOneExpenseSplitTheWayTheLedgerWould()
    {
        await SeedAsync();
        var (template, bill) = await AddDueBillAsync();

        var result = await Build().PostAsync(bill, template, null, null, Anna);
        await _context.SaveChangesAsync();

        var expected = ExpenseSplitter.Split(85000, ExpenseSplitKind.Equal,
            new[] { Anna, Ben, Cara, Dan }.Select(id => new SplitParticipant(id, 1)).ToList());

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(_context.Expenses.Count(), Is.EqualTo(1));
            Assert.That(result.Expense!.BillOccurrenceId, Is.EqualTo(bill.Id));
            Assert.That(result.Expense.Category, Is.EqualTo(ExpenseCategory.Rent),
                "the template's category travels with the expense");
            Assert.That(result.Expense.OccurredAt, Is.EqualTo(bill.DueAt),
                "a bill posted late still lands in the period it belongs to");

            Assert.That(
                result.Expense.Shares.OrderBy(s => s.UserId, StringComparer.Ordinal)
                    .Select(s => (s.UserId, s.AmountMinor)),
                Is.EqualTo(expected.OrderBy(s => s.UserId, StringComparer.Ordinal)
                    .Select(s => (s.UserId, s.AmountMinor))));

            Assert.That(result.Expense.Shares.Sum(s => s.AmountMinor), Is.EqualTo(85000),
                "shares that do not sum to the total are how a ledger stops reconciling");

            Assert.That(bill.Status, Is.EqualTo(BillStatus.Posted));
            Assert.That(bill.ExpenseId, Is.EqualTo(result.Expense.Id));
        });
    }

    /// <summary>Double-posting rent does not look like an error - it looks like everybody owing
    /// twice as much, and somebody has to work out which of the two rows is real.</summary>
    [Test]
    public async Task Post_Twice_ReturnsTheFirstExpenseRatherThanASecond()
    {
        await SeedAsync();
        var (template, bill) = await AddDueBillAsync();
        var service = Build();

        var first = await service.PostAsync(bill, template, null, null, Anna);
        await _context.SaveChangesAsync();

        var second = await service.PostAsync(bill, template, null, null, Ben);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(second.Error, Is.Null);
            Assert.That(second.Expense!.Id, Is.EqualTo(first.Expense!.Id));
            Assert.That(_context.Expenses.Count(), Is.EqualTo(1));
            Assert.That(bill.PostedByUserId, Is.EqualTo(Anna), "the second attempt did not reassign it");
        });
    }

    [Test]
    public async Task Post_AVariableBillWithNoAmount_IsRefused()
    {
        await SeedAsync();
        var (template, bill) = await AddDueBillAsync(amountMinor: null);

        var result = await Build().PostAsync(bill, template, null, null, Anna);

        Assert.Multiple(() =>
        {
            Assert.That(result.Expense, Is.Null);
            Assert.That(result.Error, Does.Contain("needs an amount"));
            Assert.That(bill.Status, Is.EqualTo(BillStatus.Pending), "and it stays waiting");
            Assert.That(_context.Expenses.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task Post_AVariableBillWithAnAmount_Succeeds()
    {
        await SeedAsync();
        var (template, bill) = await AddDueBillAsync(amountMinor: null);

        var result = await Build().PostAsync(bill, template, 12000, null, Anna);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(result.Expense!.AmountMinor, Is.EqualTo(12000));
            Assert.That(bill.AmountMinor, Is.EqualTo(12000), "the figure is kept on the bill too");
        });
    }

    /// <summary>A payer who is not a member is a creditor nobody can pay off - the balance still
    /// sums to zero, it just does so across somebody who does not live there.</summary>
    [Test]
    public async Task Post_WithAPayerWhoLeftTheGuild_IsRefused()
    {
        await SeedAsync();
        var (template, bill) = await AddDueBillAsync();

        template.PayerUserId = "someone-who-moved-out";

        var result = await Build().PostAsync(bill, template, null, null, Anna);

        Assert.Multiple(() =>
        {
            Assert.That(result.Expense, Is.Null);
            Assert.That(result.Error, Does.Contain("member of this guild"));
            Assert.That(_context.Expenses.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task Post_ASkippedBill_IsRefused()
    {
        await SeedAsync();
        var (template, bill) = await AddDueBillAsync();
        bill.Status = BillStatus.Skipped;

        var result = await Build().PostAsync(bill, template, null, null, Anna);

        Assert.That(result.Error, Does.Contain("skipped"));
    }

    [Test]
    public async Task Post_WithWeightedShares_MatchesTheSplitter()
    {
        await SeedAsync();

        // Anna has the big room and counts double.
        var (template, bill) = await AddDueBillAsync(
            amountMinor: 100000, splitKind: ExpenseSplitKind.Shares,
            shares: [(Anna, 2m), (Ben, 1m), (Cara, 1m)]);

        var result = await Build().PostAsync(bill, template, null, null, Anna);
        await _context.SaveChangesAsync();

        var expected = ExpenseSplitter.Split(100000, ExpenseSplitKind.Shares,
            [new SplitParticipant(Anna, 2m), new SplitParticipant(Ben, 1m), new SplitParticipant(Cara, 1m)]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Error, Is.Null);
            Assert.That(
                result.Expense!.Shares.OrderBy(s => s.UserId, StringComparer.Ordinal)
                    .Select(s => (s.UserId, s.AmountMinor)),
                Is.EqualTo(expected.OrderBy(s => s.UserId, StringComparer.Ordinal)
                    .Select(s => (s.UserId, s.AmountMinor))));

            Assert.That(result.Expense.Shares.Any(s => s.UserId == Dan), Is.False,
                "a named split does not silently pick up whoever else lives here");
        });
    }

    /// <summary>The empty-shares convention, resolved at post time rather than at template time so a
    /// flatmate who moved in since the schedule was written is included without anybody editing
    /// it.</summary>
    [Test]
    public void Participants_EmptyEqualSplit_IsEveryone()
    {
        var template = RecurringExpense.Create(new CreateRecurringExpenseParams
        {
            ChannelId = ChannelId, GuildId = GuildId, Description = "Rent", AmountMinor = 1000,
            PayerUserId = Anna, AnchorAt = DateTimeOffset.UtcNow, CreatedByUserId = Anna,
        });

        var participants = BillService.Participants(template, [Ben, Anna, Cara]);

        Assert.Multiple(() =>
        {
            Assert.That(participants.Select(p => p.UserId), Is.EqualTo(new[] { Anna, Ben, Cara }),
                "ordinal order, so the split is deterministic");
            Assert.That(participants.All(p => p.ShareValue == 1), Is.True);
        });
    }

    [Test]
    public void Participants_EmptyNonEqualSplit_ResolvesToNobody()
    {
        var template = RecurringExpense.Create(new CreateRecurringExpenseParams
        {
            ChannelId = ChannelId, GuildId = GuildId, Description = "Rent", AmountMinor = 1000,
            PayerUserId = Anna, SplitKind = ExpenseSplitKind.Shares,
            AnchorAt = DateTimeOffset.UtcNow, CreatedByUserId = Anna,
        });

        Assert.That(BillService.Participants(template, [Anna, Ben]), Is.Empty,
            "a weight nobody supplied cannot be guessed, and guessing divides money wrongly");
    }

    // ══════════════════════════════════════════════════════════════════════════ Sweep
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Sweep_GeneratesAndAnnouncesInOnePass()
    {
        await SeedAsync();
        await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(3));

        await Build().SweepAsync();

        var bill = _context.Set<BillOccurrence>().Single();
        var push = Pushes().Single();

        Assert.Multiple(() =>
        {
            Assert.That(bill.RemindedAt, Is.Not.Null, "generated and announced on the same pass");
            Assert.That(push.Kind, Is.EqualTo(BillAlertService.KindBillDue));
            Assert.That(push.Title, Is.EqualTo("Rent"));
            Assert.That(push.Body, Is.EqualTo("Due in 3 days. Your share is CHF 212.50."));
            Assert.That(push.BodyLocArgs, Is.EqualTo(new[] { "3 days", "CHF 212.50" }),
                "both arguments preformatted - the phone resolving the key has no formatter");
        });
    }

    [Test]
    public async Task Sweep_ABillDueToday_SaysSo()
    {
        await SeedAsync();
        await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddHours(4));

        await Build().SweepAsync();

        var push = Pushes().Single();

        Assert.Multiple(() =>
        {
            Assert.That(push.Body, Is.EqualTo("Due today. Your share is CHF 212.50."));
            Assert.That(push.BodyLocArgs, Is.EqualTo(new[] { "CHF 212.50" }));
        });
    }

    /// <summary>RemindedAt is what makes the announcement at-most-once.</summary>
    [Test]
    public async Task Sweep_RunTwice_AnnouncesOnce()
    {
        await SeedAsync();
        await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(3));

        var service = Build();
        await service.SweepAsync();
        await service.SweepAsync();

        Assert.That(Pushes(), Has.Count.EqualTo(1));
    }

    /// <summary>Otherwise a service returning from an outage buzzes the whole house about bills it
    /// has been staring at for a fortnight.</summary>
    [Test]
    public async Task Sweep_ABillLongPastDue_IsStampedWithoutBeingAnnounced()
    {
        await SeedAsync();
        var template = await AddTemplateAsync();

        var stale = BillOccurrence.Create(template, DateTimeOffset.UtcNow.AddDays(-30));
        _context.Set<BillOccurrence>().Add(stale);
        await _context.SaveChangesAsync();

        await Build().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(stale.RemindedAt, Is.Not.Null, "stamped, so it leaves the candidate set");
            Assert.That(Pushes().Any(p => p.TargetId == stale.Id), Is.False);
        });
    }

    [Test]
    public async Task Sweep_AVariableBillWithNoAmount_AsksThePeopleWhoCanPostIt()
    {
        await SeedAsync();
        await AddTemplateAsync(amountMinor: null, anchorAt: DateTimeOffset.UtcNow.AddHours(1));

        await Build().SweepAsync();

        var push = Pushes().Single();

        Assert.Multiple(() =>
        {
            Assert.That(push.Kind, Is.EqualTo(BillAlertService.KindBillNeedsAmount));
            Assert.That(push.Body, Is.EqualTo("A bill needs an amount before it can be split."));
            Assert.That(push.UserIds, Is.EquivalentTo(new[] { Anna, Dan }),
                "the owner and the Flatmates role hold ManageLedger; telling anybody else would be "
                + "telling them about a button they do not have");
        });
    }

    [Test]
    public async Task Sweep_AutoPostsAFixedBillOnceItIsDue()
    {
        await SeedAsync();
        await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddMinutes(-1), autoPost: true);

        await Build().SweepAsync();

        var bill = _context.Set<BillOccurrence>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(bill.Status, Is.EqualTo(BillStatus.Posted));
            Assert.That(bill.PostedByUserId, Is.Null, "nobody did it, and the row should not claim otherwise");
            Assert.That(_context.Expenses.Count(), Is.EqualTo(1));
            Assert.That(Pushes().Select(p => p.Kind), Has.Member(BillAlertService.KindBillPosted));
            Assert.That(Pushes().First(p => p.Kind == BillAlertService.KindBillPosted).Body,
                Is.EqualTo("Added to the ledger. Your share is CHF 212.50."));
        });
    }

    [Test]
    public async Task Sweep_DoesNotAutoPostABillThatIsNotDueYet()
    {
        await SeedAsync();
        await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(4), autoPost: true);

        await Build().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.Set<BillOccurrence>().Single().Status, Is.EqualTo(BillStatus.Pending),
                "putting February's rent in January's balance is not what the board is for");
            Assert.That(_context.Expenses.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task Sweep_WithTheLedgerModuleOff_DoesNothing()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Ledger);
        await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(3));

        await Build().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(_context.Set<BillOccurrence>().Count(), Is.Zero);
            Assert.That(Pushes(), Is.Empty);
        });
    }

    [Test]
    public async Task Sweep_APausedTemplate_GeneratesNothing()
    {
        await SeedAsync();
        var template = await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(3));
        template.IsPaused = true;
        await _context.SaveChangesAsync();

        await Build().SweepAsync();

        Assert.That(_context.Set<BillOccurrence>().Count(), Is.Zero);
    }

    /// <summary>Quiet hours were built for exactly this: an announcement that would land at 03:00 is
    /// held rather than sent, and stays unstamped so the next sweep after the window picks it
    /// up.</summary>
    [Test]
    public async Task Sweep_InsideQuietHours_HoldsTheAnnouncement()
    {
        await SeedAsync();
        await AddTemplateAsync(anchorAt: DateTimeOffset.UtcNow.AddDays(3));

        // A two-hour window starting now, so the test asserts the same thing whatever time of day
        // it runs at - including across the midnight wrap the window is allowed to make.
        var minuteOfDay = DateTimeOffset.UtcNow.Hour * 60 + DateTimeOffset.UtcNow.Minute;

        _context.GuildQuietHoursConfigs.Add(new GuildQuietHoursConfig
        {
            GuildId = GuildId, Enabled = true,
            StartMinuteLocal = minuteOfDay, EndMinuteLocal = (minuteOfDay + 120) % 1440,
            TimeZoneId = "UTC",
        });
        await _context.SaveChangesAsync();

        await Build().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Is.Empty);
            Assert.That(_context.Set<BillOccurrence>().Single().RemindedAt, Is.Null,
                "left unstamped, or the deferral would swallow the announcement entirely");
        });
    }

    [Test]
    public void DescribeDuration_ReadsLikeSomethingAPersonWouldSay()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BillAlertService.DescribeDuration(TimeSpan.FromDays(3)), Is.EqualTo("3 days"));
            Assert.That(BillAlertService.DescribeDuration(TimeSpan.FromHours(25)), Is.EqualTo("1 day"));
            Assert.That(BillAlertService.DescribeDuration(TimeSpan.FromHours(1)), Is.EqualTo("1 day"),
                "never 'in 0 days' - the floor is the smallest thing worth saying");
        });
    }
}
