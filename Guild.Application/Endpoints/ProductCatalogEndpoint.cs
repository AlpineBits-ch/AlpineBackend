using System.Security.Claims;
using System.Text.Json;
using AppEnvironment;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// The shared product catalog: what it is, a machine-readable copy of it, and the way to load one.
/// </summary>
public class ProductCatalogEndpoint
{
    /// <summary>Newline-delimited JSON, one product per line.</summary>
    public const string ExportContentType = "application/x-ndjson";

    private const string ExportPath = "/api/v1/pantry/catalog/export";

    /// <summary>Rows fetched per round trip while streaming.</summary>
    private const int ExportPageSize = 1000;

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>What the derived database is, under what licence, how big it is and where to fetch
    /// it. The human-readable half of the 4.6 offer.</summary>
    [AllowAnonymous]
    [WolverineGet("/api/v1/pantry/catalog")]
    public async Task<IResult> InfoAsync([NotBody] MicroserviceContext ctx)
    {
        var count = await ctx.ProductCatalogEntries.CountAsync();

        var lastImportedAt = count == 0
            ? (DateTimeOffset?)null
            : await ctx.ProductCatalogEntries.MaxAsync(e => e.ImportedAt);

        var versions = await ctx.ProductCatalogEntries
            .GroupBy(e => e.SourceVersion)
            .OrderByDescending(g => g.Max(e => e.ImportedAt))
            .Select(g => g.Key)
            .Take(20)
            .ToListAsync();

        return Results.Ok(new ProductCatalogInfoDto
        {
            Source = ProductCatalogSources.Food.Id,
            SourceName = ProductCatalogSources.Food.Name,
            SourceUrl = ProductCatalogSources.Food.Url,
            License = ProductCatalogSources.LicenseName,
            LicenseUrl = ProductCatalogSources.LicenseUrl,
            Attribution = ProductCatalogSources.Food.Attribution,
            Sources = await BuildSourcesAsync(ctx),
            Count = count,
            LastImportedAt = lastImportedAt,
            SourceVersions = versions,
            ExportUrl = ExportPath,
            ExportContentType = ExportContentType,
            Notice =
                "This is the complete Open Food Facts derived database used to resolve barcodes to "
                + "product names, offered under ODbL 1.0 section 4.6. It covers groceries, cosmetics "
                + "and general household products, which are separate databases in the Open Food "
                + "Facts family under the same licence; the per-row source field says which one each "
                + "row came from. It contains product data only. It contains no product names "
                + "entered by users of this instance, and no information about which household "
                + "scanned what: those live in a separate table that is not part of this database "
                + "and is not published.",
        });
    }

    /// <summary>Keyword search over this instance's copy of the catalog.</summary>
    [Authorize]
    [WolverineGet("/api/v1/pantry/catalog/search")]
    public async Task<IResult> SearchAsync(
        string? q, int? limit, int? offset,
        [NotBody] ProductCatalogSearchService search, [NotBody] HttpContext http,
        CancellationToken ct)
    {
        // Same header the scan path reads, and for the same reason: nothing in this product stores a
        // locale, and the phone sends this on every request anyway.
        var languages = ProductCatalogService.ParseLanguages(http.Request.Headers.AcceptLanguage);

        var results = await search.SearchAsync(q, languages, limit, offset, ct);

        var matches = results.Hits
            .Select(hit => ProductCatalogService.ToDto(
                new ProductCatalogService.Match(hit.Entry, hit.Name, hit.Language)))
            .ToList();

        return Results.Ok(new ProductCatalogSearchResultDto
        {
            Query = q?.Trim() ?? string.Empty,
            Results = matches,
            Count = results.Count,
            CountIsLowerBound = results.CountIsLowerBound,
            Limit = results.Limit,
            Offset = results.Offset,

            // Built from the sources on this page rather than from the whole table, so the notice
            // names what the user is actually looking at.
            Attribution = CombinedAttribution(
                matches.Select(m => m.Source).Distinct()
                    .Select(s => ProductCatalogSources.For(s))
                    .Select(s => new ProductCatalogSourceDto
                    {
                        Source = s.Id, SourceName = s.Name, SourceUrl = s.Url,
                        Attribution = s.Attribution, Count = 0,
                    })
                    .ToList()),
            License = ProductCatalogSources.LicenseName,
            LicenseUrl = ProductCatalogSources.LicenseUrl,
        });
    }

