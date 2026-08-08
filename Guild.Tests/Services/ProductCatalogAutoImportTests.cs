using System.Text;
using Guild.Application.Services;
using Guild.Domain.Entity;

namespace Guild.Tests.Services;

/// <summary>The filter and projection the automatic import applies to a published export.</summary>
[TestFixture]
public class ProductCatalogAutoImportTests
{
    private static readonly HashSet<string> Markets =
        new(["en:switzerland", "en:germany", "en:austria", "en:france"], StringComparer.OrdinalIgnoreCase);

    private static async Task<List<ProductCatalogImportService.Line>> ReadAsync(
        params string[] lines)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)));

        var read = new List<ProductCatalogImportService.Line>();

        await foreach (var line in ProductCatalogAutoImportService.ReadExportAsync(
                           stream, Markets, CancellationToken.None))
            read.Add(line);

        return read;
    }

    [Test]
    public async Task Read_KeepsAProductSoldInOurMarkets()
    {
        var lines = await ReadAsync(
            """
            {"code":"3600523351893","product_name_de":"Shampoo","product_name_fr":"Shampooing",
             "brands":"L'Oreal","product_quantity":250,"product_quantity_unit":"ml",
             "countries_tags":["en:france","en:switzerland"]}
            """.ReplaceLineEndings(""));

        var line = lines.Single();

        Assert.Multiple(() =>
        {
            Assert.That(line.Barcode, Is.EqualTo("3600523351893"));
            Assert.That(line.NameDe, Is.EqualTo("Shampoo"));
            Assert.That(line.NameFr, Is.EqualTo("Shampooing"));
            Assert.That(line.Brand, Is.EqualTo("L'Oreal"));
            Assert.That(line.Quantity, Is.EqualTo(250m));
            Assert.That(line.QuantityUnit, Is.EqualTo("ml"));
        });
    }

    /// <summary>The filter is most of the point: 113,261 products across the two databases, of which
    /// 46,110 concern our markets. Keeping the rest would be rows nobody here can ever scan.</summary>
    [Test]
    public async Task Read_DropsAProductSoldNowhereWeServe()
    {
        var lines = await ReadAsync(
            """{"code":"1","product_name_en":"Vegemite","countries_tags":["en:australia"]}""");

        Assert.That(lines, Is.Empty);
    }

    [Test]
    public async Task Read_DropsARowWithNoCountriesAtAll()
    {
        var lines = await ReadAsync("""{"code":"1","product_name_en":"Mystery"}""");

        Assert.That(lines, Is.Empty);
    }

    /// <summary>Mirrors the live lookup: a product with only an unlocalised name is still better
    /// than a blank row, and English is where a reader is least likely to assume it authoritative.
    /// Worth about six points of coverage on these two databases.</summary>
    [Test]
    public async Task Read_FallsBackToTheUnlocalisedName()
    {
        var lines = await ReadAsync(
            """{"code":"1","product_name":"Spülmittel","countries_tags":["en:germany"]}""");

        Assert.That(lines.Single().NameEn, Is.EqualTo("Spülmittel"));
    }

    [Test]
    public async Task Read_PrefersARealEnglishNameOverTheUnlocalisedOne()
    {
        var lines = await ReadAsync(
            """
            {"code":"1","product_name":"Spülmittel","product_name_en":"Washing-up liquid",
             "countries_tags":["en:germany"]}
            """.ReplaceLineEndings(""));

        Assert.That(lines.Single().NameEn, Is.EqualTo("Washing-up liquid"));
    }

    /// <summary>The source files this inconsistently and both spellings occur in one file. Typing it
    /// as either makes the other a parse error, which would silently drop every row that used it.</summary>
    [Test]
    public async Task Read_AcceptsAQuantityAsBothANumberAndAString()
    {
        var lines = await ReadAsync(
            """{"code":"1","product_name_de":"A","product_quantity":380,"countries_tags":["en:germany"]}""",
            """{"code":"2","product_name_de":"B","product_quantity":"500","countries_tags":["en:germany"]}""",
            """{"code":"3","product_name_de":"C","product_quantity":"","countries_tags":["en:germany"]}""");

        Assert.Multiple(() =>
        {
            Assert.That(lines[0].Quantity, Is.EqualTo(380m));
            Assert.That(lines[1].Quantity, Is.EqualTo(500m));
            Assert.That(lines[2].Quantity, Is.Null, "an empty string is not a quantity");
        });
    }

    /// <summary>One unreadable row in a hundred thousand must not abandon the rest.</summary>
    [Test]
    public async Task Read_SkipsAMalformedLineAndKeepsGoing()
    {
        var lines = await ReadAsync(
            """{"code":"1","product_name_de":"Before","countries_tags":["en:germany"]}""",
            """{"code":"2","product_name_de":"Broken",""",
            "",
            """{"code":"3","product_name_de":"After","countries_tags":["en:germany"]}""");

        Assert.That(lines.Select(l => l.Barcode), Is.EqualTo(new[] { "1", "3" }));
    }

    [Test]
    public async Task Read_DropsARowWithNoBarcode()
    {
        var lines = await ReadAsync(
            """{"product_name_de":"Nameless","countries_tags":["en:germany"]}""");

        Assert.That(lines, Is.Empty);
    }

    // ── What the service will and will not fetch ─────────────────────────────

    /// <summary>The food database is deliberately absent.</summary>
    [Test]
    public void ImportableSources_CoverTheSiblingsAndNotFood()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProductCatalogAutoImportService.CanImport(
                ProductCatalogSources.OpenBeautyFacts), Is.True);

            Assert.That(ProductCatalogAutoImportService.CanImport(
                ProductCatalogSources.OpenProductsFacts), Is.True);

            Assert.That(ProductCatalogAutoImportService.CanImport(
                ProductCatalogSources.OpenFoodFacts), Is.False,
                "the 11.77 GB food export is the offline script's job, not this service's");

            Assert.That(ProductCatalogAutoImportService.CanImport("nonsense"), Is.False);
            Assert.That(ProductCatalogAutoImportService.CanImport(null), Is.False);
        });
    }
}
