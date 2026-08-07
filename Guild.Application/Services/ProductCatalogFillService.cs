using System.Text.Json;
using System.Text.Json.Serialization;
using AppEnvironment;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// Best-effort backfill: asks the live source about barcodes the local catalog could not answer, a
/// handful at a time, on the household sweep.
/// </summary>
public class ProductCatalogFillService(
    MicroserviceContext ctx, IHttpClientFactory clients, ILogger<ProductCatalogFillService> logger)
{
    public const string HttpClientName = "ProductCatalog";

    /// <summary>Only the columns the pantry stores.</summary>
    private const string Fields =
        "code,product_name,product_name_de,product_name_fr,product_name_it,product_name_en,"
        + "brands,product_quantity,product_quantity_unit";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Asks about the misses that are due, and returns how many were resolved.</summary>
    public async Task<int> FillAsync(CancellationToken ct = default)
    {
        var config = Env.ProductCatalog;

        // Two gates rather than one.
        if (!config.LiveFillEnabled || string.IsNullOrWhiteSpace(config.ContactEmail)) return 0;

        var now = DateTimeOffset.UtcNow;

        var due = await ctx.ProductCatalogMisses
            .Where(m => m.RetryAfter != null && m.RetryAfter <= now)
            // Longest-waiting first, so a backlog drains in the order it accumulated rather than in
            // whatever order the planner returns.
            .OrderBy(m => m.RetryAfter)
            .Take(Math.Max(1, config.FillBatchSize))
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        var resolved = 0;

        foreach (var miss in due)
        {
            if (ct.IsCancellationRequested) break;

            var outcome = await LookupAsync(miss.Barcode, ct);

            switch (outcome.Kind)
            {
                case LookupKind.Found:
                    Upsert(miss.Barcode, outcome.Product!, miss.Source, now);
                    ctx.ProductCatalogMisses.Remove(miss);
                    resolved++;
                    break;

                case LookupKind.Absent:
                    miss.RecordAbsent(now);
                    break;

                default:
                    miss.RecordUnreachable(now);
                    break;
            }
        }

        await ctx.SaveChangesAsync(ct);

        if (resolved > 0)
            logger.LogDebug("Product catalog filled {Resolved} of {Asked} due misses", resolved, due.Count);

        return resolved;
    }

    private enum LookupKind
    {
        /// <summary>The source answered and has this product.</summary>
        Found,

        /// <summary>The source answered and does not have this product.</summary>
        Absent,

        /// <summary>No usable answer: an outage, a timeout, a body that would not parse.</summary>
        Unreachable,
    }

    private readonly record struct Lookup(LookupKind Kind, ProductResponse.ProductFields? Product);

    private async Task<Lookup> LookupAsync(string barcode, CancellationToken ct)
    {
        try
        {
            var client = clients.CreateClient(HttpClientName);

            using var response = await client.GetAsync(
                $"api/v2/product/{Uri.EscapeDataString(barcode)}?fields={Fields}", ct);

            // The documented answer for an unknown barcode, and the one this whole table exists to
            // remember. Anything else that is not a success is an outage, not an absence.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Lookup(LookupKind.Absent, null);

            if (!response.IsSuccessStatusCode) return new Lookup(LookupKind.Unreachable, null);

            var body = await response.Content.ReadFromJsonAsync<ProductResponse>(Json, ct);

            // status 0 is "product not found" delivered with a 200, which the API does for some
            // shapes of request. Treated as the 404 it means.
            if (body is null || body.Status == 0 || body.Product is null)
                return new Lookup(LookupKind.Absent, null);

            return new Lookup(LookupKind.Found, body.Product);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a failure of the source. Left untouched so the next run retries it.
            throw;
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Product catalog lookup for {Barcode} did not complete", barcode);
            return new Lookup(LookupKind.Unreachable, null);
        }
    }

    private void Upsert(string barcode, ProductResponse.ProductFields product, string source, DateTimeOffset now)
    {
        var entry = new ProductCatalogEntry
        {
            Barcode = barcode,
            NameDe = Clean(product.ProductNameDe, ProductCatalogEntry.MaxNameLength),
            NameFr = Clean(product.ProductNameFr, ProductCatalogEntry.MaxNameLength),
            NameIt = Clean(product.ProductNameIt, ProductCatalogEntry.MaxNameLength),
            NameEn = Clean(product.ProductNameEn, ProductCatalogEntry.MaxNameLength),
            Brand = Clean(product.Brands, ProductCatalogEntry.MaxBrandLength),
            Quantity = product.ProductQuantity is > 0 ? product.ProductQuantity : null,
            QuantityUnit = Clean(product.ProductQuantityUnit, ProductCatalogEntry.MaxUnitLength),
            Source = source,

            // Tagged apart from the bulk extracts on purpose: a row fetched one at a time came from
            // a different snapshot of the source than the file somebody imported last month, and the
            // 4.6 export is more honest if it can say which.
            SourceVersion = "live",
            ImportedAt = now,
        };

        // The unlocalised name as a last resort.
        if (!entry.HasAnyName())
            entry.NameEn = Clean(product.ProductName, ProductCatalogEntry.MaxNameLength);

        // A product the source has but cannot name is not worth a row: it can never fill anything,
        // and it would make the miss look answered when it is not.
        if (!entry.HasAnyName()) return;

        ctx.ProductCatalogEntries.Add(entry);
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>The slice of the source's product document that the pantry stores.</summary>
    public sealed class ProductResponse
    {
        public int Status { get; set; }

        [JsonPropertyName("product")] public ProductFields? Product { get; set; }

        public sealed class ProductFields
        {
            [JsonPropertyName("product_name")] public string? ProductName { get; set; }
            [JsonPropertyName("product_name_de")] public string? ProductNameDe { get; set; }
            [JsonPropertyName("product_name_fr")] public string? ProductNameFr { get; set; }
            [JsonPropertyName("product_name_it")] public string? ProductNameIt { get; set; }
            [JsonPropertyName("product_name_en")] public string? ProductNameEn { get; set; }
            [JsonPropertyName("brands")] public string? Brands { get; set; }
            [JsonPropertyName("product_quantity")] public decimal? ProductQuantity { get; set; }
            [JsonPropertyName("product_quantity_unit")] public string? ProductQuantityUnit { get; set; }
        }
    }
}
