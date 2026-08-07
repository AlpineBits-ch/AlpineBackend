using Guild.Application.Services;
using Guild.Contracts;
using Guild.Contracts.Bus.Events;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>The sweep that finally reads <see cref="PantryConfig.ExpiryWarningDays"/>.</summary>
[TestFixture]
public class PantryExpiryServiceTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string FridgeId = "chan-fridge";
    private const string FreezerId = "chan-freezer";
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

        foreach (var id in new[] { FridgeId, FreezerId })
        {
            _context.Channels.Add(new Channel
            {
                Id = id, GuildId = GuildId, Name = id, Type = ChannelType.Pantry,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-anna", GuildId = GuildId, UserId = "anna", JoinedAt = DateTime.UtcNow,
            SearchValue = "ANNA", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-anna", RoleId = EveryoneRoleId, MemberId = "member-anna",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private async Task<PantryItem> AddItemAsync(string channelId, string name, TimeSpan expiresIn)
    {
        var item = PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = channelId, GuildId = GuildId, Name = name, Quantity = 1,
            ExpiresAt = DateTimeOffset.UtcNow + expiresIn, AddedByUserId = "anna",
        });

        _context.PantryItems.Add(item);
        await _context.SaveChangesAsync();
        return item;
    }

    private async Task SetWarningDaysAsync(string channelId, int days)
    {
        _context.PantryConfigs.Add(new PantryConfig
        {
            ChannelId = channelId, GuildId = GuildId, ExpiryWarningDays = days,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private PantryExpiryService Build()
    {
        var notifier = new HouseholdNotifier(
            _context, new NotificationResolutionService(_context), _hub, _bus);

        var alerts = new HouseholdAlertService(
            _context, notifier, _permissions,
            new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId)),
            NullLogger<HouseholdAlertService>.Instance);

        return new PantryExpiryService(
            _context, alerts, _permissions, NullLogger<PantryExpiryService>.Instance);
    }

    private List<HouseholdPushRequested> Pushes() => _bus.Published.OfType<HouseholdPushRequested>().ToList();

    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Warns_AboutSomethingInsideTheHorizon()
    {
        await SeedAsync();
        var item = await AddItemAsync(FridgeId, "Milk", TimeSpan.FromDays(1));

        var covered = await Build().SendDueWarningsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(covered, Is.EqualTo(1));
            Assert.That(item.ExpiryNotifiedAt, Is.Not.Null);
            Assert.That(Pushes().Single().Body, Is.EqualTo("Milk is about to go off."));
        });
    }

    [Test]
    public async Task IsSentOnlyOnce()
    {
        await SeedAsync();
        await AddItemAsync(FridgeId, "Milk", TimeSpan.FromDays(1));

        var service = Build();

        Assert.Multiple(async () =>
        {
            Assert.That(await service.SendDueWarningsAsync(), Is.EqualTo(1));
            Assert.That(await service.SendDueWarningsAsync(), Is.Zero,
                "ExpiryNotifiedAt is what makes the sweep at-most-once");
        });
    }

    /// <summary>The behaviour ExpiryWarningDays claimed to have.</summary>
    [Test]
    public async Task HonoursEachPantrysOwnHorizon()
    {
        await SeedAsync();
        await SetWarningDaysAsync(FreezerId, 14);

        var fridgeItem = await AddItemAsync(FridgeId, "Yoghurt", TimeSpan.FromDays(7));
        var freezerItem = await AddItemAsync(FreezerId, "Peas", TimeSpan.FromDays(7));

        await Build().SendDueWarningsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(freezerItem.ExpiryNotifiedAt, Is.Not.Null,
                "seven days is inside the freezer's fortnight");
            Assert.That(fridgeItem.ExpiryNotifiedAt, Is.Null,
                "and outside the fridge's default three days");
        });
    }

    [Test]
    public async Task BatchesOnePantrysItemsIntoOneAlert()
    {
        await SeedAsync();
        await AddItemAsync(FridgeId, "Milk", TimeSpan.FromDays(1));
        await AddItemAsync(FridgeId, "Yoghurt", TimeSpan.FromDays(2));
        await AddItemAsync(FridgeId, "Ham", TimeSpan.FromDays(2));

        await Build().SendDueWarningsAsync();

        var pushes = Pushes();

        Assert.Multiple(() =>
        {
            Assert.That(pushes, Has.Count.EqualTo(1), "three separate pushes about one fridge is an uninstall");
            Assert.That(pushes[0].Body, Is.EqualTo("Milk, Yoghurt and 1 more are about to go off."));
        });
    }

    [Test]
    public async Task SeparatePantriesGetSeparateAlerts()
    {
        await SeedAsync();
        await AddItemAsync(FridgeId, "Milk", TimeSpan.FromDays(1));
        await AddItemAsync(FreezerId, "Peas", TimeSpan.FromDays(1));

        await Build().SendDueWarningsAsync();

        Assert.That(Pushes().Select(p => p.ChannelId), Is.EquivalentTo(new[] { FridgeId, FreezerId }));
    }

    /// <summary>Otherwise a guild coming back from a long outage announces a year of forgotten
    /// leftovers, and the rows sit at the head of the candidate ordering forever.</summary>
    [Test]
    public async Task LongExpiredItems_AreRetiredWithoutBeingAnnounced()
    {
        await SeedAsync();
        var ancient = await AddItemAsync(FridgeId, "Cream", TimeSpan.FromDays(-30));

        var covered = await Build().SendDueWarningsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(covered, Is.EqualTo(1), "it was dealt with");
            Assert.That(ancient.ExpiryNotifiedAt, Is.Not.Null, "and stamped, so it leaves the candidate set");
            Assert.That(Pushes(), Is.Empty, "but nobody was told about month-old cream");
        });
    }

    [Test]
    public async Task RecentlyExpiredItems_AreStillWorthMentioning()
    {
        await SeedAsync();
        await AddItemAsync(FridgeId, "Cream", TimeSpan.FromHours(-6));

        await Build().SendDueWarningsAsync();

        Assert.That(Pushes(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ItemsWithNoDate_AreNeverCandidates()
    {
        await SeedAsync();

        var item = PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = FridgeId, GuildId = GuildId, Name = "Salt", Quantity = 1, AddedByUserId = "anna",
        });
        _context.PantryItems.Add(item);
        await _context.SaveChangesAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await Build().SendDueWarningsAsync(), Is.Zero);
            Assert.That(item.ExpiryNotifiedAt, Is.Null);
        });
    }

    [Test]
    public async Task WithThePantryModuleOff_NothingIsAnnounced()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Pantry);
        var item = await AddItemAsync(FridgeId, "Milk", TimeSpan.FromDays(1));

        await Build().SendDueWarningsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Is.Empty);
            Assert.That(item.ExpiryNotifiedAt, Is.Null,
                "left unstamped, so switching the module back on still warns");
        });
    }

    [Test]
    public void DescribeExpiring_ReadsLikeASentence()
    {
        PantryItem Named(string name) => PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = FridgeId, GuildId = GuildId, Name = name, Quantity = 1, AddedByUserId = "anna",
        });

        var one = HouseholdAlertService.DescribeExpiring([Named("Milk")]);
        var two = HouseholdAlertService.DescribeExpiring([Named("Milk"), Named("Ham")]);
        var many = HouseholdAlertService.DescribeExpiring(
            [Named("Milk"), Named("Ham"), Named("Peas"), Named("Dill")]);

        Assert.Multiple(() =>
        {
            Assert.That(one.Text, Is.EqualTo("Milk is about to go off."));
            Assert.That(two.Text, Is.EqualTo("Milk and Ham are about to go off."));
            Assert.That(many.Text, Is.EqualTo("Milk, Ham and 2 more are about to go off."));
        });
    }

    /// <summary>Three keys rather than one with a count: the languages this is translated into do
    /// not agree on how a list of two joins, and a client cannot fix that from a count. The
    /// arguments are what each key's placeholders take, in order.</summary>
    [Test]
    public void DescribeExpiring_CarriesTheKeyAndArgumentsAClientNeeds()
    {
        PantryItem Named(string name) => PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = FridgeId, GuildId = GuildId, Name = name, Quantity = 1, AddedByUserId = "anna",
        });

        var one = HouseholdAlertService.DescribeExpiring([Named("Milk")]);
        var two = HouseholdAlertService.DescribeExpiring([Named("Milk"), Named("Ham")]);
        var many = HouseholdAlertService.DescribeExpiring(
            [Named("Milk"), Named("Ham"), Named("Peas"), Named("Dill")]);

        Assert.Multiple(() =>
        {
            Assert.That(one.LocKey, Is.EqualTo(HouseholdLocKeys.PantryExpiringOneBody));
            Assert.That(one.LocArgs, Is.EqualTo(new[] { "Milk" }));

            Assert.That(two.LocKey, Is.EqualTo(HouseholdLocKeys.PantryExpiringTwoBody));
            Assert.That(two.LocArgs, Is.EqualTo(new[] { "Milk", "Ham" }));

            Assert.That(many.LocKey, Is.EqualTo(HouseholdLocKeys.PantryExpiringManyBody));
            Assert.That(many.LocArgs, Is.EqualTo(new[] { "Milk", "Ham", "2" }),
                "the count is formatted here, so no client has to know it is a number");
        });
    }
}
