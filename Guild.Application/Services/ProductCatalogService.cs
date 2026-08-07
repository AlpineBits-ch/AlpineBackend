using AppEnvironment;
using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Guild.Application.Services;

/// <summary>
/// Resolves a barcode for a scan: from the local catalog first, from the live source second, and
/// from a miss row and a request to type a name when neither can answer.
/// </summary>
public class ProductCatalogService(MicroserviceContext ctx, ProductCatalogLookupService lookups)
{
    /// <summary>A catalog row plus the name that was actually chosen from it, so the caller does not
    /// have to redo the language selection to know what it got.</summary>
    public sealed record Match(ProductCatalogEntry Entry, string Name, string Language);

    /// <summary>Resolves a barcode for a scan, or records that it could not be resolved.</summary>
    public async Task<Match?> ResolveForScanAsync(
        string barcode, IReadOnlyList<string>? languages, CancellationToken ct = default)
    {
        // Tracked rather than AsNoTracking, for the rare row that exists with no name in any
        // language: the live lookup below fills that row in place.
        var entry = await ctx.ProductCatalogEntries
            .FirstOrDefaultAsync(e => e.Barcode == barcode, ct);

        if (entry?.NameFor(languages) is { } name)
            return new Match(entry, name.Text, name.Language);

        var miss = await StageMissAsync(barcode, ct);

        return await FillFromSourceAsync(barcode, entry, miss, languages, ct);
    }

    /// <summary>
    /// Asks the live source, inline, and writes what comes back into the catalog.
    /// </summary>
    private async Task<Match?> FillFromSourceAsync(
        string barcode, ProductCatalogEntry? existing, ProductCatalogMiss miss,
        IReadOnlyList<string>? languages, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (!miss.MayQuery(now)) return null;

        var outcome = await lookups.LookupAsync(barcode, Env.ProductCatalog.InlineTimeout, ct: ct);

        switch (outcome.Kind)
        {
            case ProductCatalogLookupService.LookupKind.Found:
                var fetched = ProductCatalogLookupService.BuildEntry(
                    barcode, outcome.Product!, miss.Source, now);

                // The source has the product and cannot name it in any language.
                if (fetched is null)
                {
                    miss.RecordAbsent(now);
                    return null;
                }

                // Overwritten in place when a nameless row was already there, added otherwise.
                var stored = existing ?? fetched;
                if (existing is not null) Overwrite(existing, fetched);
                else ctx.ProductCatalogEntries.Add(fetched);

                // Removed rather than settled: the question it existed to ask has been answered,
                // and leaving it would have the sweep asking about a product we now hold.
                ctx.ProductCatalogMisses.Remove(miss);

                return stored.NameFor(languages) is { } name
                    ? new Match(stored, name.Text, name.Language)
                    : null;

            case ProductCatalogLookupService.LookupKind.Absent:
                miss.RecordAbsent(now);
                return null;

            case ProductCatalogLookupService.LookupKind.Unreachable:
                miss.RecordUnreachable(now);
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Copies a freshly fetched row onto the one already stored, field for field.
    /// </summary>
    private static void Overwrite(ProductCatalogEntry stored, ProductCatalogEntry fetched)
    {
        stored.NameDe = fetched.NameDe;
        stored.NameFr = fetched.NameFr;
        stored.NameIt = fetched.NameIt;
        stored.NameEn = fetched.NameEn;
        stored.Brand = fetched.Brand;
        stored.Quantity = fetched.Quantity;
        stored.QuantityUnit = fetched.QuantityUnit;
        stored.Source = fetched.Source;
        stored.SourceVersion = fetched.SourceVersion;
        stored.ImportedAt = fetched.ImportedAt;
    }

    /// <summary>Returns this barcode's miss row, adding one if it has none.</summary>
    private async Task<ProductCatalogMiss> StageMissAsync(string barcode, CancellationToken ct)
    {
        // Local before the database: two scans of the same unknown code inside one request would
        // otherwise both see "no row" and both add one, which the primary key then rejects at
        // commit and takes the scan down with it.
        if (ctx.ProductCatalogMisses.Local.FirstOrDefault(m => m.Barcode == barcode) is { } staged)
            return staged;

        // Tracked, unlike the catalog read above, because the outcome of a live lookup is written
        // straight back onto this row.
        if (await ctx.ProductCatalogMisses.FirstOrDefaultAsync(m => m.Barcode == barcode, ct) is { } known)
            return known;

        var miss = ProductCatalogMiss.Create(
            barcode, ProductCatalogSources.OpenFoodFacts, DateTimeOffset.UtcNow);

        ctx.ProductCatalogMisses.Add(miss);
        return miss;
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
