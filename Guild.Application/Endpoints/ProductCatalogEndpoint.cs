using System.Security.Claims;
using System.Text.Json;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Identity.Contracts.Bus.Request;
using Identity.Contracts.Bus.Response;
using Microsoft.AspNetCore.Authorization;
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
            Source = ProductCatalogSources.OpenFoodFacts,
            SourceName = ProductCatalogSources.OpenFoodFactsName,
            SourceUrl = ProductCatalogSources.OpenFoodFactsUrl,
            License = ProductCatalogSources.LicenseName,
            LicenseUrl = ProductCatalogSources.LicenseUrl,
            Attribution = ProductCatalogSources.Attribution,
            Count = count,
            LastImportedAt = lastImportedAt,
            SourceVersions = versions,
            ExportUrl = ExportPath,
            ExportContentType = ExportContentType,
            Notice =
                "This is the complete Open Food Facts derived database used to resolve barcodes to "
                + "product names, offered under ODbL 1.0 section 4.6. It contains product data only. "
                + "It contains no product names entered by users of this instance, and no "
                + "information about which household scanned what: those live in a separate table "
                + "that is not part of this database and is not published.",
        });
    }

    /// <summary>The derived database itself, as newline-delimited JSON.</summary>
    [AllowAnonymous]
    [WolverineGet(ExportPath)]
    public IResult ExportAsync(string? after, [NotBody] MicroserviceContext ctx, [NotBody] HttpContext http)
    {
        // Set before the body is written, which is the only time they can be.
        http.Response.Headers.Append("Link", $"<{ProductCatalogSources.LicenseUrl}>; rel=\"license\"");
        http.Response.Headers.Append("X-License", ProductCatalogSources.LicenseName);
        http.Response.Headers.Append("X-Attribution", ProductCatalogSources.Attribution);

        var cursor = after;

        return Results.Stream(async stream =>
        {
            await using var writer = new StreamWriter(stream, leaveOpen: true);

            // The first line is metadata rather than a product, so the file names its own licence
            // even after it has been copied somewhere with no HTTP headers attached.
            await writer.WriteLineAsync(JsonSerializer.Serialize(new
            {
                type = "metadata",
                source = ProductCatalogSources.OpenFoodFacts,
                source_name = ProductCatalogSources.OpenFoodFactsName,
                source_url = ProductCatalogSources.OpenFoodFactsUrl,
                license = ProductCatalogSources.LicenseName,
                license_url = ProductCatalogSources.LicenseUrl,
                attribution = ProductCatalogSources.Attribution,
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

        var report = await import.ImportAsync(
            http.Request.Body,
            string.IsNullOrWhiteSpace(source) ? ProductCatalogSources.OpenFoodFacts : source.Trim(),
            sourceVersion.Trim(),
            http.RequestAborted);

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
