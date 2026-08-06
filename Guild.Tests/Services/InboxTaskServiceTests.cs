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
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        foreach (var (id, type) in new[]
                 {
                     (ChoresChannelId, ChannelType.Chores),
                     (ListChannelId, ChannelType.List),
                     (DecisionsChannelId, ChannelType.Decisions),
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

    // ══════════════════════════════════════════════════════════════════════════ The three sources
    // ══════════════════════════════════════════════════════════════════════════

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
