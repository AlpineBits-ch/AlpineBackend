using System.Reflection;
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
/// The maintenance module end to end: the sweep that finds due services and lapsing warranties, the
/// broken-status transition, the two permissions, the record paging and the attention board.
/// </summary>
[TestFixture]
public class MaintenanceServiceTests
{
    private const string GuildId = "guild-1";
    private const string OwnerId = "owner-1";
    private const string UpkeepId = "chan-upkeep";
    private const string CellarId = "chan-cellar";
    private const string EveryoneRoleId = "role-everyone";
    private const string FlatmateRoleId = "role-flatmates";

    /// <summary>Holds @everyone only, so LogMaintenance and nothing heavier.</summary>
    private const string Anna = "anna";

    /// <summary>Also in Flatmates, so ManageMaintenance.</summary>
    private const string Ben = "ben";

    private FakeDistributedCache _cache = null!;
    private MaintenanceContext _context = null!;
    private GuildPermissionService _permissions = null!;
    private FakeHubContext _hub = null!;
    private FakeMessageBus _bus = null!;
    private MaintenanceEndpoint _endpoint = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = new FakeDistributedCache();
        _context = new MaintenanceContext(Guid.NewGuid().ToString());
        _permissions = new GuildPermissionService(_cache, _context, NullLogger<GuildPermissionService>.Instance);
        _hub = new FakeHubContext();
        _bus = new FakeMessageBus();
        _endpoint = new MaintenanceEndpoint();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    /// <summary>The maintenance entities are not in <c>MicroserviceContext</c> yet - the integrator
    /// pastes their configuration in - so this fixture adds just enough of it to build a model.
    /// Guarded on the type being absent, so the day the paste lands this quietly stops doing
    /// anything rather than double-configuring.</summary>
    private sealed class MaintenanceContext(string dbName) : MicroserviceContext(
        new DbContextOptionsBuilder<MicroserviceContext>().UseInMemoryDatabase(dbName).Options)
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // Intentionally empty: the InMemory provider is configured through the constructor, and
            // calling base would add a conflicting Postgres provider.
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            if (modelBuilder.Model.FindEntityType(typeof(MaintenanceAsset)) is null)
            {
                modelBuilder.Entity<MaintenanceAsset>(b =>
                {
                    b.HasIndex(x => new { x.ChannelId, x.Name });
                    b.HasIndex(x => new { x.GuildId, x.Status });
                });
            }

