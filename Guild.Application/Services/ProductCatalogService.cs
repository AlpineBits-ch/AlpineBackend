using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Guild.Application.Services;

/// <summary>
/// Reads the shared product catalog, and records the barcodes it could not answer.
/// </summary>
public class ProductCatalogService(MicroserviceContext ctx)
{
    /// <summary>A catalog row plus the name that was actually chosen from it, so the caller does not
    /// have to redo the language selection to know what it got.</summary>
    public sealed record Match(ProductCatalogEntry Entry, string Name, string Language);

    /// <summary>Resolves a barcode for a scan, or records that it could not be resolved.</summary>
    public async Task<Match?> ResolveForScanAsync(
        string barcode, IReadOnlyList<string>? languages, CancellationToken ct = default)
    {
        var entry = await ctx.ProductCatalogEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Barcode == barcode, ct);

        if (entry?.NameFor(languages) is { } name)
            return new Match(entry, name.Text, name.Language);

        await StageMissAsync(barcode, ct);
        return null;
    }

    /// <summary>Adds a miss row unless this barcode already has one.</summary>
    private async Task StageMissAsync(string barcode, CancellationToken ct)
    {
        // Local before the database: two scans of the same unknown code inside one request would
        // otherwise both see "no row" and both add one, which the primary key then rejects at
        // commit and takes the scan down with it.
        if (ctx.ProductCatalogMisses.Local.Any(m => m.Barcode == barcode)) return;

        if (await ctx.ProductCatalogMisses.AnyAsync(m => m.Barcode == barcode, ct)) return;

        ctx.ProductCatalogMisses.Add(ProductCatalogMiss.Create(
            barcode, ProductCatalogSources.OpenFoodFacts, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The caller's language preferences, most-wanted first, read from an <c>Accept-Language</c>
    /// header.
    /// </summary>
    public static IReadOnlyList<string> ParseLanguages(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage)) return [];

        if (!StringWithQualityHeaderValue.TryParseList([acceptLanguage], out var parsed))
            return [];

        var ordered = new List<string>(4);

        foreach (var value in parsed.OrderByDescending(v => v.Quality ?? 1d))
        {
            var tag = value.Value.Value;
            if (string.IsNullOrWhiteSpace(tag) || tag == "*") continue;

            // Quality 0 is the header's way of saying "not this one", so honouring it as a
            // preference would invert the caller's meaning.
            if (value.Quality is 0) continue;

            var primary = tag.Split('-')[0].ToLowerInvariant();

            if (!ordered.Contains(primary)) ordered.Add(primary);
        }

        return ordered;
    }

    /// <summary>The wire shape of a catalog hit, attribution included.</summary>
    public static ProductCatalogMatchDto ToDto(Match match) => new()
    {
        Name = match.Name,
        Language = match.Language,
        Brand = match.Entry.Brand,
        Quantity = match.Entry.Quantity,
        QuantityUnit = match.Entry.QuantityUnit,
        Source = match.Entry.Source,
        SourceName = ProductCatalogSources.OpenFoodFactsName,
        SourceUrl = ProductCatalogSources.ProductUrl(match.Entry.Barcode),
        License = ProductCatalogSources.LicenseName,
        LicenseUrl = ProductCatalogSources.LicenseUrl,
        Attribution = ProductCatalogSources.Attribution,
        ImportedAt = match.Entry.ImportedAt,
    };
}
