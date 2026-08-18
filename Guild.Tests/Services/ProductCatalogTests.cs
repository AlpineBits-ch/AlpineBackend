using System.Net;
using System.Text;
using System.Text.Json;
using AppEnvironment;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Endpoints;
using Guild.Application.Services;
using Guild.Domain.Aggregates;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Tests.Helpers;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Guild.Tests.Services;

/// <summary>
/// The shared product catalog: resolving a barcode from it, filling it from the live source inline
/// on a scan, loading it in bulk, publishing it, and the licence boundary that is the reason it is
/// a second table rather than more columns on <see cref="PantryBarcode"/>.
/// </summary>
[TestFixture]
public class ProductCatalogTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string FridgeId = "chan-fridge";
    private const string EveryoneRoleId = "role-everyone";
    private const string Anna = "anna";
    private const string Ben = "ben";

    /// <summary>A real M-Budget code from the research, resolved live against the source on
    /// 2026-08-07. Real rather than invented so the shape of the data under test is the shape that
    /// actually arrives.</summary>
    private const string Cornflakes = "7617027080224";

    private const string Unknown = "7610000000009";

    private TestGuildContext _context = null!;
    private FakeDistributedCache _cache = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private GuildPermissionService _permissions = null!;
    private HouseholdChannelService _household = null!;
    private PantryRestockService _restock = null!;
    private ProductCatalogService _catalog = null!;
    private PantryCaptureService _capture = null!;
    private PantryCaptureEndpoint _endpoint = null!;
    private ProductCatalogEndpoint _catalogEndpoint = null!;
    private StubProductApi _api = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestGuildContext(Guid.NewGuid().ToString());
        _cache = new FakeDistributedCache();
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();

        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);

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
        _endpoint = new PantryCaptureEndpoint();
        _catalogEndpoint = new ProductCatalogEndpoint();

        // Offline by default, with no budget on top, so a test that has not said it is about the
        // live source cannot accidentally be about it.
        UseSource(StubProductApi.FindsNothing(), budget: 0);
    }

    /// <summary>Rebuilds the scan path against a given stand-in for the product API and a given
    /// number of tokens in the instance's bucket.</summary>
    private void UseSource(StubProductApi api, int budget = 1000)
    {
        _api = api;
        _catalog = new ProductCatalogService(_context, ProductCatalogHarness.Lookups(api, budget));
        _capture = new PantryCaptureService(_context, _restock, _household, _catalog);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task SeedAsync()
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
            ModulePermissions = Role.DefaultEveryoneModulePermissions,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

        _context.Channels.Add(new Channel
        {
            Id = FridgeId, GuildId = GuildId, Name = "fridge", Type = ChannelType.Pantry,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });

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

    private async Task<ProductCatalogEntry> SeedCatalogAsync(
        string barcode = Cornflakes, string? de = "Cornflakes", string? fr = "Corn Flakes",
        string? it = null, string? en = null, string? brand = "M-Budget, Migros",
        decimal? quantity = 380m, string? unit = "g", string version = "off-2026-08-01",
        string? source = null)
    {
        var entry = new ProductCatalogEntry
        {
            Barcode = barcode, NameDe = de, NameFr = fr, NameIt = it, NameEn = en,
            Brand = brand, Quantity = quantity, QuantityUnit = unit,
            Source = source ?? ProductCatalogSources.OpenFoodFacts, SourceVersion = version,
            ImportedAt = DateTimeOffset.UtcNow,
        };

        _context.ProductCatalogEntries.Add(entry);
        await _context.SaveChangesAsync();
        return entry;
    }

    private Task<IResult> ScanAsync(ScanPantryItemDto dto, string? acceptLanguage = null)
    {
        var http = new DefaultHttpContext();
        if (acceptLanguage is not null) http.Request.Headers.AcceptLanguage = acceptLanguage;

        return _endpoint.ScanAsync(
            FridgeId, dto, _household, _capture, _context, http, TestPrincipal.Create(Anna));
    }

    private static ScanPantryItemResultDto Body(IResult result) =>
        ((Ok<ScanPantryItemResultDto>)result).Value!;

    // ── Resolution: the catalog fills a name that was previously blank ───────

    [Test]
    public async Task Scan_UnknownToTheHouse_TakesTheNameFromTheCatalog()
    {
        await SeedAsync();
        await SeedCatalogAsync();

        var result = await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes });

        var body = Body(result);

        Assert.Multiple(() =>
        {
            Assert.That(body.Item.Name, Is.EqualTo("Cornflakes"),
                "the whole point: the first scan of a common grocery needs no typing");
            Assert.That(body.Created, Is.True);
            Assert.That(body.Catalog, Is.Not.Null);
            Assert.That(body.Catalog!.Brand, Is.EqualTo("M-Budget, Migros"));
        });
    }

    [Test]
    public async Task Scan_CatalogHit_CarriesAttributionOnTheResponse()
    {
        await SeedAsync();
        await SeedCatalogAsync();

        var catalog = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes })).Catalog;

        Assert.Multiple(() =>
        {
            // The obligation is ours, so the strings ship with the name rather than being left for
            // a client to remember.
            Assert.That(catalog!.Attribution, Does.Contain("Open Food Facts"));
            Assert.That(catalog.Attribution, Does.Contain("Open Database License"));
            Assert.That(catalog.LicenseUrl, Is.Not.Empty);
            Assert.That(catalog.SourceUrl, Does.Contain(Cornflakes),
                "attribution should point at the product page, not just the site");
        });
    }

    [Test]
    public async Task Scan_CatalogHit_DoesNotFillQuantityOrUnitOntoTheItem()
    {
        await SeedAsync();
        await SeedCatalogAsync(quantity: 380m, unit: "g");

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

        Assert.Multiple(() =>
        {
            // Pack size is not how many the scan added.
            Assert.That(body.Item.Quantity, Is.EqualTo(1m));
            Assert.That(body.Item.Unit, Is.Null);
            Assert.That(body.Catalog!.Quantity, Is.EqualTo(380m));
            Assert.That(body.Catalog.QuantityUnit, Is.EqualTo("g"));
        });
    }

    [Test]
    public async Task Scan_ProductWithNoQuantity_StillResolves()
    {
        await SeedAsync();

        // The normal case, not an error: only 43.5% of Swiss products in the source carry a
        // quantity at all.
        await SeedCatalogAsync(quantity: null, unit: null);

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

        Assert.Multiple(() =>
        {
            Assert.That(body.Item.Name, Is.EqualTo("Cornflakes"));
            Assert.That(body.Catalog!.Quantity, Is.Null);
            Assert.That(body.Catalog.QuantityUnit, Is.Null);
        });
    }

    [Test]
    public async Task Scan_CatalogRowWithNoNameInAnyLanguage_IsAMissNotABlankName()
    {
        await SeedAsync();
        await SeedCatalogAsync(de: null, fr: null, it: null, en: null);

        var result = await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes });

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>(),
                "a named row with no name would otherwise put a blank line in somebody's fridge");
            Assert.That(_context.ProductCatalogMisses.Any(m => m.Barcode == Cornflakes), Is.True);
        });
    }

    // ── The house always wins ────────────────────────────────────────────────

    [Test]
    public async Task Scan_GuildHasItsOwnName_BeatsTheCatalog()
    {
        await SeedAsync();
        await SeedCatalogAsync(de: "Migros Bio Vollmilch UHT 1L");

        // Taught the way a house teaches it: by stating the name once.
        await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes, Name = "milk" });

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

        Assert.Multiple(() =>
        {
            Assert.That(body.Item.Name, Is.EqualTo("milk"),
                "they are the ones who have to recognise it on a shopping list");
            Assert.That(body.Catalog, Is.Null,
                "the catalog was never consulted, so there is nothing to attribute");
        });
    }

    [Test]
    public async Task Scan_RequestStatesAName_BeatsTheCatalogAndTeachesTheHouse()
    {
        await SeedAsync();
        await SeedCatalogAsync(de: "Migros Bio Vollmilch UHT 1L");

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes, Name = "milk" }));

        var taught = await _context.PantryBarcodes.SingleAsync(b => b.Barcode == Cornflakes);

        Assert.Multiple(() =>
        {
            Assert.That(body.Item.Name, Is.EqualTo("milk"));
            Assert.That(body.Learned, Is.True);
            Assert.That(taught.Name, Is.EqualTo("milk"));
        });
    }

    // ── Agreeing with the catalog, which is what makes the name the house's ──

    private Task<IResult> TeachAsync(string barcode, string name) =>
        _endpoint.TeachBarcodeAsync(
            GuildId, barcode, new TeachPantryBarcodeDto { Name = name },
            _permissions, _capture, _context, TestPrincipal.Create(Anna));

    [Test]
    public async Task Teach_CorrectingACatalogSuggestion_MakesTheNameTheHousesOwn()
    {
        await SeedAsync();
        await SeedCatalogAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes });

        var body = ((Ok<TeachPantryBarcodeResultDto>)await TeachAsync(Cornflakes, "Cereal")).Value!;

        var taught = await _context.PantryBarcodes.SingleAsync();
        var item = await _context.PantryItems.SingleAsync();

        Assert.Multiple(() =>
        {
            // The moment the licence boundary turns.
            Assert.That(body.Learned, Is.True);
            Assert.That(taught.Name, Is.EqualTo("Cereal"));

            // And the jar in the fridge, which was still showing the suggestion, is corrected with
            // it rather than being left disagreeing with the barcode the house just taught.
            Assert.That(item.Name, Is.EqualTo("Cereal"));
            Assert.That(body.RenamedItems, Has.Count.EqualTo(1));

            // No stock moved.
            Assert.That(item.Quantity, Is.EqualTo(1m));
        });
    }

    [Test]
    public async Task Teach_AnItemSomebodyHadAlreadyNamed_IsNotOverwritten()
    {
        await SeedAsync();
        await SeedCatalogAsync();

        // Named by a person on the way in, so it never carried the suggestion at all.
        await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes, Name = "Znuni-Flocken" });

        var body = ((Ok<TeachPantryBarcodeResultDto>)await TeachAsync(Cornflakes, "Cereal")).Value!;

        var item = await _context.PantryItems.SingleAsync();

        Assert.Multiple(() =>
        {
            // There is no provenance column and adding one would be a migration for a question the
            // comparison answers: an item that no longer reads as the catalog's suggestion is an
            // item a person has named, and this route does not undo people's work.
            Assert.That(item.Name, Is.EqualTo("Znuni-Flocken"));
            Assert.That(body.RenamedItems, Is.Empty);

            // The barcode is still taught, because that is what the caller asked for.
            Assert.That(_context.PantryBarcodes.Single().Name, Is.EqualTo("Cereal"));
        });
    }

    [Test]
    public async Task Teach_ThenScanningAgain_NeverAsksTheCatalogOrTheSourceAgain()
    {
        await SeedAsync();
        await SeedCatalogAsync();

        var api = StubProductApi.Finds(Cornflakes, "Cornflakes");
        UseSource(api);

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
        {
            await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes });
            await TeachAsync(Cornflakes, "Cereal");

            var again = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

            Assert.Multiple(() =>
            {
                Assert.That(again.Item.Name, Is.EqualTo("Cereal"));
                Assert.That(again.Catalog, Is.Null, "the house's own name is now nearer");
                Assert.That(again.Learned, Is.False, "and it was learned the moment it was stated");

                // The catalog row answered the first scan out of the local table, so the source was
                // never needed - but the assertion that matters is that nothing after the correction
                // reopens the question.
                Assert.That(api.Requested, Is.Empty);
            });
        });
    }

    // ── The licence boundary ─────────────────────────────────────────────────

    [Test]
    public async Task Scan_CatalogHit_TeachesTheGuildNothing()
    {
        await SeedAsync();
        await SeedCatalogAsync();

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

        Assert.Multiple(() =>
        {
            // The core of the ODbL position. pantry_barcodes holds names households typed and is
            // published nowhere; the catalog is a Derivative Database we are obliged to publish.
            Assert.That(_context.PantryBarcodes.Any(), Is.False,
                "an ODbL-licensed name must never land in the table of names households typed");

            // Learned means "the house taught us this".
            Assert.That(body.Learned, Is.False);
            Assert.That(body.Catalog, Is.Not.Null);
        });
    }

    [Test]
    public async Task Scan_ToppingUpAnItemNamedFromTheCatalog_StillTeachesTheGuildNothing()
    {
        await SeedAsync();
        await SeedCatalogAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes });

        // The second scan finds the item rather than needing a name at all.
        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

        Assert.Multiple(() =>
        {
            Assert.That(body.Created, Is.False);
            Assert.That(body.Item.Quantity, Is.EqualTo(2m));
            Assert.That(_context.PantryBarcodes.Any(), Is.False);
            Assert.That(body.Learned, Is.False);
        });
    }

    [Test]
    public async Task Scan_CatalogHitThenCorrected_LetsTheHousesNameWinFromThenOn()
    {
        await SeedAsync();
        await SeedCatalogAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes });

        // Somebody corrects the label.
        var corrected = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes, Name = "Cereal" }));

        var again = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

        Assert.Multiple(() =>
        {
            Assert.That(corrected.Learned, Is.True);
            Assert.That(_context.PantryBarcodes.Single().Name, Is.EqualTo("Cereal"));
            Assert.That(again.Catalog, Is.Null, "the house's own name is now nearer than the catalog");
        });
    }

    [Test]
    public async Task Scan_WithAnEmptyCatalog_BehavesExactlyAsBefore()
    {
        await SeedAsync();

        var first = await ScanAsync(new ScanPantryItemDto { Barcode = Unknown });
        var named = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Unknown, Name = "Held WC-Reiniger" }));

        Assert.Multiple(() =>
        {
            // Manual entry is the primary path for cleaning products and toiletries, not a
            // fallback: measured Swiss coverage across the sibling databases is about 1,300
            // products. Nothing here may assume a name will be found.
            Assert.That(first, Is.InstanceOf<BadRequest<string>>());
            Assert.That(named.Learned, Is.True);
            Assert.That(named.Catalog, Is.Null);
        });
    }

    // ── Language selection ───────────────────────────────────────────────────

    [Test]
    public async Task Scan_PicksTheNameInTheRequestedLanguage()
    {
        await SeedAsync();
        await SeedCatalogAsync(de: "Cornflakes", fr: "Flocons de maïs", it: "Fiocchi di mais");

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }, "fr-CH,fr;q=0.9"));

        Assert.Multiple(() =>
        {
            Assert.That(body.Item.Name, Is.EqualTo("Flocons de maïs"));
            Assert.That(body.Catalog!.Language, Is.EqualTo("fr"),
                "the client is told which language it actually got, since the source files names "
                + "under the wrong one often enough to matter");
        });
    }

    [Test]
    public async Task Scan_RequestedLanguageMissing_FallsBackRatherThanReturningNothing()
    {
        await SeedAsync();
        await SeedCatalogAsync(de: "Cornflakes", fr: null, it: null, en: null);

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }, "it-CH,it"));

        Assert.Multiple(() =>
        {
            // A wrong-language name is one the user can read off the packet and correct in a tap.
            Assert.That(body.Item.Name, Is.EqualTo("Cornflakes"));
            Assert.That(body.Catalog!.Language, Is.EqualTo("de"));
        });
    }

    [Test]
    public async Task Scan_NoAcceptLanguage_UsesTheSwissFallbackOrder()
    {
        await SeedAsync();
        await SeedCatalogAsync(de: "Cornflakes", fr: "Flocons de maïs", en: "Corn Flakes");

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

        Assert.That(body.Catalog!.Language, Is.EqualTo("de"),
            "de, fr, it, en - the primary market first, English last because it is least likely to "
            + "be what a Swiss package says");
    }

    [TestCase("de-CH,de;q=0.9,en;q=0.8", new[] { "de", "en" })]
    [TestCase("en;q=0.4,fr;q=0.9", new[] { "fr", "en" })]
    [TestCase("de-DE,de-AT,de-CH", new[] { "de" })]
    [TestCase("*", new string[0])]
    [TestCase("de;q=0", new string[0])]
    [TestCase("", new string[0])]
    [TestCase(null, new string[0])]
    public void ParseLanguages_ReadsTheHeaderTheWayTheHeaderMeansIt(string? header, string[] expected)
    {
        // q=0 is the header saying "not this one", so honouring it as a preference would invert the
        // caller's meaning. Regions collapse because the catalog has one German column.
        Assert.That(ProductCatalogService.ParseLanguages(header), Is.EqualTo(expected));
    }

    // ── Negative caching ─────────────────────────────────────────────────────

    [Test]
    public async Task Scan_UnresolvableCode_RecordsAMissEvenThoughTheScanIsRejected()
    {
        await SeedAsync();

        var result = await ScanAsync(new ScanPantryItemDto { Barcode = Unknown });

        var miss = await _context.ProductCatalogMisses.SingleAsync();

        Assert.Multiple(() =>
        {
            // The scan that had to ask the user to type a name is exactly the scan that wanted an
            // answer, so the miss survives the rejection rather than rolling back with it.
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(miss.Barcode, Is.EqualTo(Unknown));
            Assert.That(miss.Attempts, Is.Zero, "nothing has asked the source yet");
            Assert.That(miss.RetryAfter, Is.Not.Null, "and it is eligible to be asked");
        });
    }

    [Test]
    public async Task Scan_StatingAName_NeverAsksTheCatalogAndRecordsNoMiss()
    {
        await SeedAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = Unknown, Name = "Held WC-Reiniger" });

        Assert.That(_context.ProductCatalogMisses.Any(), Is.False,
            "a code the house has just named is one where a catalog answer could never be used, so "
            + "queueing it for the backfill would spend a lookup on nothing");
    }

    [Test]
    public async Task Scan_SameUnresolvableCodeTwice_DoesNotDuplicateOrRefreshTheMiss()
    {
        await SeedAsync();

        await ScanAsync(new ScanPantryItemDto { Barcode = Unknown });
        var first = (await _context.ProductCatalogMisses.SingleAsync()).RetryAfter;

        await ScanAsync(new ScanPantryItemDto { Barcode = Unknown });

        var misses = await _context.ProductCatalogMisses.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(misses, Has.Count.EqualTo(1));

            // Bumping the date on every scan would mean the products a house buys weekly are the
            // ones the filler never reaches, which is precisely backwards.
            Assert.That(misses[0].RetryAfter, Is.EqualTo(first));
        });
    }

    [Test]
    public void Miss_BackoffEscalatesThenSettlesPermanently()
    {
        var now = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        var miss = ProductCatalogMiss.Create(Unknown, ProductCatalogSources.OpenFoodFacts, now);

        Assert.That(miss.MayQuery(now), Is.True);

        miss.RecordAbsent(now);
        Assert.That(miss.RetryAfter, Is.EqualTo(now.AddDays(7)));
        Assert.That(miss.MayQuery(now.AddDays(6)), Is.False, "not before its retry-after");
        Assert.That(miss.MayQuery(now.AddDays(7)), Is.True);

        miss.RecordAbsent(now.AddDays(7));
        Assert.That(miss.RetryAfter, Is.EqualTo(now.AddDays(7).AddDays(30)));

        miss.RecordAbsent(now.AddDays(40));
        Assert.That(miss.RetryAfter, Is.EqualTo(now.AddDays(40).AddDays(90)));

        // Most misses are permanent: an own-brand nobody has photographed will still not be in a
        // food database in six months, and asking forever spends the budget learning nothing.
        miss.RecordAbsent(now.AddDays(200));
        Assert.Multiple(() =>
        {
            Assert.That(miss.RetryAfter, Is.Null, "null means never again, not 'ask now'");
            Assert.That(miss.MayQuery(now.AddYears(10)), Is.False);
        });
    }

    [Test]
    public void Miss_SourceUnreachable_DoesNotConsumeAnAttempt()
    {
        var now = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        var miss = ProductCatalogMiss.Create(Unknown, ProductCatalogSources.OpenFoodFacts, now);

        miss.RecordUnreachable(now);

        Assert.Multiple(() =>
        {
            // The source was measured returning HTTP 503 on roughly half of all requests during a
            // day of testing.
            Assert.That(miss.Attempts, Is.Zero);
            Assert.That(miss.RetryAfter, Is.EqualTo(now.AddHours(6)));
        });
    }

    // ── The inline lookup, which is what makes a first scan resolve ──────────

    [Test]
    public async Task Scan_FirstScanOfAProductNobodyHasSeen_ResolvesInlineFromTheSource()
    {
        await SeedAsync();

        var api = StubProductApi.Finds(Cornflakes, "Cornflakes", "M-Budget");
        UseSource(api);

        var body = await ProductCatalogHarness.WithLiveLookupAsync(async () =>
            Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes })));

        var cached = await _context.ProductCatalogEntries.SingleAsync();

        Assert.Multiple(() =>
        {
            // The entire point of the feature, and it was unreachable while the only filler was a
            // background sweep an operator had to switch on: the first scan of a product resolves
            // rather than asking for a name.
            Assert.That(body.Item.Name, Is.EqualTo("Cornflakes"));
            Assert.That(body.Created, Is.True);
            Assert.That(body.Catalog, Is.Not.Null);
            Assert.That(body.Catalog!.Brand, Is.EqualTo("M-Budget"));

            Assert.That(api.Requested, Has.Count.EqualTo(1));
            Assert.That(api.Requested[0], Does.Contain("api/v3/product"),
                "v3 is the version the source recommends for new integrations");

            // Cached, so the second household to scan it costs nothing against a limit of 15 a
            // minute for the whole instance.
            Assert.That(cached.NameDe, Is.EqualTo("Cornflakes"));
            Assert.That(cached.SourceVersion, Is.EqualTo("live"),
                "which snapshot a row came from is what makes the 4.6 offer auditable");

            Assert.That(_context.ProductCatalogMisses.Any(), Is.False,
                "the question the miss row existed to ask has been answered");
        });
    }

    [Test]
    public async Task Scan_ARowThatExistsWithNoName_IsFilledInPlaceRatherThanDuplicated()
    {
        await SeedAsync();

        // The 4.6% case: a row the catalog holds and cannot name.
        await SeedCatalogAsync(de: null, fr: null, it: null, en: null);

        UseSource(StubProductApi.Finds(Cornflakes, "Cornflakes"));

        var body = await ProductCatalogHarness.WithLiveLookupAsync(async () =>
            Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes })));

        var rows = await _context.ProductCatalogEntries.ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(1));
            Assert.That(rows[0].NameDe, Is.EqualTo("Cornflakes"));
            Assert.That(rows[0].SourceVersion, Is.EqualTo("live"),
                "the stale snapshot is replaced whole, not merged into");
            Assert.That(body.Item.Name, Is.EqualTo("Cornflakes"));
        });
    }

    [Test]
    public async Task Scan_InlineHit_StillTeachesTheGuildNothing()
    {
        await SeedAsync();
        UseSource(StubProductApi.Finds(Cornflakes, "Cornflakes"));

        var body = await ProductCatalogHarness.WithLiveLookupAsync(async () =>
            Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes })));

        Assert.Multiple(() =>
        {
            // The licence boundary does not care where the row came from.
            Assert.That(_context.PantryBarcodes.Any(), Is.False);
            Assert.That(body.Learned, Is.False);
            Assert.That(body.Catalog, Is.Not.Null);
        });
    }

    [Test]
    public async Task Scan_GuildHasItsOwnName_NeverAsksTheSourceAtAll()
    {
        await SeedAsync();

        var api = StubProductApi.Finds(Cornflakes, "Migros Bio Vollmilch UHT 1L");
        UseSource(api);

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
        {
            // Taught the way a house teaches it: by stating the name once.
            await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes, Name = "milk" });

            var again = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

            Assert.Multiple(() =>
            {
                Assert.That(again.Item.Name, Is.EqualTo("milk"),
                    "they are the ones who have to recognise it on a shopping list");
                Assert.That(again.Catalog, Is.Null);

                // Not merely "the catalog lost the tie" - it was never consulted, so no token was
                // spent and no household's purchase was disclosed to ask a question we knew the
                // answer to.
                Assert.That(api.Requested, Is.Empty);
            });
        });
    }

    [Test]
    public async Task Scan_SourceDoesNotHaveTheProduct_RecordsTheAbsenceWithBackoff()
    {
        await SeedAsync();

        var api = StubProductApi.FindsNothing();
        UseSource(api);

        var result = await ProductCatalogHarness.WithLiveLookupAsync(async () =>
            await ScanAsync(new ScanPantryItemDto { Barcode = Unknown }));

        var miss = await _context.ProductCatalogMisses.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>(),
                "manual entry stays the normal outcome for cleaning products and toiletries");
            Assert.That(api.Requested, Has.Count.EqualTo(1));

            // A 404 is a real answer, so it takes the escalating backoff and eventually settles the
            // row for good.
            Assert.That(miss.Attempts, Is.EqualTo(1));
            Assert.That(miss.RetryAfter, Is.Not.Null);
            Assert.That(miss.RetryAfter, Is.GreaterThan(DateTimeOffset.UtcNow.AddDays(6)));
        });
    }

    [Test]
    public async Task Scan_SourceTimesOut_ReturnsUnresolvedAndConsumesNoBackoffAttempt()
    {
        await SeedAsync();

        var api = StubProductApi.Hangs();
        UseSource(api);

        var result = await ProductCatalogHarness.WithLiveLookupAsync(
            async () => await ScanAsync(new ScanPantryItemDto { Barcode = Unknown }),
            inlineTimeout: TimeSpan.FromMilliseconds(250));

        var miss = await _context.ProductCatalogMisses.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>(),
                "exactly what an empty catalog would have said, which is the promise");

            // The distinction the whole miss table turns on.
            Assert.That(miss.Attempts, Is.Zero);
            Assert.That(miss.RetryAfter, Is.Not.Null, "and it will be asked about again");
            Assert.That(miss.RetryAfter, Is.LessThan(DateTimeOffset.UtcNow.AddDays(1)));
        });
    }

    [Test]
    public async Task Scan_SourceHangs_CostsNoMoreThanTheTimeoutBudget()
    {
        await SeedAsync();
        UseSource(StubProductApi.Hangs());

        var clock = System.Diagnostics.Stopwatch.StartNew();

        await ProductCatalogHarness.WithLiveLookupAsync(
            async () => await ScanAsync(new ScanPantryItemDto { Barcode = Unknown }),
            inlineTimeout: TimeSpan.FromMilliseconds(250));

        clock.Stop();

        // The whole justification for putting a third party on the scan path.
        Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
            "a scan may never wait on a third party for longer than its own budget");
    }

    [Test]
    public async Task Scan_SourceIsUnwell_ConsumesNoBackoffAttemptEither()
    {
        await SeedAsync();
        UseSource(StubProductApi.Answers(HttpStatusCode.ServiceUnavailable, "nope"));

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
            await ScanAsync(new ScanPantryItemDto { Barcode = Unknown }));

        var miss = await _context.ProductCatalogMisses.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(miss.Attempts, Is.Zero, "a 503 is not evidence of absence");
            Assert.That(miss.RetryAfter, Is.Not.Null);
        });
    }

    [Test]
    public async Task Scan_OutOfRateLimitBudget_FallsStraightThroughWithoutAsking()
    {
        await SeedAsync();

        // One token, then nothing - which is what a real instance looks like within seconds of
        // somebody starting on a shopping bag, since the limit is 15 a minute for the whole IP.
        var api = StubProductApi.Finds(Cornflakes, "Cornflakes");
        UseSource(api, budget: 1);

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
        {
            var first = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));
            var second = await ScanAsync(new ScanPantryItemDto { Barcode = Unknown });

            var miss = await _context.ProductCatalogMisses.SingleAsync();

            Assert.Multiple(() =>
            {
                Assert.That(first.Item.Name, Is.EqualTo("Cornflakes"));

                // The second scan is not slower, not queued and not waiting for a token.
                Assert.That(second, Is.InstanceOf<BadRequest<string>>());
                Assert.That(api.Requested, Has.Count.EqualTo(1));

                // And nothing was held against the barcode for a question nobody put, so the sweep
                // finds it due.
                Assert.That(miss.Barcode, Is.EqualTo(Unknown));
                Assert.That(miss.Attempts, Is.Zero);
                Assert.That(miss.MayQuery(DateTimeOffset.UtcNow), Is.True);
            });
        });
    }

    [Test]
    public async Task Scan_MissStillInsideItsBackoff_DoesNotSpendATokenOnIt()
    {
        await SeedAsync();

        var api = StubProductApi.Finds(Unknown, "Something");
        UseSource(api);

        _context.ProductCatalogMisses.Add(new ProductCatalogMiss
        {
            Barcode = Unknown, Source = ProductCatalogSources.OpenFoodFacts,
            FirstMissedAt = DateTimeOffset.UtcNow, Attempts = 1,
            RetryAfter = DateTimeOffset.UtcNow.AddDays(7),
        });
        await _context.SaveChangesAsync();

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
            await ScanAsync(new ScanPantryItemDto { Barcode = Unknown }));

        // A flat re-scanning the same unlisted cleaning product every week must not spend the
        // instance's minute re-asking a question that has been answered three times.
        Assert.That(api.Requested, Is.Empty);
    }

    [Test]
    public async Task Scan_OnTheShippedDefaults_ResolvesWithoutAnybodyConfiguringAnything()
    {
        await SeedAsync();

        var api = StubProductApi.Finds(Cornflakes, "Cornflakes");

        // Deliberately no environment wrapper anywhere in this test.
        UseSource(api);

        var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

        Assert.Multiple(() =>
        {
            Assert.That(api.Requested, Has.Count.EqualTo(1));
            Assert.That(body.Item.Name, Is.EqualTo("Cornflakes"));
            Assert.That(body.Catalog, Is.Not.Null);
        });
    }

    [Test]
    public void Defaults_AreTheWorkingValuesAndTheLimitIsRespected()
    {
        var config = Env.ProductCatalog;

        Assert.Multiple(() =>
        {
            Assert.That(config.LiveFillEnabled, Is.True);
            Assert.That(config.ContactEmail, Is.Not.Empty,
                "Open Food Facts ask callers to identify themselves, and an unset variable must not "
                + "be the thing that decides whether we do");

            // The arithmetic that keeps a default deployment under their documented ceiling: a
            // token bucket's worst case in any sliding minute is its capacity plus its refill rate.
            Assert.That(config.BurstCapacity + config.RequestsPerMinute, Is.LessThan(15));

            Assert.That(config.InlineTimeout, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(2)));
        });
    }

    [Test]
    public async Task Scan_ContactAddressBlankedOnPurpose_DegradesToNoLookupRatherThanFailing()
    {
        await SeedAsync();

        var api = StubProductApi.Finds(Cornflakes, "Cornflakes");
        UseSource(api);

        // Enabled, with the address explicitly cleared.
        var previousFlag = Env.ProductCatalog.LiveFillEnabled;
        var previousEmail = Env.ProductCatalog.ContactEmail;

        Env.ProductCatalog.LiveFillEnabled = true;
        Env.ProductCatalog.ContactEmail = string.Empty;

        try
        {
            var result = await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes });

            Assert.Multiple(() =>
            {
                Assert.That(api.Requested, Is.Empty);
                Assert.That(result, Is.InstanceOf<BadRequest<string>>());
                Assert.That(_context.ProductCatalogMisses.Any(), Is.True,
                    "still queued, so putting the address back fills it");
            });
        }
        finally
        {
            Env.ProductCatalog.LiveFillEnabled = previousFlag;
            Env.ProductCatalog.ContactEmail = previousEmail;
        }
    }

    // ── The sweep, which is now the second chance rather than the only one ───

    [Test]
    public async Task Fill_AsksOnlyAboutMissesThatAreDue()
    {
        await SeedAsync();

        var now = DateTimeOffset.UtcNow;

        _context.ProductCatalogMisses.AddRange(
            new ProductCatalogMiss
            {
                Barcode = Cornflakes, Source = ProductCatalogSources.OpenFoodFacts,
                FirstMissedAt = now, RetryAfter = now.AddMinutes(-1),
            },
            new ProductCatalogMiss
            {
                Barcode = "7610000000001", Source = ProductCatalogSources.OpenFoodFacts,
                FirstMissedAt = now, Attempts = 1, RetryAfter = now.AddDays(7),
            },
            new ProductCatalogMiss
            {
                Barcode = "7610000000002", Source = ProductCatalogSources.OpenFoodFacts,
                FirstMissedAt = now, Attempts = 4, RetryAfter = null,
            });

        await _context.SaveChangesAsync();

        var api = StubProductApi.Finds(Cornflakes, "Cornflakes", "M-Budget");

        var filled = await ProductCatalogHarness.WithLiveLookupAsync(() => Filler(api).FillAsync());

        Assert.Multiple(() =>
        {
            Assert.That(filled, Is.EqualTo(1));
            Assert.That(api.Requested, Has.Count.EqualTo(1),
                "a miss inside its retry-after, and a settled one, are both left alone");
            Assert.That(api.Requested[0], Does.Contain(Cornflakes));
            Assert.That(_context.ProductCatalogEntries.Single().NameDe, Is.EqualTo("Cornflakes"));
            Assert.That(_context.ProductCatalogMisses.Any(m => m.Barcode == Cornflakes), Is.False,
                "the miss has been answered, so it is gone");
        });
    }

    [Test]
    public async Task Fill_Disabled_MakesNoRequestAtAll()
    {
        await SeedAsync();

        _context.ProductCatalogMisses.Add(ProductCatalogMiss.Create(
            Cornflakes, ProductCatalogSources.OpenFoodFacts, DateTimeOffset.UtcNow));
        await _context.SaveChangesAsync();

        var api = StubProductApi.Finds(Cornflakes, "Cornflakes");

        // On by default now that it is the path that makes a first scan resolve, so an operator who
        // does not want their server making requests on their flatmates' behalf turns it off - and
        // that has to stop the sweep as well as the scan.
        var filled = await ProductCatalogHarness.WithLiveLookupAsync(
            () => Filler(api).FillAsync(), enabled: false);

        Assert.Multiple(() =>
        {
            Assert.That(filled, Is.Zero);
            Assert.That(api.Requested, Is.Empty);
        });
    }

    [Test]
    public async Task Fill_OutOfBudget_LeavesTheMissExactlyAsItFoundIt()
    {
        await SeedAsync();

        var now = DateTimeOffset.UtcNow;

        _context.ProductCatalogMisses.Add(new ProductCatalogMiss
        {
            Barcode = Unknown, Source = ProductCatalogSources.OpenFoodFacts,
            FirstMissedAt = now, RetryAfter = now.AddMinutes(-1),
        });
        await _context.SaveChangesAsync();

        var api = StubProductApi.Finds(Unknown, "Something");

        await ProductCatalogHarness.WithLiveLookupAsync(() => Filler(api, budget: 0).FillAsync());

        var miss = await _context.ProductCatalogMisses.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(api.Requested, Is.Empty);

            // Not marked unreachable, which would cost it six hours: the source was never asked, and
            // a row must not inherit a penalty for a question a rate limiter refused to let us put.
            Assert.That(miss.Attempts, Is.Zero);
            Assert.That(miss.MayQuery(DateTimeOffset.UtcNow), Is.True);
        });
    }

    [Test]
    public async Task Fill_NotFound_ConsumesAnAttempt_Outage_DoesNot()
    {
        await SeedAsync();

        var now = DateTimeOffset.UtcNow;

        _context.ProductCatalogMisses.Add(new ProductCatalogMiss
        {
            Barcode = Unknown, Source = ProductCatalogSources.OpenFoodFacts,
            FirstMissedAt = now, RetryAfter = now.AddMinutes(-1),
        });
        await _context.SaveChangesAsync();

        await ProductCatalogHarness.WithLiveLookupAsync(
            () => Filler(StubProductApi.FindsNothing()).FillAsync());

        var afterAbsent = await _context.ProductCatalogMisses.SingleAsync();
        Assert.That(afterAbsent.Attempts, Is.EqualTo(1));

        afterAbsent.RetryAfter = now.AddMinutes(-1);
        await _context.SaveChangesAsync();

        await ProductCatalogHarness.WithLiveLookupAsync(
            () => Filler(StubProductApi.Answers(HttpStatusCode.ServiceUnavailable, "nope")).FillAsync());

        var afterOutage = await _context.ProductCatalogMisses.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(afterOutage.Attempts, Is.EqualTo(1), "a 503 is not evidence of absence");
            Assert.That(afterOutage.RetryAfter, Is.Not.Null);
        });
    }

    [Test]
    public async Task Fill_V2ShapedStatus_IsStillUnderstood()
    {
        await SeedAsync();

        _context.ProductCatalogMisses.Add(ProductCatalogMiss.Create(
            Cornflakes, ProductCatalogSources.OpenFoodFacts, DateTimeOffset.UtcNow));
        await _context.SaveChangesAsync();

        // v2 sends status as an integer, v3 as a string.
        var api = StubProductApi.Answers(HttpStatusCode.OK,
            """{"code":"7617027080224","status":1,"product":{"product_name_de":"Cornflakes"}}""");

        var filled = await ProductCatalogHarness.WithLiveLookupAsync(() => Filler(api).FillAsync());

        Assert.That(filled, Is.EqualTo(1));
    }

    [Test]
    public async Task Fill_TwoHundredSayingNotFound_IsReadAsTheAbsenceItIs()
    {
        await SeedAsync();

        _context.ProductCatalogMisses.Add(ProductCatalogMiss.Create(
            Unknown, ProductCatalogSources.OpenFoodFacts, DateTimeOffset.UtcNow));
        await _context.SaveChangesAsync();

        var api = StubProductApi.Answers(HttpStatusCode.OK,
            """{"status":"failure","result":{"id":"product_not_found"}}""");

        await ProductCatalogHarness.WithLiveLookupAsync(() => Filler(api).FillAsync());

        Assert.That((await _context.ProductCatalogMisses.SingleAsync()).Attempts, Is.EqualTo(1));
    }

    // ── The budget itself ────────────────────────────────────────────────────

    [Test]
    public async Task Budget_ConfiguredToZero_RefusesEveryLookup()
    {
        var previousRate = Env.ProductCatalog.RequestsPerMinute;
        Env.ProductCatalog.RequestsPerMinute = 0;

        try
        {
            // A configured zero is an operator saying "no outbound lookups", and it has to be read
            // that way rather than as a bucket that refills infinitely slowly and grants the first
            // token anyway.
            Assert.That(await ProductCatalogHarness.Limiter(budget: 1000).TryTakeAsync(), Is.False);
        }
        finally
        {
            Env.ProductCatalog.RequestsPerMinute = previousRate;
        }
    }

    [Test]
    public async Task Budget_CannotBeCounted_RefusesRatherThanAssuming()
    {
        // Fails closed.
        Assert.That(await ProductCatalogHarness.LimiterWithNoRedis().TryTakeAsync(), Is.False);
    }

    // ── Ingest ───────────────────────────────────────────────────────────────

    private ProductCatalogImportService Import() =>
        new(_context, NullLogger<ProductCatalogImportService>.Instance);

    private static Stream Ndjson(params string[] lines) =>
        new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

    private const string CornflakesLine =
        """{"barcode":"7617027080224","name_de":"Cornflakes","name_fr":"Corn Flakes","brand":"M-Budget, Migros","quantity":380,"quantity_unit":"g"}""";

    private const string SchinkenLine =
        """{"barcode":"7617100317001","name_de":"Hinterschinken","quantity":200,"quantity_unit":"g"}""";

    [Test]
    public async Task Import_LoadsRowsAndIsIdempotent()
    {
        var first = await Import().ImportAsync(
            Ndjson(CornflakesLine, SchinkenLine), ProductCatalogSources.OpenFoodFacts, "off-2026-08-01");

        var second = await Import().ImportAsync(
            Ndjson(CornflakesLine, SchinkenLine), ProductCatalogSources.OpenFoodFacts, "off-2026-08-01");

        var rows = await _context.ProductCatalogEntries.OrderBy(e => e.Barcode).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first.Created, Is.EqualTo(2));
            Assert.That(first.Updated, Is.Zero);

            // Upsert on the barcode rather than truncate-and-reload, so a re-run is safe and a
            // half-finished import is fixed by running it again.
            Assert.That(second.Created, Is.Zero);
            Assert.That(second.Updated, Is.EqualTo(2));

            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].NameDe, Is.EqualTo("Cornflakes"));
            Assert.That(rows[0].Quantity, Is.EqualTo(380m));
        });
    }

    [Test]
    public async Task Import_LaterExtractOverwritesTheEarlierValues()
    {
        await Import().ImportAsync(
            Ndjson(CornflakesLine), ProductCatalogSources.OpenFoodFacts, "off-2026-07-01");

        await Import().ImportAsync(
            Ndjson("""{"barcode":"7617027080224","name_de":"Cornflakes Classic","quantity":400,"quantity_unit":"g"}"""),
            ProductCatalogSources.OpenFoodFacts, "off-2026-08-01");

        var row = await _context.ProductCatalogEntries.SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(row.NameDe, Is.EqualTo("Cornflakes Classic"));
            Assert.That(row.NameFr, Is.Null, "a field the newer extract omits is cleared, not kept");
            Assert.That(row.SourceVersion, Is.EqualTo("off-2026-08-01"));
        });
    }

    [Test]
    public async Task Import_SkipsUselessRowsAndCountsMalformedOnesWithoutAbandoningTheFile()
    {
        var report = await Import().ImportAsync(
            Ndjson(
                CornflakesLine,
                "{ this is not json",
                """{"name_de":"No barcode at all"}""",
                """{"barcode":"7610000000003"}""",
                SchinkenLine),
            ProductCatalogSources.OpenFoodFacts, "off-2026-08-01");

        Assert.Multiple(() =>
        {
            Assert.That(report.Created, Is.EqualTo(2),
                "one bad line in a hundred-thousand-line extract must not abandon the rest");
            Assert.That(report.Malformed, Is.EqualTo(1));

            // No barcode is nothing to key on; no name in any language can never fill anything.
            Assert.That(report.Skipped, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Import_DeduplicatesABarcodeRepeatedInOneBatch()
    {
        var report = await Import().ImportAsync(
            Ndjson(CornflakesLine, CornflakesLine), ProductCatalogSources.OpenFoodFacts, "v1");

        Assert.Multiple(() =>
        {
            Assert.That(report.Created, Is.EqualTo(1));
            Assert.That(_context.ProductCatalogEntries.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Import_AnsweringAMiss_ClearsIt()
    {
        _context.ProductCatalogMisses.Add(ProductCatalogMiss.Create(
            Cornflakes, ProductCatalogSources.OpenFoodFacts, DateTimeOffset.UtcNow));
        await _context.SaveChangesAsync();

        var report = await Import().ImportAsync(
            Ndjson(CornflakesLine), ProductCatalogSources.OpenFoodFacts, "off-2026-08-01");

        Assert.Multiple(() =>
        {
            Assert.That(report.MissesResolved, Is.EqualTo(1));
            Assert.That(_context.ProductCatalogMisses.Any(), Is.False,
                "leaving it would leave the filler asking about a product we now hold");
        });
    }

    // ── The ODbL 4.6 export ──────────────────────────────────────────────────

    [Test]
    public async Task Export_ContainsTheCatalogAndNamesTheLicence()
    {
        await Import().ImportAsync(
            Ndjson(CornflakesLine, SchinkenLine), ProductCatalogSources.OpenFoodFacts, "off-2026-08-01");

        var (body, headers) = await ExportAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var metadata = JsonDocument.Parse(lines[0]).RootElement;
        var products = lines.Skip(1).Select(l => JsonDocument.Parse(l).RootElement).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(metadata.GetProperty("type").GetString(), Is.EqualTo("metadata"));

            // The file names its own licence, because a copy saved to disk loses its HTTP headers.
            Assert.That(metadata.GetProperty("license").GetString(), Does.Contain("Open Database License"));
            Assert.That(headers["X-License"].ToString(), Does.Contain("Open Database License"));

            Assert.That(products, Has.Count.EqualTo(2));
            Assert.That(products.Select(p => p.GetProperty("barcode").GetString()),
                Is.EquivalentTo(new[] { Cornflakes, "7617100317001" }));
            Assert.That(products[0].GetProperty("source_version").GetString(), Is.EqualTo("off-2026-08-01"),
                "which snapshot a row came from is what makes the 4.6 offer auditable");
        });
    }

    [Test]
    public async Task Export_ContainsNoNameAHouseholdTyped()
    {
        await SeedAsync();
        await Import().ImportAsync(
            Ndjson(CornflakesLine), ProductCatalogSources.OpenFoodFacts, "off-2026-08-01");

        // A household teaches its own name for a code the catalog also knows, plus one it does not.
        await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes, Name = "Znuni-Flocken" });
        await ScanAsync(new ScanPantryItemDto { Barcode = Unknown, Name = "Held WC-Reiniger" });

        var (body, _) = await ExportAsync();

        Assert.Multiple(() =>
        {
            // The whole reason the two live in separate tables.
            Assert.That(body, Does.Not.Contain("Znuni-Flocken"));
            Assert.That(body, Does.Not.Contain("Held WC-Reiniger"));
            Assert.That(body, Does.Not.Contain(GuildId));
            Assert.That(body, Does.Not.Contain(Anna));
            Assert.That(body, Does.Contain("Cornflakes"), "the derived database itself is there");
        });
    }

    [Test]
    public async Task Export_ResumesFromACursor()
    {
        await Import().ImportAsync(
            Ndjson(CornflakesLine, SchinkenLine), ProductCatalogSources.OpenFoodFacts, "v1");

        var (body, _) = await ExportAsync(after: Cornflakes);

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("7617100317001"));
            Assert.That(body, Does.Not.Contain($"\"barcode\":\"{Cornflakes}\""),
                "an interrupted download of a few hundred megabytes should continue, not restart");
        });
    }

    [Test]
    public async Task CatalogInfo_DescribesTheOfferAndTheBoundary()
    {
        await Import().ImportAsync(
            Ndjson(CornflakesLine), ProductCatalogSources.OpenFoodFacts, "off-2026-08-01");

        var info = ((Ok<ProductCatalogInfoDto>)await _catalogEndpoint.InfoAsync(_context)).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(info.Count, Is.EqualTo(1));
            Assert.That(info.SourceVersions, Is.EqualTo(new[] { "off-2026-08-01" }));
            Assert.That(info.LicenseUrl, Is.Not.Empty);
            Assert.That(info.ExportUrl, Is.Not.Empty);
            Assert.That(info.Notice, Does.Contain("no product names entered by users"));
        });
    }

    // ── The two tables are never one payload, and never one join ────────────

    [Test]
    public void TheTwoBarcodeTables_ShareNoRelationshipInTheModel()
    {
        var model = _context.Model;

        var catalog = model.FindEntityType(typeof(ProductCatalogEntry))!;
        var miss = model.FindEntityType(typeof(ProductCatalogMiss))!;
        var taught = model.FindEntityType(typeof(PantryBarcode))!;

        Assert.Multiple(() =>
        {
            // No foreign key out of the ODbL boundary, and none into it.
            Assert.That(catalog.GetForeignKeys(), Is.Empty);
            Assert.That(miss.GetForeignKeys(), Is.Empty);

            Assert.That(model.GetEntityTypes()
                    .SelectMany(e => e.GetForeignKeys())
                    .Where(fk => fk.PrincipalEntityType == catalog || fk.PrincipalEntityType == miss),
                Is.Empty);

            Assert.That(taught.GetForeignKeys().Select(fk => fk.PrincipalEntityType.ClrType),
                Does.Not.Contain(typeof(ProductCatalogEntry)));

            Assert.That(catalog.GetTableName(), Is.Not.EqualTo(taught.GetTableName()));
        });
    }

    [Test]
    public void TheTwoBarcodeTables_AreNeverFlattenedIntoOneResponseObject()
    {
        // A structural guard, because the failure this protects against is a tidy-up rather than a
        // bug: somebody merging the guild's learned name and the catalog's name into one "product"
        // object on the wire, at which point the licence boundary exists only in a comment.
        var learned = typeof(PantryBarcodeDto).GetProperties().Select(p => p.Name).ToList();
        var sourced = typeof(ProductCatalogMatchDto).GetProperties().Select(p => p.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(learned, Does.Not.Contain("Source"));
            Assert.That(learned, Does.Not.Contain("License"));
            Assert.That(learned, Does.Not.Contain("Attribution"),
                "a learned name has no source to attribute: the house is the source");

            Assert.That(sourced, Does.Not.Contain("GuildId"));
            Assert.That(sourced, Does.Not.Contain("TimesSeen"));
            Assert.That(sourced, Does.Not.Contain("LastUsedAt"),
                "how often one house scans something is not part of a public product database");

            // On the scan response the two are separate members, not one merged name.
            var scan = typeof(ScanPantryItemResultDto).GetProperties().Select(p => p.Name).ToList();
            Assert.That(scan, Does.Contain("Learned"));
            Assert.That(scan, Does.Contain("Catalog"));
        });
    }

    // ── Four databases behind one request ────────────────────────────────────

    [Test]
    public async Task Scan_LiveLookup_AsksAboutEveryProductType()
    {
        await SeedAsync();

        var api = StubProductApi.Finds(Cornflakes, "Cornflakes");
        UseSource(api);

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
        {
            await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes });

            // The whole non-food feature rests on this one parameter.
            Assert.That(api.Requested.Single(), Does.Contain("product_type=all"));
        });
    }

    [Test]
    public async Task Scan_LiveBeautyProduct_IsCreditedToOpenBeautyFacts()
    {
        await SeedAsync();

        var shampoo = "3600523351893";
        UseSource(StubProductApi.Finds(shampoo, "Shampoo Elseve", productType: "beauty"));

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
        {
            var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = shampoo }));

            Assert.Multiple(() =>
            {
                Assert.That(body.Catalog, Is.Not.Null);
                Assert.That(body.Catalog!.Name, Is.EqualTo("Shampoo Elseve"));

                // ODbL 4.3 asks the notice to name the database the data came from.
                Assert.That(body.Catalog.Source, Is.EqualTo(ProductCatalogSources.OpenBeautyFacts));
                Assert.That(body.Catalog.SourceName, Is.EqualTo("Open Beauty Facts"));
                Assert.That(body.Catalog.SourceUrl,
                    Is.EqualTo($"https://world.openbeautyfacts.org/product/{shampoo}"));
                Assert.That(body.Catalog.Attribution, Does.Contain("Open Beauty Facts"));

                // The licence is the one thing that does not vary: all four are ODbL 1.0, which is
                // why they can share a table and an export at all.
                Assert.That(body.Catalog.License, Is.EqualTo(ProductCatalogSources.LicenseName));

                // And the stored row carries it, so the export and any later scan agree.
                Assert.That(_context.ProductCatalogEntries.Single().Source,
                    Is.EqualTo(ProductCatalogSources.OpenBeautyFacts));
            });
        });
    }

    [Test]
    public async Task Scan_ProductHeldInASiblingDatabase_IsNotRecordedAsAbsent()
    {
        await SeedAsync();

        // The reply that used to be indistinguishable from "no such barcode": a 404 whose body says
        // the product exists, just not here.
        UseSource(StubProductApi.FindsInAnotherFlavour());

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
        {
            var before = DateTimeOffset.UtcNow;

            await ScanAsync(new ScanPantryItemDto { Barcode = Unknown });

            var miss = _context.ProductCatalogMisses.Single();

            Assert.Multiple(() =>
            {
                Assert.That(miss.Attempts, Is.Zero,
                    "the source confirmed the product exists, so this is not evidence of absence");

                Assert.That(miss.RetryAfter, Is.Not.Null,
                    "and it must stay askable rather than being settled as permanently missing");

                // A new miss is created already due, so "still due" is also what an untouched row
                // looks like.
                Assert.That(miss.RetryAfter, Is.GreaterThan(before.AddMinutes(1)),
                    "a wrong-flavour reply must cool off like an outage, not be silently ignored");
            });
        });
    }

    [Test]
    public async Task Scan_ProductWithAnUnrecognisedType_StillResolvesAndCreditsTheFoodDatabase()
    {
        await SeedAsync();

        // A product type this build has never heard of, which is what a new flavour would look like
        // before anyone updated the mapping.
        UseSource(StubProductApi.Finds(Cornflakes, "Cornflakes", productType: "groceries-v2"));

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
        {
            var body = Body(await ScanAsync(new ScanPantryItemDto { Barcode = Cornflakes }));

            Assert.Multiple(() =>
            {
                Assert.That(body.Catalog, Is.Not.Null);
                Assert.That(body.Catalog!.Source, Is.EqualTo(ProductCatalogSources.OpenFoodFacts));
                Assert.That(body.Catalog.SourceName, Is.EqualTo("Open Food Facts"));
            });
        });
    }

    [Test]
    public async Task Scan_GenuineAbsence_StillTakesTheAbsenceBackoff()
    {
        await SeedAsync();

        // The regression guard for the test above: distinguishing the wrong-flavour reply must not
        // have made every 404 look like one.
        UseSource(StubProductApi.FindsNothing());

        await ProductCatalogHarness.WithLiveLookupAsync(async () =>
        {
            await ScanAsync(new ScanPantryItemDto { Barcode = Unknown });

            Assert.That(_context.ProductCatalogMisses.Single().Attempts, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Info_WithRowsFromSeveralDatabases_CreditsEachOfThem()
    {
        await SeedAsync();
        await SeedCatalogAsync();
        await SeedCatalogAsync(barcode: "3600523351893", de: "Shampoo", source: ProductCatalogSources.OpenBeautyFacts);
        await SeedCatalogAsync(barcode: "7610000000123", de: "Putzmittel", source: ProductCatalogSources.OpenProductsFacts);

        var info = ((Ok<ProductCatalogInfoDto>)await _catalogEndpoint.InfoAsync(_context)).Value!;

        Assert.Multiple(() =>
        {
            Assert.That(info.Sources, Has.Count.EqualTo(3));

            Assert.That(info.Sources.Select(s => s.Source), Is.EquivalentTo(new[]
            {
                ProductCatalogSources.OpenFoodFacts,
                ProductCatalogSources.OpenBeautyFacts,
                ProductCatalogSources.OpenProductsFacts,
            }));

            Assert.That(
                info.Sources.Single(s => s.Source == ProductCatalogSources.OpenBeautyFacts).Attribution,
                Does.Contain("Open Beauty Facts"));

            // The scalar fields stay put for clients written when there was one database.
            Assert.That(info.Source, Is.EqualTo(ProductCatalogSources.OpenFoodFacts));
        });
    }

    [Test]
    public async Task Export_WithRowsFromSeveralDatabases_NamesThemAllInOneNotice()
    {
        await SeedAsync();
        await SeedCatalogAsync();
        await SeedCatalogAsync(barcode: "3600523351893", de: "Shampoo", source: ProductCatalogSources.OpenBeautyFacts);

        var (body, headers) = await ExportAsync();

        var metadata = JsonDocument.Parse(body.Split('\n')[0]).RootElement;

        Assert.Multiple(() =>
        {
            // ODbL 4.3 wants the notice to name the database being credited.
            Assert.That(headers["X-Attribution"].ToString(), Does.Contain("Open Food Facts"));
            Assert.That(headers["X-Attribution"].ToString(), Does.Contain("Open Beauty Facts"));

            Assert.That(metadata.GetProperty("attribution").GetString(),
                Does.Contain("Open Beauty Facts"));

            Assert.That(metadata.GetProperty("sources").GetArrayLength(), Is.EqualTo(2));
        });
    }

    // ── Import endpoint: the body size cap ───────────────────────────────────

    [Test]
    public async Task Import_ForAnAdministrator_LiftsTheBodySizeCap()
    {
        var size = new StubBodySizeLimit();

        var result = await ImportEndpointAsync(
            """{"barcode":"7617027080224","name_de":"Cornflakes"}""", size: size);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ProductCatalogImportResultDto>>());

            // Null is "no limit", which is the only answer that works: the caller is an instance
            // administrator posting a file whose size is a property of the source data, and any
            // number picked here would be a number the next export outgrows.
            Assert.That(size.MaxRequestBodySize, Is.Null);
        });
    }

    /// <summary>The lift is a privilege, not a default.</summary>
    [Test]
    public async Task Import_ForANonAdministrator_LeavesTheBodySizeCapInForce()
    {
        var size = new StubBodySizeLimit();

        var result = await ImportEndpointAsync(
            """{"barcode":"7617027080224","name_de":"Cornflakes"}""", administrator: false, size: size);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(size.MaxRequestBodySize, Is.EqualTo(StubBodySizeLimit.Default));
        });
    }

    /// <summary>A rejected import must not have moved anything first.</summary>
    [Test]
    public async Task Import_WithAnUnknownSource_LeavesTheBodySizeCapInForce()
    {
        var size = new StubBodySizeLimit();

        var result = await ImportEndpointAsync(
            """{"barcode":"7617027080224","name_de":"Cornflakes"}""",
            source: "openhouseholdfacts", size: size);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<BadRequest<string>>());
            Assert.That(size.MaxRequestBodySize, Is.EqualTo(StubBodySizeLimit.Default));
        });
    }

    /// <summary>Kestrel makes the cap read-only once the body has begun to be read, and throws on an
    /// attempt to set it. An import must not die of trying to be helpful, so the endpoint asks first
    /// and settles for the default cap when the answer is no.</summary>
    [Test]
    public async Task Import_WhenTheCapCannotBeChanged_StillImports()
    {
        var size = new StubBodySizeLimit { IsReadOnly = true };

        var result = await ImportEndpointAsync(
            """{"barcode":"7617027080224","name_de":"Cornflakes"}""", size: size);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<ProductCatalogImportResultDto>>());
            Assert.That(_context.ProductCatalogEntries.Count(), Is.EqualTo(1));
        });
    }

    /// <summary>A host that implements no such feature at all - which is every in-process test
    /// server - must not be a reason the endpoint fails.</summary>
    [Test]
    public async Task Import_WithNoBodySizeFeatureAtAll_StillImports()
    {
        var result = await ImportEndpointAsync(
            """{"barcode":"7617027080224","name_de":"Cornflakes"}""", size: null);

        Assert.That(result, Is.InstanceOf<Ok<ProductCatalogImportResultDto>>());
    }

    /// <summary>Stands in for Kestrel's own <see cref="IHttpMaxRequestBodySizeFeature"/>, including
    /// the part that matters: setting it after the body has been read is an exception, not a no-op.
    /// A stub that silently accepted the write would pass whether or not the endpoint checked.</summary>
    private sealed class StubBodySizeLimit : IHttpMaxRequestBodySizeFeature
    {
        /// <summary>Kestrel's own default, in bytes.</summary>
        public const long Default = 30_000_000;

        private long? _max = Default;

        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize
        {
            get => _max;
            set
            {
                if (IsReadOnly)
                    throw new InvalidOperationException(
                        "The maximum request body size cannot be modified after the read has started.");

                _max = value;
            }
        }
    }

    /// <summary>The import endpoint over a body, with the administrator check answered by a canned
    /// bus reply rather than by Identity.</summary>
    private async Task<IResult> ImportEndpointAsync(
        string ndjson, bool administrator = true, string? source = null,
        StubBodySizeLimit? size = null)
    {
        var bus = new FakeInvokingMessageBus();

        bus.SetResponse<IsUserAdministrativeRequest>(
            new IsUserAdministrativeResponse { IsAdministrative = administrator });

        var http = new DefaultHttpContext { Request = { Body = new MemoryStream(Encoding.UTF8.GetBytes(ndjson)) } };

        if (size is not null) http.Features.Set<IHttpMaxRequestBodySizeFeature>(size);

        return await _catalogEndpoint.ImportAsync(
            "test-snapshot", source, Import(), bus, http, TestPrincipal.Create(OwnerId),
            NullLogger<ProductCatalogEndpoint>.Instance);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    /// <summary>The sweep, wired to a stand-in for the product API and a given number of tokens.</summary>
    private ProductCatalogFillService Filler(StubProductApi api, int budget = 1000) =>
        new(_context, ProductCatalogHarness.Lookups(api, budget),
            NullLogger<ProductCatalogFillService>.Instance);

    private async Task<(string Body, IHeaderDictionary Headers)> ExportAsync(string? after = null)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        var buffer = new MemoryStream();
        http.Response.Body = buffer;

        // Awaited twice on purpose: the endpoint became async when it started resolving the set of
        // databases it credits before writing headers, so the call yields an IResult rather than
        // being one.
        var result = await _catalogEndpoint.ExportAsync(after, _context, http);

        await result.ExecuteAsync(http);

        return (Encoding.UTF8.GetString(buffer.ToArray()), http.Response.Headers);
    }
}
