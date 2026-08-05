using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Infrastructure.Seed;
using Social.Tests.Helpers;

namespace Social.Tests.Seed;

/// <summary>Reconciliation behaviour of the game-catalog bootstrap seed.</summary>
[TestFixture]
public class GameCatalogSeederTests
{
    private TestSocialContext _context = null!;
    private GameCatalogSeeder _seeder = null!;

    [SetUp]
    public void SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _seeder = new GameCatalogSeeder(_context, NullLogger<GameCatalogSeeder>.Instance);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private static GameCatalogSeed SeedOf(string version, params SeedApp[] apps) =>
        new() { Version = version, AppCount = apps.Length, Apps = apps };

    private static SeedApp App(string id, string name, params SeedExecutable[] executables) =>
        new() { DiscordApplicationId = id, Name = name, Executables = executables.Length > 0 ? executables : null };

    private static SeedExecutable Exe(string name, string os = "win32", bool launcher = false, bool negated = false) =>
        new() { Name = name, Os = os, IsLauncher = launcher, Negated = negated };

    // ── Normal ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ApplyAsync_EmptyCatalog_InsertsApplicationsAndExecutables()
    {
        await _seeder.ApplyAsync(SeedOf("v1",
            App("356875221078245376", "Overwatch", Exe("overwatch.exe")),
            App("1", "Dota 2", Exe("osx64/dota2.app", os: "darwin"))), default);

        var apps = await _context.GameApplications.Include(g => g.Executables).ToListAsync();

        Assert.That(apps, Has.Count.EqualTo(2));
        Assert.That(apps.Select(a => a.Name), Is.EquivalentTo(new[] { "Overwatch", "Dota 2" }));
        Assert.That(apps.All(a => a.Source == GameCatalogSource.Seeded), Is.True);
        Assert.That(apps.Sum(a => a.Executables.Count), Is.EqualTo(2));
    }