    /// <summary>One ODbL 4.3 notice naming every database the export actually draws on.</summary>
    private static string CombinedAttribution(IReadOnlyList<ProductCatalogSourceDto> sources)
    {
        if (sources.Count == 0) return ProductCatalogSources.Food.Attribution;
        if (sources.Count == 1) return sources[0].Attribution;

        var names = sources.Select(s => s.SourceName).ToList();

        return $"Contains information from {string.Join(", ", names.Take(names.Count - 1))} and "
               + $"{names[^1]}, which is made available here under the Open Database License (ODbL).";
    }

    /// <summary>
    /// The databases actually represented in the table, each with the notice it is owed.
    /// </summary>
    private static async Task<List<ProductCatalogSourceDto>> BuildSourcesAsync(MicroserviceContext ctx)
    {
        var counts = await ctx.ProductCatalogEntries
            .GroupBy(e => e.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        return counts.Select(row =>
        {
            var source = ProductCatalogSources.For(row.Source);

            return new ProductCatalogSourceDto
            {
                // The stored value, not the resolved one: an unrecognised source must report itself
                // rather than be silently relabelled as the fallback it renders with.
                Source = row.Source,
                SourceName = source.Name,
                SourceUrl = source.Url,
                Attribution = source.Attribution,
                Count = row.Count,
            };
        }).ToList();
    }

    /// <summary>The derived database itself, as newline-delimited JSON.</summary>
    [AllowAnonymous]
    [WolverineGet(ExportPath)]
    public async Task<IResult> ExportAsync(
        string? after, [NotBody] MicroserviceContext ctx, [NotBody] HttpContext http)
    {
        // Resolved before anything is written, because the headers below name the databases being
        // credited and a header cannot be revised once the body has started.
        var sources = await BuildSourcesAsync(ctx);

        // Set before the body is written, which is the only time they can be.
        http.Response.Headers.Append("Link", $"<{ProductCatalogSources.LicenseUrl}>; rel=\"license\"");
        http.Response.Headers.Append("X-License", ProductCatalogSources.LicenseName);
        http.Response.Headers.Append("X-Attribution", CombinedAttribution(sources));

        var cursor = after;

        return Results.Stream(async stream =>
        {
            await using var writer = new StreamWriter(stream, leaveOpen: true);

            // The first line is metadata rather than a product, so the file names its own licence
            // even after it has been copied somewhere with no HTTP headers attached.
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "metadata",
                source = ProductCatalogSources.Food.Id,
                source_name = ProductCatalogSources.Food.Name,
                source_url = ProductCatalogSources.Food.Url,
                license = ProductCatalogSources.LicenseName,
                license_url = ProductCatalogSources.LicenseUrl,
                attribution = CombinedAttribution(sources),

                // Added alongside the scalars above rather than replacing them, so a reader written
                // against the single-source export still parses this one.
                sources = sources.Select(s => new
                {
                    source = s.Source,
                    source_name = s.SourceName,
                    source_url = s.SourceUrl,
                    attribution = s.Attribution,
                    count = s.Count,
                }),
                generated_at = DateTimeOffset.UtcNow,
            }, ExportJson));

            while (true)
            {
                var page = await BuildExportPage(ctx, cursor).ToListAsync();

                if (page.Count == 0) break;

                foreach (var entry in page)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        type = "product",
                        barcode = entry.Barcode,
                        name_de = entry.NameDe,
                        name_fr = entry.NameFr,
                        name_it = entry.NameIt,
                        name_en = entry.NameEn,
                        brand = entry.Brand,
                        quantity = entry.Quantity,
                        quantity_unit = entry.QuantityUnit,
                        source = entry.Source,
                        source_version = entry.SourceVersion,
                        imported_at = entry.ImportedAt,
                    }, ExportJson));

                await writer.FlushAsync();

                cursor = page[^1].Barcode;

                if (page.Count < ExportPageSize) break;
            }
        }, ExportContentType);
    }

    /// <summary>One page of the export, keyset-paged on the barcode.</summary>
    internal static IQueryable<ProductCatalogEntry> BuildExportPage(
        MicroserviceContext ctx, string? after) =>
        ctx.ProductCatalogEntries.AsNoTracking()
            .Where(e => after == null || string.Compare(e.Barcode, after) > 0)
            .OrderBy(e => e.Barcode)
            .Take(ExportPageSize);

    /// <summary>Loads a filtered extract, as newline-delimited JSON in the request body.</summary>
    [Authorize]
    [WolverinePost("/api/v1/pantry/catalog/import")]
    public async Task<IResult> ImportAsync(string? sourceVersion, string? source,
        [NotBody] ProductCatalogImportService import, [NotBody] IMessageBus bus,
        [NotBody] HttpContext http, [NotBody] ClaimsPrincipal user,
        [NotBody] ILogger<ProductCatalogEndpoint> logger)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await IsAdministratorAsync(bus, userId, logger)) return Results.Forbid();

        if (string.IsNullOrWhiteSpace(sourceVersion))
            return Results.BadRequest(
                "sourceVersion is required: it is what the ODbL 4.6 export uses to say which "
                + "snapshot a row came from.");

        var resolved = string.IsNullOrWhiteSpace(source)
            ? ProductCatalogSources.OpenFoodFacts
            : source.Trim();

        // Checked rather than accepted as free text, which it used to be.
        if (!ProductCatalogSources.All.Any(s => string.Equals(
                s.Id, resolved, StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(
                $"Unknown source '{resolved}'. Expected one of: "
                + $"{string.Join(", ", ProductCatalogSources.All.Select(s => s.Id))}. The source "
                + "decides which database a row is credited to and which product page a client "
                + "links to, so it cannot be free text.");

        // Kestrel caps a request body at 30 MB by default and a food extract is several hundred, so
        // the curl line the extract script prints answered 413 for the one database big enough to
        // need it.
        var size = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = null;

        var report = await import.ImportAsync(
            http.Request.Body, resolved, sourceVersion.Trim(), http.RequestAborted);

        return Results.Ok(new ProductCatalogImportResultDto
        {
            SourceVersion = report.SourceVersion,
            Read = report.Read,
            Created = report.Created,
            Updated = report.Updated,
            Skipped = report.Skipped,
            Malformed = report.Malformed,
            MissesResolved = report.MissesResolved,
        });
    }

    /// <summary>Starts a refresh now, rather than waiting for the nightly window.</summary>
    [Authorize]
    [WolverinePost("/api/v1/pantry/catalog/import/auto")]
    public async Task<IResult> TriggerImportAsync(string? source,
        [NotBody] ProductCatalogAutoImportService auto, [NotBody] IMessageBus bus,
        [NotBody] ClaimsPrincipal user, [NotBody] IHostApplicationLifetime lifetime,
        [NotBody] ILogger<ProductCatalogEndpoint> logger)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await IsAdministratorAsync(bus, userId, logger)) return Results.Forbid();

        var requested = string.IsNullOrWhiteSpace(source)
            ? Env.ProductCatalog.AutoImportSources.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [source.Trim()];

        if (requested.Length == 0)
            return Results.BadRequest("No source to import.");

        foreach (var candidate in requested)
            if (!ProductCatalogAutoImportService.CanImport(candidate))
                return Results.BadRequest(
                    $"'{candidate}' has no automatic import. Available: "
                    + $"{string.Join(", ", ProductCatalogAutoImportService.ImportableSources)}. The "
                    + "food database is deliberately absent - its export is 11.77 GB and is loaded "
                    + "with deploy/off-catalog-extract.sh instead.");

        // Detached from the request on purpose, with the application's stopping token rather than
        // the request's: the caller is not waiting, and cancelling on their disconnect would mean a
        // refresh dies the moment the console tab closes.
        _ = Task.Run(async () =>
        {
            foreach (var candidate in requested)
            {
                try
                {
                    await auto.ImportAsync(candidate, lifetime.ApplicationStopping);
                }
                catch (Exception e)
                {
                    logger.LogError(
                        e, "Operator-triggered product catalog import failed for {Source}", candidate);
                }
            }
        }, CancellationToken.None);

        return Results.Accepted(value: new
        {
            started = requested,
            message =
                "Import started. It runs in the background and takes several minutes; watch "
                + "GET /api/v1/pantry/catalog for the per-source counts to move.",
        });
    }

    /// <summary>Fails closed: an Identity outage must not turn a global write surface into an open
    /// one, so an unanswered check is a refusal and the log line is the only thing that tells it
    /// apart from a legitimate one.</summary>
    private static async Task<bool> IsAdministratorAsync(
        IMessageBus bus, string userId, ILogger logger)
    {
        try
        {
            var response = await bus.InvokeAsync<IsUserAdministrativeResponse>(
                new IsUserAdministrativeRequest { UserId = userId });

            if (response.IsAdministrative) return true;

            logger.LogWarning(
                "Account {UserId} is not a platform administrator; refusing the product catalog "
                + "import.", userId);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "The administrator check for {UserId} could not be completed - Identity did not "
                + "answer over the bus. Refusing the product catalog import; this is an outage, not "
                + "a permission decision.", userId);
        }

        return false;
    }
}
