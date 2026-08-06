using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
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
    private const string EveryoneRoleId = "role-everyone";

    private FakeDistributedCache _cache = null!;
    private TestGuildContext _context = null!;
    private GuildPermissionService _permissions = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
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
                     (LedgerChannelId, ChannelType.Ledger),
                     (PantryChannelId, ChannelType.Pantry),
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

    private HouseholdDigestService Build(params (string UserId, string Kind)[] homeStatuses) =>
        new(_context, _permissions, new LedgerService(_context),
            new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId, homeStatuses)));

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
            Assert.That(digest.Lists, Is.Not.Null, "the other modules are unaffected");
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
        });
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
