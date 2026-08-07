using System.Text.Json;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Contracts;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>The nudge: one tap that makes the app do the asking.</summary>
[TestFixture]
public class ChoreNudgeTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string ChoresChannelId = "chan-chores";
    private const string EveryoneRoleId = "role-everyone";
    private const string RotationRoleId = "role-flatmates";

    private FakeDistributedCache _cache = null!;
    private AbsenceTestContext _context = null!;
    private GuildPermissionService _permissions = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private ChoreEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new AbsenceTestContext(Guid.NewGuid().ToString());
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _endpoint = new ChoreEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task SeedGuildAsync()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "The Flat",
            Features = GuildFeaturePresets.Household, Kind = GuildKind.Household,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = EveryoneRoleId, GuildId = GuildId, Type = RoleType.Everyone, Name = "Everyone",
            Permissions = Role.DefaultEveryonePermissions,
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

    private async Task AddMemberAsync(string userId)
    {
        var memberId = $"member-{userId}";

        _context.GuildMembers.Add(new GuildMember
        {
            Id = memberId, GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
            SearchValue = userId.ToUpperInvariant(),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var roleId in new[] { EveryoneRoleId, RotationRoleId })
        {
            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{roleId}-{userId}", RoleId = roleId, MemberId = memberId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>An occurrence assigned to <paramref name="assignee"/>, overdue by default: two days
    /// past a 24-hour grace period.</summary>
    private async Task<ChoreOccurrence> AddOccurrenceAsync(
        string assignee, double dueHoursAgo = 72, int graceHours = 24)
    {
        var chore = Chore.Create(new CreateChoreParams
        {
            ChannelId = ChoresChannelId, GuildId = GuildId, Title = "Bins",
            IntervalDays = 7, AnchorAt = DateTimeOffset.UtcNow, EffortMinutes = 15,
            RotationRoleId = RotationRoleId, GraceHours = graceHours,
        });
        _context.Chores.Add(chore);

        var occurrence = ChoreOccurrence.Create(
            chore, DateTimeOffset.UtcNow.AddHours(-dueHoursAgo), assignee);
        _context.ChoreOccurrences.Add(occurrence);

        await _context.SaveChangesAsync();
        return occurrence;
    }

    private async Task EnableQuietHoursCoveringNowAsync()
    {
        // A window anchored on the current UTC minute, so the test does not depend on what time it
        // happens to be run at.
        var minute = (int)DateTimeOffset.UtcNow.TimeOfDay.TotalMinutes;

        _context.GuildQuietHoursConfigs.Add(new GuildQuietHoursConfig
        {
            GuildId = GuildId, Enabled = true, TimeZoneId = "UTC",
            StartMinuteLocal = (minute + 1439) % 1440,
            EndMinuteLocal = (minute + 60) % 1440,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
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

    private Task<IResult> NudgeAsync(string callerId, string occurrenceId) =>
        _endpoint.NudgeAsync(occurrenceId, BuildHousehold(), BuildChoreAlerts(), _context,
            TestPrincipal.Create(callerId));

    private static int StatusOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private List<string> Alerted() =>
        ((FakeHubClients)_hub.Clients).RecipientsOf(HouseholdNotifier.AlertEventName);

    private List<HouseholdPushRequested> Pushes() =>
        _bus.Published.OfType<HouseholdPushRequested>().ToList();

    // ══════════════════════════════════════════════════════════════════════════
    // The happy path, and the anonymity that is the whole point of it
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Nudge_AnOverdueChore_ReachesTheAssigneeAndNobodyElse()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");
        var occurrence = await AddOccurrenceAsync("anna");

        var result = await NudgeAsync("ben", occurrence.Id);

        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(200));
            Assert.That(occurrence.NudgedAt, Is.Not.Null);
            Assert.That(Alerted(), Is.EquivalentTo(new[] { "anna" }),
                "cara does not need to know that anna is being chased");
        });
    }

    /// <summary>
    /// The occurrence board has to carry the nudge stamp, because greying the button is the only
    /// way a client can avoid offering an action that will 409 - the cooldown is per occurrence, so
    /// every member looking at it gets the same answer.
    /// </summary>
    [Test]
    public async Task Nudge_IsVisibleOnTheOccurrenceBoard()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna");

        await NudgeAsync("ben", occurrence.Id);

        var listed = await _endpoint.ListOccurrencesAsync(
            ChoresChannelId, null, null, BuildHousehold(), _context, TestPrincipal.Create("ben"));

        var dto = (listed as Ok<IEnumerable<ChoreOccurrenceDto>>)?.Value?
            .SingleOrDefault(o => o.Id == occurrence.Id);

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.NudgedAt, Is.Not.Null,
            "a client that cannot see the stamp cannot grey the button");
    }

    [Test]
    public async Task Nudge_SendsTheCopyTheClientsHaveAKeyFor()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna");

        await NudgeAsync("ben", occurrence.Id);

        var push = Pushes().Single();

        Assert.Multiple(() =>
        {
            Assert.That(push.Kind, Is.EqualTo(ChoreAlertService.KindNudge));
            Assert.That(push.TargetId, Is.EqualTo(occurrence.Id));
            Assert.That(push.Title, Is.EqualTo("Bins"));
            Assert.That(push.TitleLocKey, Is.Null, "a chore's title is what the user typed");
            Assert.That(push.BodyLocKey, Is.EqualTo(HouseholdLocKeys.ChoreNudgeBody));
            Assert.That(HouseholdLocKeys.All, Does.Contain(push.BodyLocKey));
        });
    }

    /// <summary>The design, not an omission.</summary>
    [Test]
    public async Task Nudge_NeverNamesWhoSentIt()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna");

        await NudgeAsync("ben", occurrence.Id);

        var realtime = ((FakeHubClients)_hub.Clients).SentToUsers
            .Where(s => s.Method == HouseholdNotifier.AlertEventName)
            .Select(s => JsonSerializer.Serialize(s.Args[0]))
            .ToList();

        var push = JsonSerializer.Serialize(Pushes().Single());

        Assert.Multiple(() =>
        {
            Assert.That(realtime, Is.Not.Empty);
            foreach (var payload in realtime)
                Assert.That(payload, Does.Not.Contain("ben"), "not in the realtime payload");

            Assert.That(push, Does.Not.Contain("ben"), "and not in the push either");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ Who may nudge what
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Nudge_ByTheAssignee_IsRejected()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        var occurrence = await AddOccurrenceAsync("anna");

        var result = await NudgeAsync("anna", occurrence.Id);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(occurrence.NudgedAt, Is.Null);
            Assert.That(Alerted(), Is.Empty);
        });
    }

    [Test]
    public async Task Nudge_ACompletedChore_IsRejected()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna");
        occurrence.CompletedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        Assert.That(await NudgeAsync("ben", occurrence.Id), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Nudge_ASkippedChore_IsRejected()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna");
        occurrence.SkippedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        Assert.That(await NudgeAsync("ben", occurrence.Id), Is.InstanceOf<BadRequest<string>>());
    }

    /// <summary>Nudging something that is not late yet is not a reminder, it is somebody leaning
    /// over your shoulder - and it is exactly how the feature becomes the one people mute, taking
    /// the real reminders with it.</summary>
    [Test]
    public async Task Nudge_AChoreThatIsNotOverdueYet_IsRejected()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna", dueHoursAgo: 1);

        Assert.Multiple(async () =>
        {
            Assert.That(await NudgeAsync("ben", occurrence.Id), Is.InstanceOf<BadRequest<string>>());
            Assert.That(occurrence.NudgedAt, Is.Null);
        });
    }

    /// <summary>Still inside the grace period counts as not late.</summary>
    [Test]
    public async Task Nudge_InsideTheGracePeriod_IsRejected()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna", dueHoursAgo: 20, graceHours: 24);

        Assert.That(await NudgeAsync("ben", occurrence.Id), Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Nudge_JustPastTheGracePeriod_IsAllowed()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna", dueHoursAgo: 25, graceHours: 24);

        Assert.That(StatusOf(await NudgeAsync("ben", occurrence.Id)), Is.EqualTo(200));
    }

    [Test]
    public async Task Nudge_AnOccurrenceThatDoesNotExist_IsNotFound()
    {
        await SeedGuildAsync();
        await AddMemberAsync("ben");

        Assert.That(await NudgeAsync("ben", "choc_MISSING"), Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════════
    // One nudge per chore, not one per flatmate
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Rate-limited per occurrence rather than per sender.</summary>
    [Test]
    public async Task Nudge_ASecondTimeByADifferentFlatmate_IsRefused()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await AddMemberAsync("cara");
        var occurrence = await AddOccurrenceAsync("anna");

        await NudgeAsync("ben", occurrence.Id);
        var second = await NudgeAsync("cara", occurrence.Id);

        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(second), Is.EqualTo(409));
            Assert.That(Pushes(), Has.Count.EqualTo(1), "one buzz for one bin");
        });
    }

    [Test]
    public async Task Nudge_OnceTheCooldownHasPassed_IsAllowedAgain()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna", dueHoursAgo: 200);

        occurrence.NudgedAt = DateTimeOffset.UtcNow - ChoreAlertService.NudgeCooldown - TimeSpan.FromMinutes(1);
        await _context.SaveChangesAsync();

        Assert.That(StatusOf(await NudgeAsync("ben", occurrence.Id)), Is.EqualTo(200));
    }

    [Test]
    public async Task Nudge_JustInsideTheCooldown_IsRefused()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna", dueHoursAgo: 200);

        occurrence.NudgedAt = DateTimeOffset.UtcNow - ChoreAlertService.NudgeCooldown + TimeSpan.FromMinutes(1);
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(StatusOf(await NudgeAsync("ben", occurrence.Id)), Is.EqualTo(409));
            Assert.That(Alerted(), Is.Empty);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Quiet hours, which this handles differently from a due reminder
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Refused rather than deferred, which is the opposite of what <c>chore.due</c> does
    /// with the same config. A deferred reminder is still true at 07:00 - the chore is still due. A
    /// deferred nudge is somebody's 23:40 impulse arriving seven hours later as though it were
    /// fresh, about a bin that may well have been taken out in the meantime.</summary>
    [Test]
    public async Task Nudge_InsideQuietHours_IsRefusedRatherThanDeferred()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await EnableQuietHoursCoveringNowAsync();
        var occurrence = await AddOccurrenceAsync("anna");

        var result = await NudgeAsync("ben", occurrence.Id);

        Assert.Multiple(() =>
        {
            Assert.That(StatusOf(result), Is.EqualTo(409));
            Assert.That(Alerted(), Is.Empty);
            Assert.That(occurrence.NudgedAt, Is.Null,
                "a refused nudge must not spend the twelve-hour window either");
        });
    }

    [Test]
    public async Task Nudge_WithQuietHoursConfiguredButDisabled_GoesThrough()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        await EnableQuietHoursCoveringNowAsync();

        var config = _context.GuildQuietHoursConfigs.Single(c => c.GuildId == GuildId);
        config.Enabled = false;
        await _context.SaveChangesAsync();

        var occurrence = await AddOccurrenceAsync("anna");

        Assert.That(StatusOf(await NudgeAsync("ben", occurrence.Id)), Is.EqualTo(200));
    }

    [Test]
    public async Task Nudge_WithNoQuietHoursConfigured_GoesThrough()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna");

        Assert.That(StatusOf(await NudgeAsync("ben", occurrence.Id)), Is.EqualTo(200));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Failure is never the caller's problem
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The alert fires after the stamp has committed.</summary>
    [Test]
    public async Task Nudge_WhenTheAlertCannotBeDelivered_StillSucceeds()
    {
        await SeedGuildAsync();
        await AddMemberAsync("anna");
        await AddMemberAsync("ben");
        var occurrence = await AddOccurrenceAsync("anna");

        var alerts = new ChoreAlertService(
            new HouseholdNotifier(_context, new NotificationResolutionService(_context), _hub,
                new ThrowingMessageBus()),
            _permissions,
            NullLogger<ChoreAlertService>.Instance);

        var result = await _endpoint.NudgeAsync(occurrence.Id, BuildHousehold(), alerts, _context,
            TestPrincipal.Create("ben"));

        Assert.That(StatusOf(result), Is.EqualTo(200));
    }
}
