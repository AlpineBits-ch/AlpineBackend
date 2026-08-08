using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// Keyword search over the product catalog, against the provider that actually ships.
/// </summary>
[TestFixture]
public class ProductCatalogSearchTests
{
    private MicroserviceContext _context = null!;
    private ProductCatalogSearchService _search = null!;

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.EnsureStartedAsync();
        await PostgresTestDatabase.ResetAsync();

        _context = new PostgresGuildContext();
        _search = new ProductCatalogSearchService(_context);
    }

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    private async Task SeedAsync(params ProductCatalogEntry[] entries)
    {
        _context.ProductCatalogEntries.AddRange(entries);
        await _context.SaveChangesAsync();
    }

    private static ProductCatalogEntry Entry(
        string barcode, string? de = null, string? fr = null, string? en = null,
        string? brand = null, string source = ProductCatalogSources.OpenFoodFacts) => new()
    {
        Barcode = barcode, NameDe = de, NameFr = fr, NameEn = en, Brand = brand,
        Source = source, SourceVersion = "test", ImportedAt = DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task Search_MatchesOnTheGermanName()
    {
        await SeedAsync(
            Entry("1000000000001", de: "Vollmilch Bio"),
            Entry("1000000000002", de: "Cornflakes"));

        var results = await _search.SearchAsync("milch", ["de"], null, null);

        Assert.Multiple(() =>
        {
            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(results.Hits.Single().Entry.Barcode, Is.EqualTo("1000000000001"));
            Assert.That(results.Hits.Single().Name, Is.EqualTo("Vollmilch Bio"));
        });
    }

    /// <summary>The property the whole feature rests on: one query, every database.</summary>
    [Test]
    public async Task Search_CoversEveryDatabaseAtOnce()
    {
        await SeedAsync(
            Entry("1000000000001", de: "Reinigungsmittel Zitrone",
                source: ProductCatalogSources.OpenProductsFacts),
            Entry("1000000000002", de: "Zitronen Shampoo",
                source: ProductCatalogSources.OpenBeautyFacts),
            Entry("1000000000003", de: "Zitronensaft",
                source: ProductCatalogSources.OpenFoodFacts));

        var results = await _search.SearchAsync("zitron", ["de"], null, null);

        Assert.That(results.Hits.Select(h => h.Entry.Source), Is.EquivalentTo(new[]
        {
            ProductCatalogSources.OpenProductsFacts,
            ProductCatalogSources.OpenBeautyFacts,
            ProductCatalogSources.OpenFoodFacts,
        }));
    }

    [Test]
    public async Task Search_IsCaseInsensitiveAndMatchesInsideAWord()
    {
        await SeedAsync(Entry("1000000000001", de: "Handseife Lavendel"));

        var results = await _search.SearchAsync("SEIFE", ["de"], null, null);

        Assert.That(results.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Search_MatchesOnTheBrand()
    {
        await SeedAsync(Entry("1000000000001", de: "Vollmilch", brand: "M-Budget"));

        var results = await _search.SearchAsync("budget", ["de"], null, null);

        Assert.That(results.Count, Is.EqualTo(1));
    }

    /// <summary>A product matched in one language is shown in whichever language the caller can
    /// read. Hiding it because the match was on a column the caller did not ask for would be the
    /// wrong outcome for a trilingual market.</summary>
    [Test]
    public async Task Search_MatchingOneLanguage_ReturnsTheNameInAnother()
    {
        await SeedAsync(Entry("1000000000001", de: "Zahnpasta Minze", fr: "Dentifrice Menthe"));

        var results = await _search.SearchAsync("zahnpasta", ["fr"], null, null);

        var hit = results.Hits.Single();

        Assert.Multiple(() =>
        {
            Assert.That(hit.Name, Is.EqualTo("Dentifrice Menthe"));
            Assert.That(hit.Language, Is.EqualTo("fr"), "and it says which language it gave back");
        });
    }

    [Test]
    public async Task Search_TooShortAQuery_ReturnsNothingRatherThanEverything()
    {
        await SeedAsync(Entry("1000000000001", de: "Milch"));

        foreach (var term in new[] { "", "  ", "mi" })
        {
            var results = await _search.SearchAsync(term, ["de"], null, null);
            Assert.That(results.Count, Is.Zero, $"'{term}' must not match the whole catalog");
        }
    }

    /// <summary>A wildcard is a LIKE metacharacter, so an unescaped one turns any search into a
    /// full scan that returns the entire catalog. Escaped rather than stripped: somebody searching
    /// "50%" means the literal string.</summary>
    [Test]
    public async Task Search_WildcardsAreTakenLiterally()
    {
        await SeedAsync(
            Entry("1000000000001", de: "Rahm 35% Fett"),
            Entry("1000000000002", de: "Cornflakes"));

        var everything = await _search.SearchAsync("%%%", ["de"], null, null);
        var literal = await _search.SearchAsync("35%", ["de"], null, null);

        Assert.Multiple(() =>
        {
            Assert.That(everything.Count, Is.Zero, "a wildcard must not return the whole catalog");
            Assert.That(literal.Count, Is.EqualTo(1), "but a literal percent sign must still match");
        });
    }

    [Test]
    public async Task Search_PagesStablyAndClampsTheLimit()
    {
        await SeedAsync(Enumerable.Range(1, 10)
            .Select(i => Entry($"200000000000{i}", de: $"Seife {i}")).ToArray());

        var first = await _search.SearchAsync("seife", ["de"], limit: 4, offset: 0);
        var second = await _search.SearchAsync("seife", ["de"], limit: 4, offset: 4);
        var clamped = await _search.SearchAsync("seife", ["de"], limit: 9999, offset: 0);

        Assert.Multiple(() =>
        {
            Assert.That(first.Hits, Has.Count.EqualTo(4));
            Assert.That(second.Hits, Has.Count.EqualTo(4));

            // Stable ordering is what makes paging correct; overlapping pages would repeat rows.
            Assert.That(first.Hits.Select(h => h.Entry.Barcode).Intersect(
                second.Hits.Select(h => h.Entry.Barcode)), Is.Empty);

            Assert.That(clamped.Limit, Is.EqualTo(ProductCatalogSearchService.MaxLimit));
            Assert.That(first.Count, Is.EqualTo(10), "the count is of all matches, not of the page");
        });
    }

    [Test]
    public async Task Search_NoMatch_IsAnEmptyResultRatherThanAnError()
    {
        await SeedAsync(Entry("1000000000001", de: "Milch"));

        var results = await _search.SearchAsync("zzzqqqxyz", ["de"], null, null);

        Assert.Multiple(() =>
        {
            Assert.That(results.Hits, Is.Empty);
            Assert.That(results.Count, Is.Zero);
            Assert.That(results.CountIsLowerBound, Is.False);
        });
    }

    /// <summary>A row with no name in any language cannot be shown, so it must not occupy a slot in
    /// the page. It can still match, because the brand is searched too.</summary>
    [Test]
    public async Task Search_NamelessRow_IsNotReturned()
    {
        await SeedAsync(Entry("1000000000001", brand: "Persil"));

        var results = await _search.SearchAsync("persil", ["de"], null, null);

        Assert.That(results.Hits, Is.Empty);
    }
}

/// <summary>The search query compiled against the real Npgsql provider, with no database.</summary>
[TestFixture]
public class ProductCatalogSearchQueryTranslationTests
{
    private PostgresGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public void Match_TranslatesToSql()
    {
        var sql = ProductCatalogSearchService.Match(_context, "shampoo").ToQueryString();

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("SELECT"));

            // ILIKE is what the trigram index answers.
            Assert.That(sql.ToUpperInvariant(), Does.Contain("ILIKE"));
        });
    }
}
