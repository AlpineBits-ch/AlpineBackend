using Guild.Application.Endpoints;
using Guild.Domain.Entity;
using Guild.Tests.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Services;

/// <summary>
/// Asserts the product catalog's queries compile to SQL against the real Npgsql provider.
/// </summary>
[TestFixture]
public class ProductCatalogQueryTranslationTests
{
    private PostgresGuildContext _context = null!;

    [SetUp]
    public void SetUp() => _context = new PostgresGuildContext();

    [TearDown]
    public async Task TearDown() => await _context.DisposeAsync();

    [Test]
    public void ExportPage_TranslatesWithoutACursor()
    {
        Assert.That(ProductCatalogEndpoint.BuildExportPage(_context, after: null).ToQueryString(),
            Does.Contain("SELECT"));
    }

    [Test]
    public void ExportPage_TranslatesWithACursor()
    {
        // The resumable branch, and the only one with a comparison the provider has to know how to
        // render.
        Assert.That(ProductCatalogEndpoint.BuildExportPage(_context, "7617027080224").ToQueryString(),
            Does.Contain("SELECT"));
    }

    [Test]
    public void Resolve_TranslatesTheBarcodeLookup()
    {
        Assert.That(_context.ProductCatalogEntries.AsNoTracking()
                .Where(e => e.Barcode == "7617027080224")
                .ToQueryString(),
            Does.Contain("SELECT"));
    }

    [Test]
    public void DueMisses_TranslateTheFillerQueue()
    {
        var now = DateTimeOffset.UtcNow;

        // Nullable comparison plus an ordering on the same nullable column: the shape the partial
        // index on retry_after is there to serve.
        Assert.That(_context.ProductCatalogMisses
                .Where(m => m.RetryAfter != null && m.RetryAfter <= now)
                .OrderBy(m => m.RetryAfter)
                .Take(5)
                .ToQueryString(),
            Does.Contain("SELECT"));
    }

    [Test]
    public void ImportBatch_TranslatesTheContainsUpsertProbe()
    {
        var barcodes = new List<string> { "7617027080224", "7617100317001" };

        Assert.That(_context.ProductCatalogEntries
                .Where(e => barcodes.Contains(e.Barcode))
                .ToQueryString(),
            Does.Contain("SELECT"));
    }

    [Test]
    public void CatalogInfo_TranslatesTheVersionRollup()
    {
        // A GroupBy with an aggregate in the OrderBy, which is the one expression on the info
        // endpoint that a provider can refuse.
        Assert.That(_context.ProductCatalogEntries
                .GroupBy(e => e.SourceVersion)
                .OrderByDescending(g => g.Max(e => e.ImportedAt))
                .Select(g => g.Key)
                .Take(20)
                .ToQueryString(),
            Does.Contain("SELECT"));
    }

    [Test]
    public void TheTwoTablesAreMappedApart()
    {
        // Belt and braces on the licence boundary, asserted against the provider that actually
        // names tables: the ODbL derivative and the households' own learned names are different
        // relations, so no accidental join can be written across them without saying so.
        Assert.That(_context.Model.FindEntityType(typeof(ProductCatalogEntry))!.GetTableName(),
            Is.Not.EqualTo(_context.Model.FindEntityType(typeof(PantryBarcode))!.GetTableName()));
    }
}