    [Test]
    public async Task ApplyAsync_RecordsSeedVersion_SoRestartsAreANoOp()
    {
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "A", Exe("a.exe"))), default);

        var state = await _context.GameCatalogStates.SingleAsync();

        Assert.That(state.Id, Is.EqualTo(GameCatalogState.SingletonId));
        Assert.That(state.SeedVersion, Is.EqualTo("v1"));
        Assert.That(await _seeder.IsCurrentAsync("v1", default), Is.True);
        Assert.That(await _seeder.IsCurrentAsync("v2", default), Is.False);
    }

    [Test]
    public async Task ApplyAsync_ProjectsSteamAppIdFromStores()
    {
        var withSteam = new SeedApp
        {
            DiscordApplicationId = "1",
            Name = "Overwatch",
            Stores = new Dictionary<string, string[]> { ["steam"] = ["2357570"], ["xbox"] = ["C2XK6T9K04V6"] },
        };

        await _seeder.ApplyAsync(SeedOf("v1", withSteam, App("2", "No Store")), default);

        var apps = await _context.GameApplications.ToDictionaryAsync(a => a.DiscordApplicationId!);

        Assert.That(apps["1"].SteamAppId, Is.EqualTo("2357570"));
        Assert.That(apps["2"].SteamAppId, Is.Null, "an app with no store SKUs must not invent one");
    }

    // ── Matching data the rules exist for ───────────────────────────────────────────────────

    [Test]
    public async Task ApplyAsync_PathQualifiedRule_DerivesBasenameButKeepsFullRule()
    {
        // 83% of real rules look like this.
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "World of Warcraft", Exe("_retail_/wow.exe"))), default);

        var exe = await _context.GameExecutables.SingleAsync();

        Assert.That(exe.Name, Is.EqualTo("_retail_/wow.exe"));
        Assert.That(exe.Basename, Is.EqualTo("wow.exe"));
    }

    [Test]
    public async Task ApplyAsync_PlainRule_UsesWholeNameAsBasename()
    {
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Overwatch", Exe("overwatch.exe"))), default);

        var exe = await _context.GameExecutables.SingleAsync();

        Assert.That(exe.Basename, Is.EqualTo("overwatch.exe"));
        Assert.That(exe.Name, Is.EqualTo("overwatch.exe"));
    }

    [Test]
    public async Task ApplyAsync_PreservesLauncherAndNegationFlags()
    {
        // Both flags are disambiguators: without IsLauncher we announce the store front-end as the
        // game, and without IsNegated the 34-way `hl2.exe` collision has no resolution at all.
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Garry's Mod",
            Exe("win64/gmod.exe"),
            Exe("hl2.exe", negated: true),
            Exe("launcher.exe", launcher: true))), default);

        var rules = await _context.GameExecutables.ToDictionaryAsync(e => e.Name);

        Assert.That(rules["win64/gmod.exe"].IsNegated, Is.False);
        Assert.That(rules["win64/gmod.exe"].IsLauncher, Is.False);
        Assert.That(rules["hl2.exe"].IsNegated, Is.True);
        Assert.That(rules["launcher.exe"].IsLauncher, Is.True);
    }

    [Test]
    public async Task ApplyAsync_AppWithNoExecutables_IsStillInserted()
    {
        // 13,413 of the real entries have no executables.
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Some RPC-only Game")), default);

        Assert.That(await _context.GameApplications.CountAsync(), Is.EqualTo(1));
        Assert.That(await _context.GameExecutables.CountAsync(), Is.Zero);
    }

    // ── Idempotency and divergence ──────────────────────────────────────────────────────────

    [Test]
    public async Task ApplyAsync_RunTwiceWithSameSeed_DoesNotDuplicate()
    {
        var seed = SeedOf("v1", App("1", "Overwatch", Exe("overwatch.exe")));

        await _seeder.ApplyAsync(seed, default);
        await _seeder.ApplyAsync(seed, default);

        Assert.That(await _context.GameApplications.CountAsync(), Is.EqualTo(1));
        Assert.That(await _context.GameExecutables.CountAsync(), Is.EqualTo(1));
        Assert.That(await _context.GameCatalogStates.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task ApplyAsync_UpdatedSeed_ReplacesExecutablesWholesale()
    {
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Game", Exe("old.exe"), Exe("shared.exe"))), default);
        await _seeder.ApplyAsync(SeedOf("v2", App("1", "Game Renamed", Exe("shared.exe"), Exe("new.exe"))), default);

        var app = await _context.GameApplications.Include(a => a.Executables).SingleAsync();

        Assert.That(app.Name, Is.EqualTo("Game Renamed"));
        Assert.That(app.Executables.Select(e => e.Name), Is.EquivalentTo(new[] { "shared.exe", "new.exe" }),
            "a rule removed upstream must disappear, which a diff-by-name would keep forever");
        Assert.That(await _context.GameExecutables.CountAsync(), Is.EqualTo(2), "orphaned rules must not linger");
    }

    [Test]
    public async Task ApplyAsync_SeededRowAbsentFromNewSeed_IsRemoved()
    {
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Kept", Exe("a.exe")), App("2", "Dropped", Exe("b.exe"))), default);
        await _seeder.ApplyAsync(SeedOf("v2", App("1", "Kept", Exe("a.exe"))), default);

        var remaining = await _context.GameApplications.ToListAsync();

        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].Name, Is.EqualTo("Kept"));
    }

    [Test]
    public async Task ApplyAsync_CommunityRow_IsNeitherOverwrittenNorRemoved()
    {
        // This is the property that makes the catalog ours rather than a mirror of its bootstrap.
        _context.GameApplications.Add(new GameApplication
        {
            Id = GameApplication.GenerateId(),
            DiscordApplicationId = "1",
            Name = "Community Corrected Name",
            Source = GameCatalogSource.Community,
        });
        await _context.SaveChangesAsync();

        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Bootstrap Name", Exe("a.exe"))), default);

        var app = await _context.GameApplications.SingleAsync();
        Assert.That(app.Name, Is.EqualTo("Community Corrected Name"));
        Assert.That(app.Source, Is.EqualTo(GameCatalogSource.Community));

        // ...and a later seed that drops the entry entirely must still leave it alone.
        await _seeder.ApplyAsync(SeedOf("v2"), default);

        Assert.That(await _context.GameApplications.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task ApplyAsync_ManualRow_IsPreservedLikeCommunity()
    {
        _context.GameApplications.Add(new GameApplication
        {
            Id = GameApplication.GenerateId(),
            DiscordApplicationId = "1",
            Name = "Staff Fixed",
            Source = GameCatalogSource.Manual,
        });
        await _context.SaveChangesAsync();

        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Bootstrap Name", Exe("a.exe"))), default);

        Assert.That((await _context.GameApplications.SingleAsync()).Name, Is.EqualTo("Staff Fixed"));
    }

    // ── Negative / malformed input ──────────────────────────────────────────────────────────

    [Test]
    public async Task ApplyAsync_UnknownOperatingSystem_DropsRuleButKeepsApplication()
    {
        // A rule we cannot evaluate must never become a rule we evaluate wrongly.
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Game", Exe("a.exe", os: "haiku"), Exe("b.exe"))), default);

        var app = await _context.GameApplications.Include(a => a.Executables).SingleAsync();

        Assert.That(app.Executables, Has.Count.EqualTo(1));
        Assert.That(app.Executables.Single().Name, Is.EqualTo("b.exe"));
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task ApplyAsync_BlankApplicationId_IsSkipped(string id)
    {
        await _seeder.ApplyAsync(SeedOf("v1", App(id, "Nameless")), default);

        Assert.That(await _context.GameApplications.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ApplyAsync_BlankName_IsSkipped()
    {
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "  ")), default);

        Assert.That(await _context.GameApplications.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ApplyAsync_BlankExecutableName_IsSkipped()
    {
        await _seeder.ApplyAsync(SeedOf("v1", App("1", "Game", Exe(""), Exe("real.exe"))), default);

        Assert.That(await _context.GameExecutables.CountAsync(), Is.EqualTo(1));
    }

    // ── The shipped artifact itself ─────────────────────────────────────────────────────────

    [Test]
    public void EmbeddedSeed_IsReadableAndPlausible()
    {
        // Guards the packaging as much as the data: an EmbeddedResource that silently stops being
        // embedded fails here rather than at a customer's first launch.
        var seed = GameCatalogSeedReader.Read();

        Assert.That(seed.Version, Is.Not.Empty);
        Assert.That(seed.Apps, Has.Count.GreaterThan(20_000));
        Assert.That(seed.AppCount, Is.EqualTo(seed.Apps.Count));

        var withExecutables = seed.Apps.Where(a => a.Executables is { Length: > 0 }).ToList();
        Assert.That(withExecutables, Has.Count.GreaterThan(10_000));

        var rules = withExecutables.SelectMany(a => a.Executables!).ToList();
        Assert.That(rules.Any(r => r.Name.Contains('/')), Is.True, "path-qualified rules must survive the transform");
        Assert.That(rules.Any(r => r.Negated), Is.True, "negation rules must survive the transform");
        Assert.That(rules.All(r => r.Name == r.Name.ToLowerInvariant()), Is.True, "rules must be normalized to lower case");
        Assert.That(rules.All(r => !r.Name.StartsWith('>')), Is.True, "the negation marker must be parsed, not stored");
        Assert.That(rules.All(r => !r.Name.Contains('\\')), Is.True, "backslashes must be folded to forward slashes");
    }
}
