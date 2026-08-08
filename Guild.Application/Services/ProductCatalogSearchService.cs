using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Keyword search over the local product catalog.</summary>
public class ProductCatalogSearchService(MicroserviceContext ctx)
{
    /// <summary>Shorter than this and a substring match returns most of the catalog.</summary>
    public const int MinimumQueryLength = 3;

    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    /// <summary>
    /// Above this the count is reported as "at least this many" rather than exactly.
    /// </summary>
    private const int CountCeiling = 500;

    public sealed record Hit(ProductCatalogEntry Entry, string Name, string Language);

    public sealed record Results(
        IReadOnlyList<Hit> Hits, int Count, bool CountIsLowerBound, int Limit, int Offset);

    /// <summary>Products whose name or brand contains <paramref name="query"/>.</summary>
    public async Task<Results> SearchAsync(
        string? query, IReadOnlyList<string>? languages, int? limit, int? offset,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var skip = Math.Max(0, offset ?? 0);

        var term = Normalise(query);

        if (term is null) return new Results([], 0, false, take, skip);

        var matches = Match(ctx, term);

        // Capped rather than a plain Count().
        var counted = await matches.Take(CountCeiling + 1).CountAsync(ct);

        var page = await matches
            .OrderBy(e => e.Barcode)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(ct);

        var hits = page
            .Select(entry => entry.NameFor(languages) is { } name
                ? new Hit(entry, name.Text, name.Language)
                : null)
            .Where(hit => hit is not null)
            .Select(hit => hit!)
            .ToList();

        return new Results(
            hits,
            Math.Min(counted, CountCeiling),
            counted > CountCeiling,
            take,
            skip);
    }

    /// <summary>
    /// The predicate, separated so a translation test can put it through the real Npgsql provider.
    /// </summary>
    internal static IQueryable<ProductCatalogEntry> Match(MicroserviceContext ctx, string term) =>
        ctx.ProductCatalogEntries.Where(e =>
            e.SearchText != null && EF.Functions.ILike(e.SearchText, $"%{term}%", EscapeCharacter));

    /// <summary>Stated on the query rather than left to the server's default.</summary>
    private const string EscapeCharacter = "\\";

    /// <summary>
    /// Trims, rejects what is too short to be worth indexing, and defuses the wildcards.
    /// </summary>
    private static string? Normalise(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var trimmed = query.Trim();

        if (trimmed.Length < MinimumQueryLength) return null;

        // Longer than any product name; a caller sending more is not searching.
        if (trimmed.Length > ProductCatalogEntry.MaxNameLength)
            trimmed = trimmed[..ProductCatalogEntry.MaxNameLength];

        return trimmed
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }
}
