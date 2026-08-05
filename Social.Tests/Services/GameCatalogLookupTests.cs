using Social.Api.Services;
using Social.Domain.Aggregate;
using Social.Domain.Enums;
using Social.Tests.Helpers;

namespace Social.Tests.Services;

/// <summary>The application-id to canonical-name resolution.</summary>
[TestFixture]
public class GameCatalogLookupTests
{
    private TestSocialContext _context = null!;
    private GameCatalogLookup _lookup = null!;

    [SetUp]
    public async Task SetUp()
    {
        _context = new TestSocialContext(Guid.NewGuid().ToString());
        _lookup = new GameCatalogLookup(_context);

        _context.GameApplications.AddRange(
            new GameApplication
            {
                Id = GameApplication.GenerateId(),
                DiscordApplicationId = "356875221078245376",
                Name = "Overwatch",
                Source = GameCatalogSource.Seeded,
                IsEnabled = true,
            },
            new GameApplication
            {
                Id = GameApplication.GenerateId(),
                DiscordApplicationId = "999999999999999999",
                Name = "Retired Entry",
                Source = GameCatalogSource.Seeded,
                IsEnabled = false,
            });

        await _context.SaveChangesAsync();
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    // ── Normal ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveCanonicalNameAsync_KnownEnabledApplication_ReturnsCatalogName()
    {
        Assert.That(await _lookup.ResolveCanonicalNameAsync("356875221078245376"), Is.EqualTo("Overwatch"));
    }

    [Test]
    public async Task FindByApplicationIdAsync_KnownApplication_ReturnsRow()
    {
        var app = await _lookup.FindByApplicationIdAsync("356875221078245376");

        Assert.That(app, Is.Not.Null);
        Assert.That(app!.Name, Is.EqualTo("Overwatch"));
    }

    // ── Edge ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ResolveCanonicalNameAsync_DisabledApplication_ReturnsNull()
    {
        // Disabling is how a bad entry is switched off without losing the record that it existed;
        // it has to stop the entry being broadcast, or it achieves nothing.
        Assert.That(await _lookup.ResolveCanonicalNameAsync("999999999999999999"), Is.Null);
    }

    [Test]
    public async Task ResolveCanonicalNameAsync_UnknownButWellFormedId_ReturnsNull()
    {
        Assert.That(await _lookup.ResolveCanonicalNameAsync("123456789012345678"), Is.Null);
    }

    // ── Negative / hostile input ────────────────────────────────────────────────────────────

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-a-snowflake")]
    [TestCase("356875221078245376x")]
    [TestCase("35687522107824537 ")]
    [TestCase("-356875221078245376")]
    [TestCase("999999999999999999999999")] // longer than any snowflake
    public void IsWellFormedApplicationId_RejectsAnythingButDecimalDigits(string? candidate)
    {
        Assert.That(GameCatalogLookup.IsWellFormedApplicationId(candidate), Is.False);
    }

    [Test]
    public void IsWellFormedApplicationId_AcceptsASnowflake()
    {
        Assert.That(GameCatalogLookup.IsWellFormedApplicationId("356875221078245376"), Is.True);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-snowflake")]
    public async Task ResolveCanonicalNameAsync_MalformedId_ReturnsNullWithoutQuerying(string? candidate)
    {
        Assert.That(await _lookup.ResolveCanonicalNameAsync(candidate), Is.Null);
    }
}
