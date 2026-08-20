using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Contracts.Bus.Events;
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

/// <summary>
/// The one-tap capture paths: scan, consume, restock, and the per-guild barcode memory that makes
/// the second scan of a product need no typing.
/// </summary>
[TestFixture]
public class PantryCaptureTests
{
    private const string GuildId = "guild-1";
    private const string OtherGuildId = "guild-2";
    private const string OwnerId = "owner-1";
    private const string FridgeId = "chan-fridge";
    private const string ListId = "chan-list";
    private const string OtherFridgeId = "chan-fridge-2";
    private const string EveryoneRoleId = "role-everyone";
    private const string Anna = "anna";
    private const string Ben = "ben";

    private PantryCaptureContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private GuildPermissionService _permissions = null!;
    private HouseholdChannelService _household = null!;
    private PantryRestockService _restock = null!;
    private ProductCatalogService _catalog = null!;
    private PantryCaptureService _capture = null!;
    private PantryCaptureEndpoint _endpoint = null!;
    private PantryEndpoint _pantry = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new PantryCaptureContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();

        _permissions = PermissionTestFactory.Create(_cache, _context);

        _household = new HouseholdChannelService(
            _context, _permissions,
            new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance),
            new ChannelAudienceService(_permissions, new MemoryCache(new MemoryCacheOptions())),
            _hub);

        var alerts = new HouseholdAlertService(
            _context,
            new HouseholdNotifier(new NotificationResolutionService(_context), _hub, _bus),
            _permissions,
            new HomeStatusService(RedisTestFactory.CreateWithHomeStatus(GuildId)),
            NullLogger<HouseholdAlertService>.Instance);

        _restock = new PantryRestockService(_context, _household, alerts);
        // A stub that would answer if it were ever reached, and a rate limiter with no budget so it
        // is not. Nothing in this fixture is about the catalog; wiring it offline keeps that true.
        _catalog = new ProductCatalogService(
            _context, ProductCatalogHarness.Lookups(StubProductApi.FindsNothing(), budget: 0));
        _capture = new PantryCaptureService(_context, _restock, _household, _catalog);
        _endpoint = new PantryCaptureEndpoint();
        _pantry = new PantryEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

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

        _context.Channels.Add(new Channel
        {
            Id = FridgeId, GuildId = GuildId, Name = "fridge", Type = ChannelType.Pantry,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Channels.Add(new Channel
        {
            Id = ListId, GuildId = GuildId, Name = "shopping", Type = ChannelType.List,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        // Two flatmates, because every household alert excludes whoever caused it - a house of one
        // can never be told anything, and the low-stock assertions would pass vacuously.
        foreach (var userId in new[] { Anna, Ben })
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = $"member-{userId}", GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
                SearchValue = userId.ToUpperInvariant(),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });

            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-{userId}", RoleId = EveryoneRoleId, MemberId = $"member-{userId}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _context.SaveChangesAsync();
    }

    /// <summary>A second household with its own pantry, for the guild-scoping tests.</summary>
    private async Task SeedOtherGuildAsync()
    {
        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = OtherGuildId, OwnerId = OwnerId, Name = "Next Door",
            Features = GuildFeaturePresets.Household, Kind = GuildKind.Household,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Roles.Add(new Role
        {
            Id = "role-everyone-2", GuildId = OtherGuildId, Type = RoleType.Everyone, Name = "Everyone",
            Permissions = Role.DefaultEveryonePermissions,
            ModulePermissions = Role.DefaultEveryoneModulePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Channels.Add(new Channel
        {
            Id = OtherFridgeId, GuildId = OtherGuildId, Name = "fridge", Type = ChannelType.Pantry,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.GuildMembers.Add(new GuildMember
        {
            Id = "member-anna-2", GuildId = OtherGuildId, UserId = Anna, JoinedAt = DateTime.UtcNow,
            SearchValue = "ANNA", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-anna-2", RoleId = "role-everyone-2", MemberId = "member-anna-2",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _context.SaveChangesAsync();
    }

    private async Task LinkRestockListAsync()
    {
        _context.PantryConfigs.Add(new PantryConfig
        {
            ChannelId = FridgeId, GuildId = GuildId, RestockListChannelId = ListId,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<PantryItem> AddItemAsync(string name, decimal quantity,
        decimal? lowThreshold = null, string? barcode = null, string channelId = FridgeId,
        string guildId = GuildId)
    {
        var item = PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = channelId, GuildId = guildId, Name = name, Quantity = quantity,
            LowThreshold = lowThreshold, Barcode = barcode, AddedByUserId = Anna,
        });

        _context.PantryItems.Add(item);
        await _context.SaveChangesAsync();
        return item;
    }

    /// <summary><paramref name="acceptLanguage"/> is the only language signal a scan has - there is
    /// no locale stored on an account or a guild anywhere in this product - so it is what the
    /// catalog's language selection is driven by, and null exercises the fallback order.</summary>
    private Task<IResult> ScanAsync(
        ScanPantryItemDto dto, string channelId = FridgeId, string? acceptLanguage = null)
    {
        var http = new DefaultHttpContext();
        if (acceptLanguage is not null) http.Request.Headers.AcceptLanguage = acceptLanguage;

        return _endpoint.ScanAsync(
            channelId, dto, _household, _capture, _context, http, TestPrincipal.Create(Anna));
    }

    private Task<IResult> ConsumeAsync(string itemId, decimal? amount = null, bool? all = null) =>
        _endpoint.ConsumeAsync(itemId, new ConsumePantryItemDto { Amount = amount, All = all },
            _household, _capture, _context, TestPrincipal.Create(Anna));

    private Task<IResult> RestockAsync(string itemId, decimal? amount = null) =>
        _endpoint.RestockAsync(itemId, new RestockPantryItemDto { Amount = amount },
            _household, _capture, _context, TestPrincipal.Create(Anna));

    private Task<IResult> BarcodesAsync(string? q = null, string guildId = GuildId, string user = Anna) =>
        _endpoint.BarcodesAsync(guildId, q, _permissions, _context, TestPrincipal.Create(user));

    private List<PantryBarcode> Learned(string guildId = GuildId) =>
        _context.Set<PantryBarcode>().Where(b => b.GuildId == guildId).ToList();

    private List<ListItem> Lines() => _context.ListItems.Where(i => i.ChannelId == ListId).ToList();

    private List<HouseholdPushRequested> LowPushes() =>
        _bus.Published.OfType<HouseholdPushRequested>()
            .Where(p => p.Kind == HouseholdAlertService.KindPantryLow
                        || p.Kind == HouseholdAlertService.KindRestock)
            .ToList();

    // ══════════════════════════════════════════════════════════════════════════ Scanning
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>The commonest scan there is: the same product, back from the shop.</summary>
    [Test]
    public async Task Scan_AnItemAlreadyCarryingTheCode_TopsItUp()
    {
        await SeedAsync();
        var milk = await AddItemAsync("Milk", quantity: 1, barcode: "7610200000001");

        var result = await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000001", Quantity = 2 });
        var ok = result as Ok<ScanPantryItemResultDto>;

        Assert.Multiple(async () =>
        {
            Assert.That(ok, Is.Not.Null);
            Assert.That(ok!.Value!.Created, Is.False);
            Assert.That(ok.Value.Item.Id, Is.EqualTo(milk.Id));
            Assert.That(milk.Quantity, Is.EqualTo(3m));
            Assert.That(await _context.PantryItems.CountAsync(), Is.EqualTo(1),
                "a second row for the same jar is the whole failure mode");
        });
    }

    /// <summary>The payoff for the barcode table: a product this house has seen before, scanned
    /// into an empty fridge, needs no typing at all.</summary>
    [Test]
    public async Task Scan_AKnownBarcode_CreatesAnItemPrefilledFromIt()
    {
        await SeedAsync();

        await ScanAsync(new ScanPantryItemDto
        {
            Barcode = "7610200000002", Name = "Yoghurt", Unit = "pots", Quantity = 4,
        });

        var first = await _context.PantryItems.FirstAsync();
        first.LowThreshold = 2m;
        _context.PantryItems.Remove(first);
        await _context.SaveChangesAsync();

        // The house remembers the code even though nothing is stocked against it any more, which is
        // the case a per-item memory would have lost.
        var learnedRow = Learned().Single();
        learnedRow.LowThreshold = 2m;
        await _context.SaveChangesAsync();

        var result = await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000002" });
        var ok = result as Ok<ScanPantryItemResultDto>;

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.Not.Null);
            Assert.That(ok!.Value!.Created, Is.True);
            Assert.That(ok.Value.Learned, Is.False, "the house had already been taught this one");
            Assert.That(ok.Value.Item.Name, Is.EqualTo("Yoghurt"));
            Assert.That(ok.Value.Item.Unit, Is.EqualTo("pots"));
            Assert.That(ok.Value.Item.LowThreshold, Is.EqualTo(2m));
            Assert.That(ok.Value.Item.Quantity, Is.EqualTo(4m),
                "and one scan means four, because that is what the box holds");
        });
    }

    [Test]
    public async Task Scan_AnUnknownBarcodeWithNoName_IsRejected()
    {
        await SeedAsync();

        var result = await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000003" });

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(await _context.PantryItems.AnyAsync(), Is.False);
            Assert.That(Learned(), Is.Empty, "a rejected scan teaches nothing");
        });
    }

    [Test]
    public async Task Scan_AnUnknownBarcodeWithAName_CreatesItAndLearnsIt()
    {
        await SeedAsync();

        var result = await ScanAsync(new ScanPantryItemDto
        {
            Barcode = "7610200000004", Name = "Coffee", Unit = "bags",
        });

        var ok = result as Ok<ScanPantryItemResultDto>;
        var learned = Learned().Single();

        Assert.Multiple(() =>
        {
            Assert.That(ok!.Value!.Created, Is.True);
            Assert.That(ok.Value.Learned, Is.True, "this is the one scan worth confirming a name on");
            Assert.That(ok.Value.Item.Barcode, Is.EqualTo("7610200000004"));
            Assert.That(ok.Value.Item.Quantity, Is.EqualTo(1m), "nothing said otherwise");
            Assert.That(learned.Name, Is.EqualTo("Coffee"));
            Assert.That(learned.Unit, Is.EqualTo("bags"));
            Assert.That(learned.TimesSeen, Is.EqualTo(1));
        });
    }

    /// <summary>Learning is not a one-shot: the house corrects the label and the pack size by using
    /// the scanner, never by editing a settings screen it would have to find.</summary>
    [Test]
    public async Task Scan_RepeatedScans_KeepReteachingTheBarcode()
    {
        await SeedAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000005", Name = "Bier" });
        await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000005", Name = "Beer", Quantity = 6 });

        var learned = Learned().Single();

        Assert.Multiple(() =>
        {
            Assert.That(learned.TimesSeen, Is.EqualTo(2));
            Assert.That(learned.Name, Is.EqualTo("Beer"), "the correction sticks");
            Assert.That(learned.DefaultQuantity, Is.EqualTo(6m),
                "a stated quantity is what a scan of this code means from now on");
        });
    }

    /// <summary>A defaulted quantity is not evidence of anything, so it must not write itself back
    /// - otherwise the first guess freezes in place and the sixpack becomes a single forever.</summary>
    [Test]
    public async Task Scan_WithNoStatedQuantity_LeavesTheLearnedDefaultAlone()
    {
        await SeedAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000006", Name = "Eggs", Quantity = 12 });
        await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000006" });

        var learned = Learned().Single();

        Assert.Multiple(async () =>
        {
            Assert.That(learned.DefaultQuantity, Is.EqualTo(12m));
            Assert.That((await _context.PantryItems.FirstAsync()).Quantity, Is.EqualTo(24m),
                "and the silent second scan added a dozen, not one");
        });
    }

    /// <summary>The reason the table is guild-scoped: "Milch 1L" is what one house calls it, and a
    /// shared global table would need a language, a moderation story and a conflict rule.</summary>
    [Test]
    public async Task Scan_ABarcodeLearnedInOneGuild_IsInvisibleInAnother()
    {
        await SeedAsync();
        await SeedOtherGuildAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000007", Name = "Milch 1L" });

        var next = await ScanAsync(new ScanPantryItemDto { Barcode = "7610200000007" }, OtherFridgeId);
        var listed = await BarcodesAsync(guildId: OtherGuildId) as Ok<IEnumerable<PantryBarcodeDto>>;

        Assert.Multiple(() =>
        {
            Assert.That(next, Is.InstanceOf<BadRequest<string>>(),
                "the other house has never been taught this code and must be asked what it is");
            Assert.That(listed!.Value, Is.Empty);
        });
    }

    [Test]
    public async Task Scan_WithNoBarcode_IsRejected()
    {
        await SeedAsync();

        Assert.That(await ScanAsync(new ScanPantryItemDto { Barcode = "  " }),
            Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task Scan_WithoutManagePantry_IsForbidden()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Pantry);

        Assert.That(await ScanAsync(new ScanPantryItemDto { Barcode = "761", Name = "Milk" }),
            Is.InstanceOf<ForbidHttpResult>());
    }

    /// <summary>A scan that arrives with a new date releases the expiry stamp, exactly as a PATCH
    /// does - a replacement carton must not inherit the old one's spent warning.</summary>
    [Test]
    public async Task Scan_WithANewDate_ReleasesTheExpiryStamp()
    {
        await SeedAsync();
        var milk = await AddItemAsync("Milk", quantity: 1, barcode: "761");
        milk.ExpiresAt = DateTimeOffset.UtcNow.AddDays(1);
        milk.ExpiryNotifiedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        await ScanAsync(new ScanPantryItemDto
        {
            Barcode = "761", ExpiresAt = DateTimeOffset.UtcNow.AddDays(9),
        });

        Assert.That(milk.ExpiryNotifiedAt, Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════ Consuming
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Consume_TakesTheDefaultOfOne()
    {
        await SeedAsync();
        var item = await AddItemAsync("Eggs", quantity: 6);

        var ok = await ConsumeAsync(item.Id) as Ok<PantryItemDto>;

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.Not.Null);
            Assert.That(item.Quantity, Is.EqualTo(5m));
        });
    }

    /// <summary>A negative quantity would make the item permanently low and would read on the board
    /// as a debt the house owes its own fridge.</summary>
    [Test]
    public async Task Consume_ClampsAtZero()
    {
        await SeedAsync();
        var item = await AddItemAsync("Eggs", quantity: 2);

        await ConsumeAsync(item.Id, amount: 5m);

        Assert.That(item.Quantity, Is.Zero);
    }

    [Test]
    public async Task Consume_All_EmptiesItOutright()
    {
        await SeedAsync();
        var item = await AddItemAsync("Rice", quantity: 3.75m);

        await ConsumeAsync(item.Id, all: true);

        Assert.That(item.Quantity, Is.Zero);
    }

    /// <summary>The behaviour the whole slice exists to preserve: one tap runs exactly the loop a
    /// PATCH ran, so the list gets one line and the house gets one buzz.</summary>
    [Test]
    public async Task Consume_CrossingTheThreshold_RunsTheRestockLoopExactlyOnce()
    {
        await SeedAsync();
        await LinkRestockListAsync();
        var coffee = await AddItemAsync("Coffee", quantity: 3, lowThreshold: 2);

        await ConsumeAsync(coffee.Id);
        await ConsumeAsync(coffee.Id);

        Assert.Multiple(() =>
        {
            Assert.That(coffee.Quantity, Is.EqualTo(1m));
            Assert.That(Lines(), Has.Count.EqualTo(1), "the second dip must not append a duplicate line");
            Assert.That(Lines()[0].SourcePantryItemId, Is.EqualTo(coffee.Id));
            Assert.That(LowPushes(), Has.Count.EqualTo(1));
            Assert.That(coffee.RestockedAt, Is.Not.Null);
            Assert.That(coffee.LowNotifiedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Consume_ANonPositiveAmount_IsRejected()
    {
        await SeedAsync();
        var item = await AddItemAsync("Eggs", quantity: 6);

        Assert.Multiple(async () =>
        {
            Assert.That(await ConsumeAsync(item.Id, amount: 0m), Is.InstanceOf<BadRequest<string>>());
            Assert.That(await ConsumeAsync(item.Id, amount: -3m), Is.InstanceOf<BadRequest<string>>(),
                "otherwise this is a restock route that skips the stamp release");
            Assert.That(item.Quantity, Is.EqualTo(6m));
        });
    }

    [Test]
    public async Task Consume_AnItemThatIsNotThere_IsNotFound()
    {
        await SeedAsync();

        Assert.That(await ConsumeAsync("pitm-nope"), Is.InstanceOf<NotFound>());
    }

    // ══════════════════════════════════════════════════════════════════════════ Restocking
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Climbing back above the threshold re-arms the loop, which is what makes the next
    /// dip announce itself instead of being swallowed by a stale stamp.</summary>
    [Test]
    public async Task Restock_BackAboveTheThreshold_ReleasesTheStamps()
    {
        await SeedAsync();
        await LinkRestockListAsync();
        var coffee = await AddItemAsync("Coffee", quantity: 2, lowThreshold: 2);

        await ConsumeAsync(coffee.Id, amount: 1m);
        Assume.That(coffee.RestockedAt, Is.Not.Null);

        await RestockAsync(coffee.Id, amount: 5m);

        Assert.Multiple(() =>
        {
            Assert.That(coffee.Quantity, Is.EqualTo(6m));
            Assert.That(coffee.RestockedAt, Is.Null);
            Assert.That(coffee.LowNotifiedAt, Is.Null);
        });
    }

    /// <summary>Putting the thing in the cupboard is the moment it stops needing buying, so the
    /// line it put on the list goes grey without anyone having to tick it in the shop.</summary>
    [Test]
    public async Task Restock_TicksOffTheLineItPutOnTheList()
    {
        await SeedAsync();
        await LinkRestockListAsync();
        var coffee = await AddItemAsync("Coffee", quantity: 2, lowThreshold: 2);

        await ConsumeAsync(coffee.Id, amount: 1m);
        Assume.That(Lines(), Has.Count.EqualTo(1));

        await RestockAsync(coffee.Id, amount: 5m);

        Assert.Multiple(() =>
        {
            Assert.That(Lines()[0].IsChecked, Is.True);
            Assert.That(Lines()[0].CheckedByUserId, Is.EqualTo(Anna));
        });
    }

    /// <summary>A half-restock is still a low pantry.</summary>
    [Test]
    public async Task Restock_StillBelowTheThreshold_KeepsTheStamp()
    {
        await SeedAsync();
        await LinkRestockListAsync();
        var coffee = await AddItemAsync("Coffee", quantity: 4, lowThreshold: 4);

        await ConsumeAsync(coffee.Id, amount: 2m);
        Assume.That(Lines(), Has.Count.EqualTo(1));

        await RestockAsync(coffee.Id, amount: 1m);

        Assert.Multiple(() =>
        {
            Assert.That(coffee.Quantity, Is.EqualTo(3m));
            Assert.That(coffee.RestockedAt, Is.Not.Null);
            Assert.That(Lines(), Has.Count.EqualTo(1));
            Assert.That(LowPushes(), Has.Count.EqualTo(1), "one low episode is one buzz");
        });
    }

    [Test]
    public async Task Restock_ANonPositiveAmount_IsRejected()
    {
        await SeedAsync();
        var item = await AddItemAsync("Coffee", quantity: 1);

        Assert.Multiple(async () =>
        {
            Assert.That(await RestockAsync(item.Id, amount: 0m), Is.InstanceOf<BadRequest<string>>());
            Assert.That(await RestockAsync(item.Id, amount: -2m), Is.InstanceOf<BadRequest<string>>());
            Assert.That(item.Quantity, Is.EqualTo(1m));
        });
    }

    /// <summary>A scan is a restock too, and the shopper who scanned the coffee in has already
    /// bought it - so the same line goes grey on that path.</summary>
    [Test]
    public async Task Scan_OntoALowItem_AlsoTicksTheLine()
    {
        await SeedAsync();
        await LinkRestockListAsync();
        var coffee = await AddItemAsync("Coffee", quantity: 2, lowThreshold: 2, barcode: "761");

        await ConsumeAsync(coffee.Id, amount: 1m);
        Assume.That(Lines(), Has.Count.EqualTo(1));

        await ScanAsync(new ScanPantryItemDto { Barcode = "761", Quantity = 4 });

        Assert.Multiple(() =>
        {
            Assert.That(Lines()[0].IsChecked, Is.True);
            Assert.That(coffee.RestockedAt, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ The learned table
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Barcodes_ReturnsWhatThisHouseHasLearned_MostUsedFirst()
    {
        await SeedAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = "111", Name = "Milk" });
        await ScanAsync(new ScanPantryItemDto { Barcode = "222", Name = "Coffee" });
        await ScanAsync(new ScanPantryItemDto { Barcode = "222" });

        var ok = await BarcodesAsync() as Ok<IEnumerable<PantryBarcodeDto>>;
        var rows = ok!.Value!.ToList();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].Name, Is.EqualTo("Coffee"));
            Assert.That(rows[0].TimesSeen, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Barcodes_FilterMatchesNameCaseInsensitivelyAndBarcodeByPrefix()
    {
        await SeedAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = "76100111", Name = "Milk" });
        await ScanAsync(new ScanPantryItemDto { Barcode = "40000222", Name = "Coffee" });

        var byName = (await BarcodesAsync("mil") as Ok<IEnumerable<PantryBarcodeDto>>)!.Value!.ToList();
        var byCode = (await BarcodesAsync("4000") as Ok<IEnumerable<PantryBarcodeDto>>)!.Value!.ToList();
        var byNothing = (await BarcodesAsync("zzz") as Ok<IEnumerable<PantryBarcodeDto>>)!.Value!.ToList();

        Assert.Multiple(() =>
        {
            Assert.That(byName.Single().Name, Is.EqualTo("Milk"));
            Assert.That(byCode.Single().Name, Is.EqualTo("Coffee"));
            Assert.That(byNothing, Is.Empty);
        });
    }

    [Test]
    public async Task Barcodes_ANonMember_IsForbidden()
    {
        await SeedAsync();

        Assert.That(await BarcodesAsync(user: "stranger"), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Barcodes_WithThePantryModuleOff_IsForbidden()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Pantry);

        Assert.That(await BarcodesAsync(), Is.InstanceOf<ForbidHttpResult>());
    }

    // ── Stating a name for a barcode, which moves no stock ───────────────────

    private Task<IResult> TeachAsync(
        string barcode, TeachPantryBarcodeDto dto, string guildId = GuildId, string user = Anna) =>
        _endpoint.TeachBarcodeAsync(
            guildId, barcode, dto, _permissions, _capture, _context, TestPrincipal.Create(user));

    private static TeachPantryBarcodeResultDto TeachBody(IResult result) =>
        ((Ok<TeachPantryBarcodeResultDto>)result).Value!;

    [Test]
    public async Task Teach_ABarcodeTheHouseHasNeverSeen_LearnsItWithoutAScan()
    {
        await SeedAsync();

        var body = TeachBody(await TeachAsync("111", new TeachPantryBarcodeDto { Name = "Milk" }));

        var taught = await _context.PantryBarcodes.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body.Learned, Is.True);
            Assert.That(taught.Name, Is.EqualTo("Milk"));

            // Not a sighting.
            Assert.That(taught.TimesSeen, Is.Zero);

            // The point of the whole route: nothing was added to any fridge.
            Assert.That(_context.PantryItems.Any(), Is.False);
        });
    }

    [Test]
    public async Task Teach_CorrectingAName_MovesNoStockAndKeepsTheLearnedDefault()
    {
        await SeedAsync();

        // Taught the old way, with a scan that stated a quantity of six.
        await ScanAsync(new ScanPantryItemDto { Barcode = "111", Name = "Bier", Quantity = 6m });

        var before = await _context.PantryItems.SingleAsync(i => i.Barcode == "111");
        var quantityBefore = before.Quantity;

        var body = TeachBody(await TeachAsync("111", new TeachPantryBarcodeDto { Name = "Beer" }));

        var taught = await _context.PantryBarcodes.SingleAsync();
        var item = await _context.PantryItems.SingleAsync(i => i.Barcode == "111");

        Assert.Multiple(() =>
        {
            Assert.That(body.Learned, Is.False, "the house already knew this code");
            Assert.That(taught.Name, Is.EqualTo("Beer"));

            // The three things a client would have had to compensate for if correcting a name meant
            // re-scanning: stock added, a quantity it could not decline, and a default quantity
            // silently rewritten by whatever the correction happened to carry.
            Assert.That(item.Quantity, Is.EqualTo(quantityBefore));
            Assert.That(taught.DefaultQuantity, Is.EqualTo(6m));
            Assert.That(taught.TimesSeen, Is.EqualTo(1), "still one scan, because there was one");
        });
    }

    [Test]
    public async Task Teach_OmittingUnitAndQuantity_LeavesThemRatherThanClearingThem()
    {
        await SeedAsync();

        await ScanAsync(new ScanPantryItemDto
        {
            Barcode = "111", Name = "Milch", Unit = "L", Quantity = 2m,
        });

        await TeachAsync("111", new TeachPantryBarcodeDto { Name = "Milk" });

        var taught = await _context.PantryBarcodes.SingleAsync();

        Assert.Multiple(() =>
        {
            // A correction made from a scanner toast knows the name and nothing else.
            Assert.That(taught.Unit, Is.EqualTo("L"));
            Assert.That(taught.DefaultQuantity, Is.EqualTo(2m));
        });
    }

    [Test]
    public async Task Teach_RejectsTheThingsAScanRejects()
    {
        await SeedAsync();

        var noName = await TeachAsync("111", new TeachPantryBarcodeDto { Name = "   " });
        var tooLong = await TeachAsync("111", new TeachPantryBarcodeDto { Name = new string('x', 101) });
        var zero = await TeachAsync("111",
            new TeachPantryBarcodeDto { Name = "Milk", DefaultQuantity = 0m });

        Assert.Multiple(() =>
        {
            Assert.That(noName, Is.InstanceOf<BadRequest<string>>());
            Assert.That(tooLong, Is.InstanceOf<BadRequest<string>>());

            // Rejected rather than ignored: the only thing a client can mean by zero here is a scan
            // that adds nothing, which is not a thing.
            Assert.That(zero, Is.InstanceOf<BadRequest<string>>());
            Assert.That(_context.PantryBarcodes.Any(), Is.False);
        });
    }

    [Test]
    public async Task Teach_ANonMember_IsForbidden()
    {
        await SeedAsync();

        Assert.That(
            await TeachAsync("111", new TeachPantryBarcodeDto { Name = "Milk" }, user: "stranger"),
            Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Teach_WithThePantryModuleOff_IsForbidden()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Pantry);

        Assert.That(
            await TeachAsync("111", new TeachPantryBarcodeDto { Name = "Milk" }),
            Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Teach_CannotReachAnotherHouseholdsBarcode()
    {
        await SeedAsync();
        await SeedOtherGuildAsync();

        await TeachAsync("111", new TeachPantryBarcodeDto { Name = "Milk" });

        // Ben is in the first guild only.
        var refused = await TeachAsync(
            "111", new TeachPantryBarcodeDto { Name = "Whatever" }, guildId: OtherGuildId, user: Ben);

        var rows = await _context.PantryBarcodes.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].GuildId, Is.EqualTo(GuildId));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // The CRUD half still carries the code
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Create_AcceptsABarcode_AndPatchCanClearIt()
    {
        await SeedAsync();

        var created = await _pantry.CreateAsync(FridgeId,
            new CreatePantryItemDto { Name = "Milk", Quantity = 1, Barcode = " 761 " },
            _household, _restock, _context, TestPrincipal.Create(Anna)) as Ok<PantryItemDto>;

        Assume.That(created!.Value!.Barcode, Is.EqualTo("761"));

        var patched = await _pantry.UpdateAsync(created.Value.Id,
            new UpdatePantryItemDto { ClearBarcode = true },
            _household, _restock, _context, TestPrincipal.Create(Anna)) as Ok<PantryItemDto>;

        Assert.Multiple(() =>
        {
            Assert.That(patched!.Value!.Barcode, Is.Null);
            Assert.That(Learned(), Is.Empty,
                "typing a code into the form is not a scan, so it teaches nothing");
        });
    }
}

/// <summary><see cref="TestGuildContext"/> plus <see cref="PantryBarcode"/>.</summary>
internal sealed class PantryCaptureContext : MicroserviceContext
{
    public PantryCaptureContext(string dbName)
        : base(new DbContextOptionsBuilder<MicroserviceContext>().UseInMemoryDatabase(dbName).Options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Left empty for the same reason TestGuildContext leaves it empty: the InMemory provider is
        // already configured through the constructor, and calling base would add a conflicting
        // Postgres provider.
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PantryBarcode>(barcodeBuilder =>
        {
            barcodeBuilder.HasOne<Guild.Domain.Aggregates.Guild>()
                .WithMany()
                .HasForeignKey(x => x.GuildId)
                .OnDelete(DeleteBehavior.Cascade);

            barcodeBuilder.HasIndex(x => new { x.GuildId, x.Barcode }).IsUnique();
            barcodeBuilder.HasIndex(x => new { x.GuildId, x.TimesSeen });
        });
    }
}