            if (modelBuilder.Model.FindEntityType(typeof(MaintenanceRecord)) is null)
            {
                modelBuilder.Entity<MaintenanceRecord>(b =>
                    b.HasIndex(x => new { x.ChannelId, x.PerformedAt }));
            }
        }
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private async Task SeedAsync(GuildFeatures features = GuildFeaturePresets.Household)
    {
        var now = DateTimeOffset.UtcNow;

        _context.Guilds.Add(new Guild.Domain.Aggregates.Guild
        {
            Id = GuildId, OwnerId = OwnerId, Name = "The Flat", Features = features,
            Kind = GuildKind.Household, CreatedAt = now, UpdatedAt = now,
        });

        _context.Roles.AddRange(
            new Role
            {
                Id = EveryoneRoleId, GuildId = GuildId, Type = RoleType.Everyone, Name = "Everyone",
                Permissions = Role.DefaultEveryonePermissions, ModulePermissions = Role.DefaultEveryoneModulePermissions, CreatedAt = now, UpdatedAt = now,
            },
            new Role
            {
                Id = FlatmateRoleId, GuildId = GuildId, Type = RoleType.None, Name = "Flatmates",
                ModulePermissions = Role.FlatmatePermissions, CreatedAt = now, UpdatedAt = now,
            });

        foreach (var id in new[] { UpkeepId, CellarId })
        {
            _context.Channels.Add(new Channel
            {
                Id = id, GuildId = GuildId, Name = id, Type = ChannelType.Maintenance,
                CreatedAt = now, UpdatedAt = now,
            });
        }

        foreach (var userId in new[] { Anna, Ben })
        {
            _context.GuildMembers.Add(new GuildMember
            {
                Id = $"member-{userId}", GuildId = GuildId, UserId = userId, JoinedAt = DateTime.UtcNow,
                SearchValue = userId.ToUpperInvariant(), CreatedAt = now, UpdatedAt = now,
            });

            _context.RoleMembers.Add(new RoleMember
            {
                Id = $"rm-everyone-{userId}", RoleId = EveryoneRoleId, MemberId = $"member-{userId}",
                CreatedAt = now, UpdatedAt = now,
            });
        }

        _context.RoleMembers.Add(new RoleMember
        {
            Id = "rm-flatmate-ben", RoleId = FlatmateRoleId, MemberId = $"member-{Ben}",
            CreatedAt = now, UpdatedAt = now,
        });

        await _context.SaveChangesAsync();
    }

    /// <summary>The cellar denies @everyone and allows only Flatmates, so Anna cannot see it and
    /// Ben can - the shape a house with a locked cellar or a guest actually has.</summary>
    private async Task RestrictCellarToFlatmatesAsync()
    {
        var now = DateTimeOffset.UtcNow;

        _context.Set<ChannelPermission>().AddRange(
            new ChannelPermission
            {
                // The whole @everyone mask, not just ViewChannel: the permission expansion
                // re-grants ViewChannel from anything that implies it, so a deny of the one bit
                // is silently undone.
                Id = "chpr-cellar-deny", ChannelId = CellarId, RoleId = EveryoneRoleId,
                DenyPermissions = Role.DefaultEveryonePermissions, DenyModulePermissions = Role.DefaultEveryoneModulePermissions, CreatedAt = now, UpdatedAt = now,
            },
            new ChannelPermission
            {
                Id = "chpr-cellar-allow", ChannelId = CellarId, RoleId = FlatmateRoleId,
                AllowPermissions = Permissions.ViewChannel, CreatedAt = now, UpdatedAt = now,
            });

        await _context.SaveChangesAsync();
    }

    private async Task<MaintenanceAsset> AddAssetAsync(
        string channelId = UpkeepId,
        string name = "Boiler",
        int? intervalDays = null,
        DateTimeOffset? lastServicedAt = null,
        DateTimeOffset? warrantyUntil = null,
        AssetStatus status = AssetStatus.Ok)
    {
        var asset = MaintenanceAsset.Create(new CreateMaintenanceAssetParams
        {
            ChannelId = channelId, GuildId = GuildId, Name = name,
            ServiceIntervalDays = intervalDays, LastServicedAt = lastServicedAt,
            WarrantyUntil = warrantyUntil, AddedByUserId = Anna,
        });

        asset.Status = status;

        _context.Set<MaintenanceAsset>().Add(asset);
        await _context.SaveChangesAsync();
        return asset;
    }

    private async Task<MaintenanceRecord> AddRecordAsync(string title, DateTimeOffset performedAt,
        string? assetId = null)
    {
        var record = MaintenanceRecord.Create(new CreateMaintenanceRecordParams
        {
            AssetId = assetId, ChannelId = UpkeepId, GuildId = GuildId, Title = title,
            PerformedAt = performedAt, PerformedByUserId = Anna,
        });

        _context.Set<MaintenanceRecord>().Add(record);
        await _context.SaveChangesAsync();
        return record;
    }

    /// <summary>A window that always contains "now", so a deferred alert is deterministic rather
    /// than depending on what time the test suite happens to run.</summary>
    private async Task SeedQuietHoursCoveringNowAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var startMinute = now.Hour * 60 + now.Minute;

        _context.GuildQuietHoursConfigs.Add(new GuildQuietHoursConfig
        {
            GuildId = GuildId, Enabled = true, TimeZoneId = "UTC",
            StartMinuteLocal = startMinute, EndMinuteLocal = (startMinute + 120) % 1440,
            UpdatedAt = now,
        });
        await _context.SaveChangesAsync();
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private HouseholdChannelService Household() => new(
        _context, _permissions,
        new GuildHydrateService(RedisTestFactory.Create(), NullLogger<GuildHydrateService>.Instance),
        new ChannelAudienceService(_permissions, new MemoryCache(new MemoryCacheOptions())),
        _hub);

    private MaintenanceAlertService Alerts() => new(
        _context,
        new HouseholdNotifier(_context, new NotificationResolutionService(_context), _hub, _bus),
        _permissions,
        NullLogger<MaintenanceAlertService>.Instance);

    private MaintenanceService Sweep() => new(
        _context, Alerts(), _permissions, NullLogger<MaintenanceService>.Instance);

    private AuditLogService Audit() => new(_context);

    private List<HouseholdPushRequested> Pushes() => _bus.Published.OfType<HouseholdPushRequested>().ToList();

    private static object? Value(IResult result) => (result as IValueHttpResult)?.Value;

    private static T Read<T>(object? value, string property) =>
        (T)value!.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.GetValue(value)!;

    // ══════════════════════════════════════════════════════════════════════════ The service sweep
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Sweep_AnnouncesAServiceThatHasComeDue()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(intervalDays: 365, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-366));

        var handled = await Sweep().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.EqualTo(1));
            Assert.That(asset.ServiceNotifiedAt, Is.Not.Null);
            Assert.That(Pushes().Single().Body, Is.EqualTo("This is due for a service."));
            Assert.That(Pushes().Single().TargetId, Is.EqualTo(asset.Id));
            Assert.That(Pushes().Single().Title, Is.EqualTo("Boiler"), "the asset's own name, untranslated");
        });
    }

    [Test]
    public async Task Sweep_AnnouncesADueServiceOnlyOnce()
    {
        await SeedAsync();
        await AddAssetAsync(intervalDays: 365, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-366));

        Assert.Multiple(async () =>
        {
            Assert.That(await Sweep().SweepAsync(), Is.EqualTo(1));
            Assert.That(await Sweep().SweepAsync(), Is.Zero,
                "ServiceNotifiedAt is what makes the sweep at-most-once");
        });
    }

    /// <summary>A boiler two months late is a standing fact about the house, not news.</summary>
    [Test]
    public async Task Sweep_ALongOverdueService_IsRetiredWithoutBeingAnnounced()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(intervalDays: 30, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-90));

        var handled = await Sweep().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(handled, Is.EqualTo(1), "it was dealt with");
            Assert.That(asset.ServiceNotifiedAt, Is.Not.Null, "and stamped, so it leaves the candidate set");
            Assert.That(Pushes(), Is.Empty, "but nobody was buzzed about June");
        });
    }

    [Test]
    public async Task Sweep_AnUnscheduledAsset_IsNeverACandidate()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(intervalDays: null);

        Assert.Multiple(async () =>
        {
            Assert.That(await Sweep().SweepAsync(), Is.Zero);
            Assert.That(asset.ServiceNotifiedAt, Is.Null);
        });
    }

    [Test]
    public async Task Sweep_AServiceNotYetDue_IsLeftAlone()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(intervalDays: 365, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-10));

        Assert.Multiple(async () =>
        {
            Assert.That(await Sweep().SweepAsync(), Is.Zero);
            Assert.That(asset.ServiceNotifiedAt, Is.Null);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Warranties, and the cutoff on each side of the window
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Sweep_WarnsAboutAWarrantyInsideTheWindow()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(warrantyUntil: DateTimeOffset.UtcNow.AddDays(21));

        await Sweep().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(asset.WarrantyNotifiedAt, Is.Not.Null);
            Assert.That(Pushes().Single().Body, Is.EqualTo("The warranty runs out in 3 weeks."));
            Assert.That(Pushes().Single().BodyLocArgs, Is.EqualTo(new[] { "3 weeks" }),
                "the duration is preformatted, so no client has to own a unit table");
        });
    }

    [Test]
    public async Task Sweep_WarnsAboutAWarrantyOnlyOnce()
    {
        await SeedAsync();
        await AddAssetAsync(warrantyUntil: DateTimeOffset.UtcNow.AddDays(21));

        await Sweep().SweepAsync();
        _bus.Published.Clear();

        await Sweep().SweepAsync();

        Assert.That(Pushes(), Is.Empty);
    }

    [Test]
    public async Task Sweep_AWarrantyBeyondTheWindow_IsNotACandidate()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(warrantyUntil: DateTimeOffset.UtcNow.AddDays(60));

        await Sweep().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Is.Empty, "two months out is not news yet");
            Assert.That(asset.WarrantyNotifiedAt, Is.Null, "and it must still warn when it gets close");
        });
    }

    /// <summary>The far edge.</summary>
    [Test]
    public async Task Sweep_AWarrantyThatHasAlreadyLapsed_IsRetiredWithoutBeingAnnounced()
    {
        await SeedAsync();
        var recent = await AddAssetAsync(name: "Dryer", warrantyUntil: DateTimeOffset.UtcNow.AddDays(-2));
        var ancient = await AddAssetAsync(name: "Kettle", warrantyUntil: DateTimeOffset.UtcNow.AddDays(-400));

        await Sweep().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Is.Empty);
            Assert.That(recent.WarrantyNotifiedAt, Is.Not.Null);
            Assert.That(ancient.WarrantyNotifiedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task Sweep_WithTheModuleOff_AnnouncesNothingAndStampsNothing()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Maintenance);
        var asset = await AddAssetAsync(intervalDays: 365, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-366));

        await Sweep().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Is.Empty);
            Assert.That(asset.ServiceNotifiedAt, Is.Null,
                "left unstamped, so switching the module back on still warns");
        });
    }

    /// <summary>Quiet hours were built for exactly this.</summary>
    [Test]
    public async Task Sweep_InsideQuietHours_DefersWithoutStamping()
    {
        await SeedAsync();
        await SeedQuietHoursCoveringNowAsync();

        // Due right now rather than a day ago: DeferPast moves the alert instant to the end of the
        // window, so a due date already a day old has a deferral that expired with it.
        var asset = await AddAssetAsync(intervalDays: 365, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-365));

        await Sweep().SweepAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Is.Empty);
            Assert.That(asset.ServiceNotifiedAt, Is.Null);
        });
    }

    [Test]
    public void DescribeDuration_ReadsLikeSomethingAPersonWouldSay()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MaintenanceAlertService.DescribeDuration(TimeSpan.FromDays(21)), Is.EqualTo("3 weeks"));
            Assert.That(MaintenanceAlertService.DescribeDuration(TimeSpan.FromDays(14)), Is.EqualTo("2 weeks"));
            Assert.That(MaintenanceAlertService.DescribeDuration(TimeSpan.FromDays(5)), Is.EqualTo("5 days"));
            Assert.That(MaintenanceAlertService.DescribeDuration(TimeSpan.FromDays(1)), Is.EqualTo("1 day"));
            Assert.That(MaintenanceAlertService.DescribeDuration(TimeSpan.FromHours(3)), Is.EqualTo("1 day"));
            Assert.That(MaintenanceAlertService.DescribeDuration(TimeSpan.Zero), Is.EqualTo("less than a day"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Broken: the transition, not the value
    // ══════════════════════════════════════════════════════════════════════════

    private Task<IResult> SetStatusAsync(string assetId, AssetStatus status, string userId, string? note = null) =>
        _endpoint.SetStatusAsync(assetId, new UpdateAssetStatusDto { Status = status, Note = note },
            Household(), Alerts(), _context, Audit(), TestPrincipal.Create(userId));

    [Test]
    public async Task MarkingSomethingBroken_TellsTheHouse()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(name: "Washing machine");

        var result = await SetStatusAsync(asset.Id, AssetStatus.Broken, Anna, "leaks on a hot wash");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<MaintenanceAssetDto>>());
            Assert.That(asset.Status, Is.EqualTo(AssetStatus.Broken));
            Assert.That(Pushes().Single().Body, Is.EqualTo("Marked as broken. Don't use it for now."));
            Assert.That(Pushes().Single().Title, Is.EqualTo("Washing machine"));
        });
    }

    /// <summary>Editing the note on an already-broken machine three times is normal, and must not
    /// buzz the house three times - the rule decision.blocked follows.</summary>
    [Test]
    public async Task ReSavingTheSameBrokenStatus_TellsNobody()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();

        await SetStatusAsync(asset.Id, AssetStatus.Broken, Anna);
        _bus.Published.Clear();

        await SetStatusAsync(asset.Id, AssetStatus.Broken, Anna, "and the drum rattles");

        Assert.Multiple(() =>
        {
            Assert.That(Pushes(), Is.Empty);
            Assert.That(asset.StatusNote, Is.EqualTo("and the drum rattles"), "the edit still landed");
        });
    }

    [Test]
    public async Task MarkingSomethingBroken_DoesNotTellTheFinder()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();

        await SetStatusAsync(asset.Id, AssetStatus.Broken, Anna);

        Assert.That(Pushes().Single().UserIds, Is.EqualTo(new[] { Ben }),
            "the person standing in front of it already knows");
    }

    [Test]
    public async Task MarkingSomethingFixedAgainThenBroken_TellsTheHouseAgain()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();

        await SetStatusAsync(asset.Id, AssetStatus.Broken, Anna);
        await SetStatusAsync(asset.Id, AssetStatus.Ok, Ben);
        _bus.Published.Clear();

        await SetStatusAsync(asset.Id, AssetStatus.Broken, Anna);

        Assert.That(Pushes(), Has.Count.EqualTo(1), "a second breakage is a second event");
    }

    [Test]
    public async Task SetStatus_RejectsAnUnknownStatus()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();

        var result = await SetStatusAsync(asset.Id, (AssetStatus)99, Anna);

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    // ══════════════════════════════════════════════════════════════════════════ The two
    // permissions ══════════════════════════════════════════════════════════════════════════

    /// <summary>The deliberate asymmetry.</summary>
    [Test]
    public async Task LogMaintenance_IsEnoughToMarkSomethingBroken()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();

        var result = await SetStatusAsync(asset.Id, AssetStatus.Broken, Anna);

        Assert.That(result, Is.InstanceOf<Ok<MaintenanceAssetDto>>());
    }

    [Test]
    public async Task LogMaintenance_IsNotEnoughToEditTheAsset()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();

        var result = await _endpoint.UpdateAssetAsync(asset.Id,
            new UpdateMaintenanceAssetDto { Name = "Renamed" },
            Household(), _context, Audit(), TestPrincipal.Create(Anna));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(asset.Name, Is.EqualTo("Boiler"));
        });
    }

    [Test]
    public async Task ManageMaintenance_CanEditTheAsset()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();

        var result = await _endpoint.UpdateAssetAsync(asset.Id,
            new UpdateMaintenanceAssetDto { Name = "Combi boiler" },
            Household(), _context, Audit(), TestPrincipal.Create(Ben));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<MaintenanceAssetDto>>());
            Assert.That(asset.Name, Is.EqualTo("Combi boiler"));
        });
    }

    [Test]
    public async Task CreatingAnAsset_NeedsManageMaintenance()
    {
        await SeedAsync();

        var dto = new CreateMaintenanceAssetDto { Name = "Dishwasher", ServiceIntervalDays = 365 };

        var refused = await _endpoint.CreateAssetAsync(UpkeepId, dto,
            Household(), _context, Audit(), TestPrincipal.Create(Anna));

        var allowed = await _endpoint.CreateAssetAsync(UpkeepId, dto,
            Household(), _context, Audit(), TestPrincipal.Create(Ben));

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.InstanceOf<ForbidHttpResult>());
            Assert.That(allowed, Is.InstanceOf<Ok<MaintenanceAssetDto>>());
        });
    }

    /// <summary>Moving the warranty date releases the stamp, or correcting a mistyped year would
    /// silently cost the asset its only warning.</summary>
    [Test]
    public async Task ChangingTheWarrantyDate_LetsItWarnAgain()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(warrantyUntil: DateTimeOffset.UtcNow.AddDays(21));
        asset.WarrantyNotifiedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync();

        await _endpoint.UpdateAssetAsync(asset.Id,
            new UpdateMaintenanceAssetDto { WarrantyUntil = DateTimeOffset.UtcNow.AddDays(25) },
            Household(), _context, Audit(), TestPrincipal.Create(Ben));

        Assert.That(asset.WarrantyNotifiedAt, Is.Null);
    }

    // ══════════════════════════════════════════════════════════════════════════ Recording a
    // service ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task RecordingAService_WritesTheLogAndMovesTheDueDate()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(intervalDays: 365, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-400));

        var performedAt = DateTimeOffset.UtcNow.AddDays(-2);

        var result = await _endpoint.RecordServicedAsync(asset.Id,
            new RecordServiceDto { PerformedAt = performedAt, Title = "Annual service", VendorName = "The engineer" },
            Household(), _context, Audit(), TestPrincipal.Create(Anna));

        var records = await _context.Set<MaintenanceRecord>().ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Value(result), Is.Not.Null);
            Assert.That(records, Has.Count.EqualTo(1));
            Assert.That(records[0].AssetId, Is.EqualTo(asset.Id));
            Assert.That(records[0].PerformedByUserId, Is.EqualTo(Anna));
            Assert.That(asset.LastServicedAt, Is.EqualTo(performedAt));
            Assert.That(asset.NextServiceAt, Is.EqualTo(performedAt.AddDays(365)),
                "counted from the engineer's visit, not from the date it was missed");
        });
    }

    [Test]
    public async Task RecordingAService_ReleasesTheDueStamp()
    {
        await SeedAsync();
        var asset = await AddAssetAsync(intervalDays: 365, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-400));
        await Sweep().SweepAsync();

        Assert.That(asset.ServiceNotifiedAt, Is.Not.Null, "sanity: it was announced");

        await _endpoint.RecordServicedAsync(asset.Id, new RecordServiceDto(),
            Household(), _context, Audit(), TestPrincipal.Create(Anna));

        Assert.That(asset.ServiceNotifiedAt, Is.Null);
    }

    [Test]
    public async Task RecordingAService_RejectsAnExpenseFromAnotherGuild()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();

        var result = await _endpoint.RecordServicedAsync(asset.Id,
            new RecordServiceDto { ExpenseId = "expn-nope" },
            Household(), _context, Audit(), TestPrincipal.Create(Anna));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>(),
            "a dangling pointer into the ledger renders as a link to money nobody can find");
    }

    // ══════════════════════════════════════════════════════════════════════════ The record log and
    // its paging ══════════════════════════════════════════════════════════════════════════

    private Task<IResult> ListRecordsAsync(string? assetId = null, int? limit = null, string? cursor = null) =>
        _endpoint.ListRecordsAsync(UpkeepId, assetId, limit, cursor,
            Household(), _context, TestPrincipal.Create(Anna));

    [Test]
    public async Task Records_PageNewestFirstAndHandBackACursor()
    {
        await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++) await AddRecordAsync($"Job {i}", now.AddDays(-i));

        var first = Value(await ListRecordsAsync(limit: 2));
        var firstItems = Read<List<MaintenanceRecordDto>>(first, "Items");
        var cursor = Read<string?>(first, "NextCursor");

        var second = Value(await ListRecordsAsync(limit: 2, cursor: cursor));
        var secondItems = Read<List<MaintenanceRecordDto>>(second, "Items");

        Assert.Multiple(() =>
        {
            Assert.That(firstItems.Select(r => r.Title), Is.EqualTo(new[] { "Job 0", "Job 1" }));
            Assert.That(cursor, Is.Not.Null);
            Assert.That(secondItems.Select(r => r.Title), Is.EqualTo(new[] { "Job 2", "Job 3" }));
        });
    }

    [Test]
    public async Task Records_TheLastPageCarriesNoCursor()
    {
        await SeedAsync();
        await AddRecordAsync("Only job", DateTimeOffset.UtcNow);

        var page = Value(await ListRecordsAsync(limit: 10));

        Assert.That(Read<string?>(page, "NextCursor"), Is.Null);
    }

    /// <summary>A 400 rather than a silent first page: a client that has corrupted its cursor is
    /// paging in a circle, and quietly restarting the list is how that becomes an infinite scroll
    /// nobody can reproduce.</summary>
    [Test]
    public async Task Records_AMalformedCursorIsRejected()
    {
        await SeedAsync();
        await AddRecordAsync("Job", DateTimeOffset.UtcNow);

        Assert.Multiple(async () =>
        {
            Assert.That(await ListRecordsAsync(cursor: "nonsense"), Is.InstanceOf<BadRequest<string>>());
            Assert.That(await ListRecordsAsync(cursor: "not-a-date|mrec_1"), Is.InstanceOf<BadRequest<string>>());
        });
    }

    [Test]
    public async Task Records_CanBeFilteredToOneAsset()
    {
        await SeedAsync();
        var asset = await AddAssetAsync();
        await AddRecordAsync("Boiler service", DateTimeOffset.UtcNow, assetId: asset.Id);
        await AddRecordAsync("Blocked drain", DateTimeOffset.UtcNow.AddDays(-1));

        var page = Value(await ListRecordsAsync(assetId: asset.Id));

        Assert.That(Read<List<MaintenanceRecordDto>>(page, "Items").Select(r => r.Title),
            Is.EqualTo(new[] { "Boiler service" }));
    }

    [Test]
    public async Task Records_ARepairWithNoAssetIsStillLoggable()
    {
        await SeedAsync();

        var result = await _endpoint.CreateRecordAsync(UpkeepId,
            new CreateMaintenanceRecordDto { Title = "Plumber out for a blocked drain" },
            Household(), _context, Audit(), TestPrincipal.Create(Anna));

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.InstanceOf<Ok<MaintenanceRecordDto>>());
            Assert.That(_context.Set<MaintenanceRecord>().Single().AssetId, Is.Null,
                "requiring a 'drain' asset first is the friction that stops it being logged at all");
        });
    }

    [Test]
    public async Task Records_RejectAnAssetFromAnotherChannel()
    {
        await SeedAsync();
        var cellarAsset = await AddAssetAsync(channelId: CellarId, name: "Freezer");

        var result = await _endpoint.CreateRecordAsync(UpkeepId,
            new CreateMaintenanceRecordDto { Title = "Defrosted", AssetId = cellarAsset.Id },
            Household(), _context, Audit(), TestPrincipal.Create(Anna));

        Assert.That(result, Is.InstanceOf<BadRequest<string>>());
    }

    [Test]
    public async Task EditingSomebodyElsesRecord_NeedsManageMaintenance()
    {
        await SeedAsync();
        var record = await AddRecordAsync("Job", DateTimeOffset.UtcNow);   // performed by Anna

        var refused = await _endpoint.UpdateRecordAsync(record.Id,
            new UpdateMaintenanceRecordDto { Title = "Rewritten" },
            Household(), _context, TestPrincipal.Create(OwnerId + "-stranger"));

        var byOwnAuthor = await _endpoint.UpdateRecordAsync(record.Id,
            new UpdateMaintenanceRecordDto { Title = "Corrected" },
            Household(), _context, TestPrincipal.Create(Anna));

        Assert.Multiple(() =>
        {
            Assert.That(refused, Is.InstanceOf<ForbidHttpResult>(),
                "a non-member cannot rewrite what somebody said happened to the house");
            Assert.That(byOwnAuthor, Is.InstanceOf<Ok<MaintenanceRecordDto>>(),
                "but fixing your own typo needs only LogMaintenance");
        });
    }

    // ══════════════════════════════════════════════════════════════════════════ The attention
    // board ══════════════════════════════════════════════════════════════════════════

    private Task<IResult> AttentionAsync(string userId) =>
        _endpoint.AttentionAsync(GuildId, _permissions, _context, TestPrincipal.Create(userId));

    [Test]
    public async Task Attention_ListsWhatIsAskingForAHuman()
    {
        await SeedAsync();
        await AddAssetAsync(name: "Washing machine", status: AssetStatus.Broken);
        await AddAssetAsync(name: "Boiler", intervalDays: 30, lastServicedAt: DateTimeOffset.UtcNow.AddDays(-40));
        await AddAssetAsync(name: "Dryer", warrantyUntil: DateTimeOffset.UtcNow.AddDays(10));
        await AddAssetAsync(name: "Kettle");

        var board = (IEnumerable<MaintenanceAttentionDto>)Value(await AttentionAsync(Anna))!;

        Assert.Multiple(() =>
        {
            Assert.That(board.Select(b => b.Asset.Name),
                Is.EquivalentTo(new[] { "Boiler", "Dryer", "Washing machine" }));
            Assert.That(board.Single(b => b.Asset.Name == "Washing machine").Reasons, Does.Contain("broken"));
            Assert.That(board.Single(b => b.Asset.Name == "Boiler").Reasons, Does.Contain("service_overdue"));
            Assert.That(board.Single(b => b.Asset.Name == "Dryer").Reasons, Does.Contain("warranty_expiring"));
        });
    }

    [Test]
    public async Task Attention_LeavesOutSomethingTakenOutOfUseDeliberately()
    {
        await SeedAsync();
        await AddAssetAsync(name: "Old dryer", status: AssetStatus.OutOfService);

        var board = (IEnumerable<MaintenanceAttentionDto>)Value(await AttentionAsync(Anna))!;

        Assert.That(board, Is.Empty);
    }

    /// <summary>The board spans every maintenance channel in the guild, so it has to be filtered by
    /// ViewChannel per channel - otherwise a guest with access to the kitchen learns what is in the
    /// cellar.</summary>
    [Test]
    public async Task Attention_HidesChannelsTheCallerCannotSee()
    {
        await SeedAsync();
        await RestrictCellarToFlatmatesAsync();
        await AddAssetAsync(channelId: UpkeepId, name: "Washing machine", status: AssetStatus.Broken);
        await AddAssetAsync(channelId: CellarId, name: "Freezer", status: AssetStatus.Broken);

        var forAnna = (IEnumerable<MaintenanceAttentionDto>)Value(await AttentionAsync(Anna))!;
        var forBen = (IEnumerable<MaintenanceAttentionDto>)Value(await AttentionAsync(Ben))!;

        Assert.Multiple(() =>
        {
            Assert.That(forAnna.Select(b => b.Asset.Name), Is.EqualTo(new[] { "Washing machine" }));
            Assert.That(forBen.Select(b => b.Asset.Name),
                Is.EquivalentTo(new[] { "Freezer", "Washing machine" }));
        });
    }

    [Test]
    public async Task Attention_WithTheModuleOff_IsForbidden()
    {
        await SeedAsync(GuildFeaturePresets.Household & ~GuildFeatures.Maintenance);
        await AddAssetAsync(status: AssetStatus.Broken);

        Assert.That(await AttentionAsync(Anna), Is.InstanceOf<ForbidHttpResult>());
    }

    [Test]
    public async Task Attention_ForSomebodyWhoIsNotAMember_IsForbidden()
    {
        await SeedAsync();
        await AddAssetAsync(status: AssetStatus.Broken);

        Assert.That(await AttentionAsync("stranger"), Is.InstanceOf<ForbidHttpResult>());
    }
}
