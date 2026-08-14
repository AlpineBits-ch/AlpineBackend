using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>An InMemory context that also knows about <see cref="MemberAbsence"/>.</summary>
internal sealed class AbsenceTestContext(string dbName) : MicroserviceContext(
    new DbContextOptionsBuilder<MicroserviceContext>().UseInMemoryDatabase(dbName).Options)
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Left empty for the reason TestGuildContext leaves it empty: calling base would add a
        // second, conflicting Postgres provider.
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        if (modelBuilder.Model.FindEntityType(typeof(MemberAbsence)) is null)
            modelBuilder.Entity<MemberAbsence>();
    }
}

/// <summary>
/// Absence: the rules on declaring one, and the three things the rota does about it - stops
/// assigning you work you cannot do, stops counting your holiday against you, and hands over what
/// you were already holding.
/// </summary>
[TestFixture]
public class AbsenceServiceTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string ChoresChannelId = "chan-chores";
    private const string EveryoneRoleId = "role-everyone";
    private const string RotationRoleId = "role-flatmates";

    private FakeDistributedCache _cache = null!;
    private AbsenceTestContext _context = null!;
    private GuildPermissionService _permissions = null!;
    private ChoreRotationService _rotation = null!;
    private AbsenceService _absences = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private AbsenceEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new AbsenceTestContext(Guid.NewGuid().ToString());
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _rotation = new ChoreRotationService(_context, _permissions);
        _absences = new AbsenceService(_context, _rotation);
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _endpoint = new AbsenceEndpoint();
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
            ModulePermissions = Role.DefaultEveryoneModulePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = RotationRoleId, GuildId = GuildId, Type = RoleType.None, Name = "Flatmates",
            Permissions = Permissions.None,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Channels.Add(new Channel
        {
            Id = ChoresChannelId, GuildId = GuildId, Name = "chores", Type = ChannelType.Chores,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private async Task AddMemberAsync(string userId, bool inRotationRole = true)
    {
        var memberId = $"member-{userId}";

        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId.ToUpperInvariant(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.RoleMembers.Add(new RoleMember
        {
            Id = $"rm-everyone-{userId}", RoleId = EveryoneRoleId, MemberId = memberId,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        if (inRotationRole)
        {
            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-rota-{userId}", RoleId = RotationRoleId, MemberId = memberId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    private async Task<Chore> AddChoreAsync(string title = "Bins", int effortMinutes = 30)
    {
        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChoresChannelId, GuildId = GuildId, Title = title,
            IntervalDays = 7, AnchorAt = DateTimeOffset.UtcNow, EffortMinutes = effortMinutes,
            RotationRoleId = RotationRoleId,
        });
        _context.Chores.Add(chore);
        await _context.SaveChangesAsync();
        return chore;
    }

    private async Task<MemberAbsence> AddAbsenceAsync(string userId, double fromDays, double toDays)
    {
        // Both ends off one instant.
        var now = DateTimeOffset.UtcNow;

        var absence = MemberAbsence.Create(new CreateMemberAbsenceParams
        {
            GuildId = GuildId, UserId = userId, CreatedByUserId = userId,
            StartAt = now.AddDays(fromDays),
            EndAt = now.AddDays(toDays),
        });
        _context.Set<MemberAbsence>().Add(absence);
        await _context.SaveChangesAsync();
        return absence;
    }

    private async Task<ChoreOccurrence> AddOccurrenceAsync(
        Chore chore, string userId, double dueInDays, bool completed = false, bool skipped = false)
    {
        var occurrence = ChoreOccurrence.Create(chore, DateTimeOffset.UtcNow.AddDays(dueInDays), userId);
        if (completed)
        {
            occurrence.CompletedAt = DateTimeOffset.UtcNow;
            occurrence.CompletedByUserId = userId;
        }

        if (skipped) occurrence.SkippedAt = DateTimeOffset.UtcNow;

        _context.ChoreOccurrences.Add(occurrence);
        await _context.SaveChangesAsync();
        return occurrence;
    }

    private HouseholdChannelService BuildHousehold() => new(
        _context, _permissions,
        new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance),
        new ChannelAudienceService(_permissions, new MemoryCache(new MemoryCacheOptions())),
        _hub);

    private ChoreAlertService BuildChoreAlerts() => new(
        new HouseholdNotifier(_context, new NotificationResolutionService(_context), _hub, _bus),
        _permissions,
        NullLogger<ChoreAlertService>.Instance);

    // ══════════════════════════════════════════════════════════════════════════ Validation
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Validate_AnOrdinaryHoliday_IsAccepted()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        var error = await _absences.ValidateAsync(GuildId, "anna",
            DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow.AddDays(17), "Lisbon", null);

        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task Validate_EndBeforeStart_IsRejected()
    {
        await SeedGuildAsync();

        var error = await _absences.ValidateAsync(GuildId, "anna",
            DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow.AddDays(3), null, null);

        Assert.That(error, Does.Contain("after"));
    }

    [Test]
    public async Task Validate_ZeroLengthWindow_IsRejected()
    {
        await SeedGuildAsync();
        var start = DateTimeOffset.UtcNow.AddDays(3);

        Assert.That(await _absences.ValidateAsync(GuildId, "anna", start, start, null, null),
            Is.Not.Null, "an absence covering no time at all is a client bug, not a holiday");
    }

    [Test]
    public async Task Validate_LongerThanTheCap_IsRejected()
    {
        await SeedGuildAsync();

        var error = await _absences.ValidateAsync(GuildId, "anna",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(AbsenceService.MaxAbsenceDays + 1), null, null);

        Assert.That(error, Does.Contain("longer"),
            "past half a year the honest flow is move-out, which also settles their ledger");
    }

    [Test]
    public async Task Validate_NoteTooLong_IsRejected()
    {
        await SeedGuildAsync();

        var error = await _absences.ValidateAsync(GuildId, "anna",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(3),
            new string('x', AbsenceService.MaxNoteLength + 1), null);

        Assert.That(error, Does.Contain("Note"));
    }

    /// <summary>Rejected rather than merged.</summary>
    [Test]
    public async Task Validate_OverlappingAbsence_IsRejectedNotMerged()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddAbsenceAsync("anna", 5, 12);

        var error = await _absences.ValidateAsync(GuildId, "anna",
            DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow.AddDays(20), null, null);

        Assert.Multiple(async () =>
        {
            Assert.That(error, Does.Contain("overlap"));
            Assert.That(await _context.Set<MemberAbsence>().CountAsync(), Is.EqualTo(1),
                "and nothing was quietly rewritten");
        });
    }

    /// <summary>Abutting windows are two trips, not one collision - flying home and away again the
    /// same day has to stay expressible.</summary>
    [Test]
    public async Task Validate_AbuttingAbsence_IsAccepted()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var first = await AddAbsenceAsync("anna", 5, 12);

        var error = await _absences.ValidateAsync(GuildId, "anna", first.EndAt, first.EndAt.AddDays(4), null, null);

        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task Validate_OverlappingSomebodyElsesAbsence_IsAccepted()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddAbsenceAsync("ben", 5, 12);

        var error = await _absences.ValidateAsync(GuildId, "anna",
            DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(12), null, null);

        Assert.That(error, Is.Null, "a whole house can go away at the same time");
    }

    /// <summary>Editing a row must not collide with itself.</summary>
    [Test]
    public async Task Validate_IgnoresTheAbsenceBeingEdited()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var absence = await AddAbsenceAsync("anna", 5, 12);

        var error = await _absences.ValidateAsync(GuildId, "anna",
            absence.StartAt, absence.EndAt.AddDays(2), null, ignoreAbsenceId: absence.Id);

        Assert.That(error, Is.Null);
    }

    [Test]
    public async Task Validate_PastTheLiveAbsenceCap_IsRejected()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        for (var i = 0; i < AbsenceService.MaxLiveAbsencesPerMember; i++)
            await AddAbsenceAsync("anna", 2 + i * 3, 3 + i * 3);

        var error = await _absences.ValidateAsync(GuildId, "anna",
            DateTimeOffset.UtcNow.AddDays(500), DateTimeOffset.UtcNow.AddDays(502), null, null);

        Assert.That(error, Does.Contain("upcoming"));
    }

    /// <summary>The cap counts what is still ahead.</summary>
    [Test]
    public async Task Validate_PastAbsences_DoNotCountTowardTheCap()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        for (var i = 0; i < AbsenceService.MaxLiveAbsencesPerMember + 5; i++)
            await AddAbsenceAsync("anna", -(400 + i * 3), -(398 + i * 3));

        var error = await _absences.ValidateAsync(GuildId, "anna",
            DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow.AddDays(6), null, null);

        Assert.That(error, Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Effect 1: the rotation stops assigning work to people who are away
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RotationPool_ExcludesSomebodyAwayOnTheDueDate()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        await AddAbsenceAsync("anna", 1, 10);

        var pool = await _rotation.GetRotationPoolAsync(chore, DateTimeOffset.UtcNow.AddDays(5));

        Assert.That(pool, Is.EquivalentTo(new[] { "ben" }));
    }

    [Test]
    public async Task RotationPool_KeepsSomebodyWhoseAbsenceEndsBeforeTheDueDate()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        await AddAbsenceAsync("anna", 1, 4);

        var pool = await _rotation.GetRotationPoolAsync(chore, DateTimeOffset.UtcNow.AddDays(9));

        Assert.That(pool, Is.EquivalentTo(new[] { "anna", "ben" }),
            "she is back by then, so the rota may still land on her");
    }

    /// <summary>The half of the rule that stops the feature from breaking the rota.</summary>
    [Test]
    public async Task RotationPool_EverybodyAway_FallsBackToTheWholePool()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        await AddAbsenceAsync("anna", 1, 10);
        await AddAbsenceAsync("ben", 1, 10);

        var pool = await _rotation.GetRotationPoolAsync(chore, DateTimeOffset.UtcNow.AddDays(5));

        Assert.That(pool, Is.EquivalentTo(new[] { "anna", "ben" }));
    }

    /// <summary>Without the Presence module there is no absence board, so the rotation must behave
    /// exactly as it did before absences existed - the same degrade the restock alert makes.</summary>
    [Test]
    public async Task RotationPool_WithPresenceDisabled_IgnoresAbsencesEntirely()
    {
        await SeedGuildAsync(GuildFeaturePresets.Household & ~GuildFeatures.Presence);
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        await AddAbsenceAsync("anna", 1, 10);

        var pool = await _rotation.GetRotationPoolAsync(chore, DateTimeOffset.UtcNow.AddDays(5));

        Assert.That(pool, Is.EquivalentTo(new[] { "anna", "ben" }));
    }

    /// <summary>Asked without a date, the pool is the whole rota.</summary>
    [Test]
    public async Task RotationPool_WithoutADate_IncludesEverybody()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        await AddAbsenceAsync("anna", -1, 10);

        Assert.That(await _rotation.GetRotationPoolAsync(chore), Is.EquivalentTo(new[] { "anna", "ben" }));
    }

    [Test]
    public async Task PickNextAssignee_SkipsTheMemberWhoIsAwayEvenWhenTheyAreLightest()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        // Ben has carried the house; on load alone the next one is Anna's.
        await AddOccurrenceAsync(chore, "ben", -3, completed: true);
        await AddAbsenceAsync("anna", 1, 10);

        var assignee = await _rotation.PickNextAssigneeAsync(chore, dueAt: DateTimeOffset.UtcNow.AddDays(5));

        Assert.That(assignee, Is.EqualTo("ben"), "a chore nobody can do is not a fair chore");
    }

    [Test]
    public async Task StageNextOccurrence_AssignsForTheDueDateNotForToday()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        // Due a week out, and Anna is away that week but here today.
        chore.NextDueAt = DateTimeOffset.UtcNow.AddDays(7);
        await AddAbsenceAsync("anna", 5, 12);
        await AddOccurrenceAsync(chore, "ben", -3, completed: true);

        var occurrence = await _rotation.StageNextOccurrenceAsync(chore);
        await _context.SaveChangesAsync();

        Assert.That(occurrence!.AssignedUserId, Is.EqualTo("ben"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Effect 2: the fairness balance stops charging you for your holiday
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The safety property of the whole change.</summary>
    [Test]
    public async Task WeightedBalance_WithNobodyAway_IsIdenticalToTheFlatAverage()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");
        var chore = await AddChoreAsync();

        await AddOccurrenceAsync(chore, "anna", -5, completed: true);
        await AddOccurrenceAsync(chore, "anna", -4, completed: true);
        await AddOccurrenceAsync(chore, "ben", -3, completed: true);

        var plain = await _rotation.GetBalancesAsync(GuildId, new[] { "anna", "ben", "cara" });
        var average = (int)plain.Average(b => b.CompletedMinutes);

        var weighted = await _rotation.GetWeightedBalancesAsync(GuildId, new[] { "anna", "ben", "cara" });

        Assert.Multiple(() =>
        {
            foreach (var entry in weighted)
            {
                var old = plain.Single(p => p.UserId == entry.UserId);
                Assert.That(entry.BalanceMinutes, Is.EqualTo(old.CompletedMinutes - average),
                    $"{entry.UserId}'s balance must not move when nothing was absent");
            }
        });
    }

    [Test]
    public async Task WeightedBalance_ReportsTheFullWindowAsPresentWhenNobodyIsAway()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        var weighted = await _rotation.GetWeightedBalancesAsync(GuildId, new[] { "anna", "ben" }, windowDays: 30);

        Assert.That(weighted.Select(w => w.PresentDays), Is.All.EqualTo(30));
    }

    /// <summary>The defect, stated as arithmetic.</summary>
    [Test]
    public async Task WeightedBalance_AMemberAwayHalfTheWindow_IsExpectedToDoHalfAsMuch()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        await AddOccurrenceAsync(chore, "anna", -2, completed: true);          // 30 minutes
        await AddOccurrenceAsync(chore, "ben", -3, completed: true);           // 30
        await AddOccurrenceAsync(chore, "ben", -4, completed: true);           // 30

        await AddAbsenceAsync("anna", -15, 0);

        var weighted = await _rotation.GetWeightedBalancesAsync(GuildId, new[] { "anna", "ben" }, windowDays: 30);
        var anna = weighted.Single(w => w.UserId == "anna");
        var ben = weighted.Single(w => w.UserId == "ben");

        Assert.Multiple(() =>
        {
            Assert.That(anna.PresentDays, Is.EqualTo(15));
            Assert.That(ben.PresentDays, Is.EqualTo(30));
            // 90 minutes of work, split 15:30 by presence, is 30 for Anna and 60 for Ben.
            Assert.That(anna.ExpectedMinutes, Is.EqualTo(30));
            Assert.That(anna.BalanceMinutes, Is.Zero, "she did exactly her share of the days she was here");
            Assert.That(ben.BalanceMinutes, Is.Zero);
        });
    }

    /// <summary>What the flat average used to say about the same household, kept as the record of
    /// what was wrong with it.</summary>
    [Test]
    public async Task WeightedBalance_TheSameHouseholdUnderTheOldAverage_WouldHaveReadAsSlacking()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        await AddOccurrenceAsync(chore, "anna", -2, completed: true);
        await AddOccurrenceAsync(chore, "ben", -3, completed: true);
        await AddOccurrenceAsync(chore, "ben", -4, completed: true);
        await AddAbsenceAsync("anna", -15, 0);

        var plain = await _rotation.GetBalancesAsync(GuildId, new[] { "anna", "ben" });
        var average = (int)plain.Average(b => b.CompletedMinutes);
        var oldAnnaBalance = plain.Single(p => p.UserId == "anna").CompletedMinutes - average;

        Assert.That(oldAnnaBalance, Is.LessThan(0),
            "a fortnight in Lisbon used to show up as fifteen minutes of shirking");
    }

    [Test]
    public async Task WeightedBalance_WithPresenceDisabled_IgnoresAbsences()
    {
        await SeedGuildAsync(GuildFeaturePresets.Household & ~GuildFeatures.Presence);
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddAbsenceAsync("anna", -15, 0);

        var weighted = await _rotation.GetWeightedBalancesAsync(GuildId, new[] { "anna", "ben" }, windowDays: 30);

        Assert.That(weighted.Select(w => w.PresentDays), Is.All.EqualTo(30));
    }

    /// <summary>The degenerate month.</summary>
    [Test]
    public async Task WeightedBalance_EverybodyAwayForTheWholeWindow_SplitsEvenly()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();

        await AddOccurrenceAsync(chore, "anna", -2, completed: true);
        await AddAbsenceAsync("anna", -40, 1);
        await AddAbsenceAsync("ben", -40, 1);

        var weighted = await _rotation.GetWeightedBalancesAsync(GuildId, new[] { "anna", "ben" }, windowDays: 30);

        Assert.Multiple(() =>
        {
            Assert.That(weighted.Select(w => w.PresentDays), Is.All.Zero);
            Assert.That(weighted.Select(w => w.ExpectedMinutes), Is.All.EqualTo(15));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Effect 3: the chores you were holding change hands
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Handover_MovesUnfinishedOccurrencesInsideTheWindow()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        var occurrence = await AddOccurrenceAsync(chore, "anna", 5);

        var absence = await AddAbsenceAsync("anna", 1, 10);
        var handovers = await _absences.StageChoreHandoverAsync(absence);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(handovers, Has.Count.EqualTo(1));
            Assert.That(occurrence.AssignedUserId, Is.EqualTo("ben"));
            Assert.That(occurrence.RemindedAt, Is.Null);
            Assert.That(occurrence.NudgedAt, Is.Null);
        });
    }

    [Test]
    public async Task Handover_LeavesOccurrencesOutsideTheWindowAlone()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        var before = await AddOccurrenceAsync(chore, "anna", 0.5);
        var after = await AddOccurrenceAsync(chore, "anna", 20);

        var absence = await AddAbsenceAsync("anna", 1, 10);
        var handovers = await _absences.StageChoreHandoverAsync(absence);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(handovers, Is.Empty);
            Assert.That(before.AssignedUserId, Is.EqualTo("anna"));
            Assert.That(after.AssignedUserId, Is.EqualTo("anna"));
        });
    }

    /// <summary>Completed and skipped occurrences are the fairness history.</summary>
    [Test]
    public async Task Handover_NeverTouchesCompletedOrSkippedOccurrences()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        var done = await AddOccurrenceAsync(chore, "anna", 3, completed: true);
        var skipped = await AddOccurrenceAsync(chore, "anna", 4, skipped: true);

        var absence = await AddAbsenceAsync("anna", 1, 10);
        var handovers = await _absences.StageChoreHandoverAsync(absence);
        await _context.SaveChangesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(handovers, Is.Empty);
            Assert.That(done.AssignedUserId, Is.EqualTo("anna"));
            Assert.That(skipped.AssignedUserId, Is.EqualTo("anna"));
        });
    }

    /// <summary>Unlike a move-out, an occurrence nobody else can take is kept.</summary>
    [Test]
    public async Task Handover_WithNobodyElseInTheRota_KeepsTheOccurrence()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var chore = await AddChoreAsync();
        var occurrence = await AddOccurrenceAsync(chore, "anna", 5);

        var absence = await AddAbsenceAsync("anna", 1, 10);
        var handovers = await _absences.StageChoreHandoverAsync(absence);
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(handovers, Is.Empty);
            Assert.That(occurrence.AssignedUserId, Is.EqualTo("anna"));
            Assert.That(await _context.ChoreOccurrences.CountAsync(), Is.EqualTo(1),
                "a move-out deletes these; an absence must not");
        });
    }

    [Test]
    public async Task Handover_DoesNotHandOverToAnotherAbsentee()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");
        var chore = await AddChoreAsync();
        var occurrence = await AddOccurrenceAsync(chore, "anna", 5);

        await AddAbsenceAsync("ben", 1, 10);
        var absence = await AddAbsenceAsync("anna", 1, 10);
        await _absences.StageChoreHandoverAsync(absence);
        await _context.SaveChangesAsync();

        Assert.That(occurrence.AssignedUserId, Is.EqualTo("cara"));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The endpoint: your own absences and nobody else's
    // ══════════════════════════════════════════════════════════════════════════

    private Task<IResult> CreateAsync(string callerId, CreateAbsenceDto dto) =>
        _endpoint.CreateAsync(GuildId, dto, _permissions, _absences, BuildChoreAlerts(),
            BuildHousehold(), _context, TestPrincipal.Create(callerId));

    private Task<IResult> UpdateAsync(string callerId, string absenceId, UpdateAbsenceDto dto) =>
        _endpoint.UpdateAsync(absenceId, dto, _permissions, _absences, BuildChoreAlerts(),
            BuildHousehold(), _context, TestPrincipal.Create(callerId));

    private Task<IResult> DeleteAsync(string callerId, string absenceId) =>
        _endpoint.DeleteAsync(absenceId, _permissions, _absences, BuildHousehold(), _context,
            TestPrincipal.Create(callerId));

    [Test]
    public async Task Create_DeclaresYourOwnAbsenceAndReportsTheHandover()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        await AddOccurrenceAsync(chore, "anna", 5);

        var result = await CreateAsync("anna", new CreateAbsenceDto
        {
            StartAt = DateTimeOffset.UtcNow.AddDays(1),
            EndAt = DateTimeOffset.UtcNow.AddDays(10),
            Note = "Lisbon",
        });

        var saved = ((Ok<AbsenceSavedDto>)result).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(saved.Absence.UserId, Is.EqualTo("anna"));
            Assert.That(saved.Absence.CreatedByUserId, Is.EqualTo("anna"));
            Assert.That(saved.ChoresReassigned, Is.EqualTo(1));
        });
    }

    /// <summary>There is no route that takes a target user, and that is the design: "Anna is away
    /// next week" is only Anna's to assert, the same rule home status has.</summary>
    [Test]
    public async Task Create_AlwaysRecordsTheCallerAsTheSubject()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");

        await CreateAsync("ben", new CreateAbsenceDto
        {
            StartAt = DateTimeOffset.UtcNow.AddDays(1),
            EndAt = DateTimeOffset.UtcNow.AddDays(4),
        });

        var stored = await _context.Set<MemberAbsence>().SingleAsync();

        Assert.That(stored.UserId, Is.EqualTo("ben"));
    }

    [Test]
    public async Task Create_ByANonMember_IsForbidden()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        var result = await CreateAsync("stranger", new CreateAbsenceDto
        {
            StartAt = DateTimeOffset.UtcNow.AddDays(1),
            EndAt = DateTimeOffset.UtcNow.AddDays(4),
        });

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Create_WithPresenceDisabled_IsForbidden()
    {
        await SeedGuildAsync(GuildFeaturePresets.Household & ~GuildFeatures.Presence);
        await AddMemberAsync("anna");

        var result = await CreateAsync("anna", new CreateAbsenceDto
        {
            StartAt = DateTimeOffset.UtcNow.AddDays(1),
            EndAt = DateTimeOffset.UtcNow.AddDays(4),
        });

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Update_SomebodyElsesAbsenceWithoutManageGuild_IsForbidden()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var absence = await AddAbsenceAsync("anna", 3, 6);

        var result = await UpdateAsync("ben", absence.Id, new UpdateAbsenceDto { Note = "made this up" });

        Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
    }

    /// <summary>Somebody has to be able to clear a row entered by a flatmate who has since stopped
    /// answering their phone - but ManageGuild still cannot create one, because inventing an absence
    /// would move somebody's chores off them without their knowing.</summary>
    [Test]
    public async Task Delete_SomebodyElsesAbsenceWithManageGuild_IsAllowed()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var absence = await AddAbsenceAsync("anna", 3, 6);

        var result = await DeleteAsync(OwnerId, absence.Id);

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<NoContent>());
            Assert.That(await _context.Set<MemberAbsence>().CountAsync(), Is.Zero);
        });
    }

    /// <summary>Withdrawing an absence does not claw the chores back: the new assignee may already
    /// have done them, and an occurrence that changes hands twice is how the fairness ledger stops
    /// being believable.</summary>
    [Test]
    public async Task Delete_DoesNotReturnTheHandedOverChores()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        var occurrence = await AddOccurrenceAsync(chore, "anna", 5);

        var created = ((Ok<AbsenceSavedDto>)await CreateAsync("anna", new CreateAbsenceDto
        {
            StartAt = DateTimeOffset.UtcNow.AddDays(1),
            EndAt = DateTimeOffset.UtcNow.AddDays(10),
        })).Value!;

        await DeleteAsync("anna", created.Absence.Id);

        Assert.That(occurrence.AssignedUserId, Is.EqualTo("ben"));
    }

    [Test]
    public async Task Update_ThatWouldOverlapAnotherOfYourOwn_IsRejected()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddAbsenceAsync("anna", 3, 6);
        var second = await AddAbsenceAsync("anna", 10, 14);

        var result = await UpdateAsync("anna", second.Id,
            new UpdateAbsenceDto { StartAt = DateTimeOffset.UtcNow.AddDays(5) });

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Update_AnAbsenceThatDoesNotExist_IsNotFound()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");

        var result = await UpdateAsync("anna", "absn_MISSING", new UpdateAbsenceDto { Note = "x" });

        Assert.That(result, Is.InstanceOf<NotFound>());
    }

    /// <summary>Extending an absence hands over what the extension newly covers - those are
    /// occurrences the member has just said they will not be here for.</summary>
    [Test]
    public async Task Update_ExtendingAnAbsence_HandsOverTheNewlyCoveredChores()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var chore = await AddChoreAsync();
        var occurrence = await AddOccurrenceAsync(chore, "anna", 12);
        var absence = await AddAbsenceAsync("anna", 1, 6);

        var result = await UpdateAsync("anna", absence.Id,
            new UpdateAbsenceDto { EndAt = DateTimeOffset.UtcNow.AddDays(20) });

        Assert.Multiple(() =>
        {
            Assert.That(((Ok<AbsenceSavedDto>)result).Value!.ChoresReassigned, Is.EqualTo(1));
            Assert.That(occurrence.AssignedUserId, Is.EqualTo("ben"));
        });
    }
}
