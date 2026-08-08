using System.Text.Json;
using System.Text.Json.Serialization;
using AppEnvironment;
using Guild.Domain.Entity;

namespace Guild.Application.Services;

/// <summary>One barcode, one question to the live product database, one of four answers.</summary>
public class ProductCatalogLookupService(
    IHttpClientFactory clients, ProductCatalogRateLimiter limiter,
    ILogger<ProductCatalogLookupService> logger)
{
    /// <summary>The one named client the pantry has.</summary>
    public const string HttpClientName = "ProductCatalog";

    /// <summary>v3 because the source's own documentation recommends it "for all new integrations".
    /// The measurement this feature was rebuilt on was taken against v2, which is a reason to trust
    /// the endpoint's latency and not a reason to pin the version somebody is moving off.</summary>
    private const string ProductPath = "api/v3/product";

    /// <summary>The parameter that makes non-food barcodes resolvable at all.</summary>
    private const string AllProductTypes = "all";

    /// <summary>The <c>result.id</c> v3 returns, with a 404, when the barcode is real but lives in
    /// a sibling database. Matched on rather than inferred from the status word, which is "failure"
    /// for this and for a genuine absence alike.</summary>
    private const string WrongProductTypeResult = "product_found_with_a_different_product_type";

    /// <summary>Only the columns the pantry stores.</summary>
    private const string Fields =
        "code,product_name,product_name_de,product_name_fr,product_name_it,product_name_en,"
        + "brands,product_quantity,product_quantity_unit,product_type";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Latched so the misconfiguration is stated once.</summary>
    private int _warnedAboutContact;

    /// <summary>Latched for the same reason as <see cref="_warnedAboutContact"/>: the condition it
    /// reports is a fixed misconfiguration, so it produces one line rather than one per scan.</summary>
    private int _warnedAboutProductType;

    public enum LookupKind
    {
        /// <summary>The source answered and has this product.</summary>
        Found,

        /// <summary>The source answered and does not have this product.</summary>
        Absent,

        /// <summary>No usable answer: an outage, a timeout, a body that would not parse.</summary>
        Unreachable,

        /// <summary>
        /// The product exists, in one of the sibling databases, and the host we asked declined to
        /// answer for it.
        /// </summary>
        WrongProductType,

        /// <summary>
        /// Nothing was asked, because nothing was allowed to be: the feature is off, there is no
        /// contact address, or the instance is out of budget for this minute.
        /// </summary>
        NotAttempted,
    }

    public readonly record struct Lookup(LookupKind Kind, ProductResponse.ProductFields? Product);

    /// <summary>Asks about one barcode, if this instance is allowed to.</summary>
    public async Task<Lookup> LookupAsync(
        string barcode, TimeSpan timeout, int reserve = 0, CancellationToken ct = default)
    {
        var config = Env.ProductCatalog;

        if (!config.LiveFillEnabled) return new Lookup(LookupKind.NotAttempted, null);

        if (string.IsNullOrWhiteSpace(config.ContactEmail))
        {
            WarnAboutContactOnce();
            return new Lookup(LookupKind.NotAttempted, null);
        }

        // Before the request rather than after a failure, and it is the only place the budget is
        // spent. A denial here is not a problem to report: it is the limiter working.
        if (!await limiter.TryTakeAsync(reserve, ct)) return new Lookup(LookupKind.NotAttempted, null);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);

        try
        {
            var client = clients.CreateClient(HttpClientName);

            using var response = await client.GetAsync(
                $"{ProductPath}/{Uri.EscapeDataString(barcode)}.json"
                + $"?product_type={AllProductTypes}&fields={Fields}", budget.Token);

            var notFound = response.StatusCode == System.Net.HttpStatusCode.NotFound;

            // Anything that is neither a success nor the documented not-found is an outage.
            if (!response.IsSuccessStatusCode && !notFound)
                return new Lookup(LookupKind.Unreachable, null);

            // Read even on a 404, which the earlier version did not.
            ProductResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<ProductResponse>(Json, budget.Token);
            }
            catch (JsonException)
            {
                // A 404 we cannot parse is still a 404, and that is the documented answer for an
                // unknown barcode. An unparseable success is an outage.
                return new Lookup(notFound ? LookupKind.Absent : LookupKind.Unreachable, null);
            }

            if (body?.SaysWrongProductType() == true)
            {
                WarnAboutProductTypeOnce(barcode);
                return new Lookup(LookupKind.WrongProductType, null);
            }

            if (notFound || body is null || body.SaysNotFound() || body.Product is null)
                return new Lookup(LookupKind.Absent, null);

            return new Lookup(LookupKind.Found, body.Product);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown or an abandoned request, not a failure of the source.
            throw;
        }
        catch (Exception e)
        {
            // Everything else, the inline timeout included: a budget that expired is a source too
            // slow to be worth waiting for, which is the same fact as a source that is down.
            logger.LogDebug(e, "Product catalog lookup for {Barcode} did not complete", barcode);
            return new Lookup(LookupKind.Unreachable, null);
        }
    }

    /// <summary>
    /// A catalog row built from what the source said, or null when the source has the product but
    /// cannot name it in any language.
    /// </summary>
    public static ProductCatalogEntry? BuildEntry(
        string barcode, ProductResponse.ProductFields product, DateTimeOffset now)
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
            Source = ProductCatalogSources.ForProductType(product.ProductType).Id,

            // Tagged apart from the bulk extracts on purpose: a row fetched one at a time came from
            // a different snapshot of the source than the file somebody imported last month, and the
            // 4.6 export is more honest if it can say which.
            SourceVersion = "live",
            ImportedAt = now,
        };

        // The unlocalised name as a last resort.
        if (!entry.HasAnyName())
            entry.NameEn = Clean(product.ProductName, ProductCatalogEntry.MaxNameLength);

        return entry.HasAnyName() ? entry : null;
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>States the one thing an operator can do about it, once.</summary>
    private void WarnAboutContactOnce()
    {
        if (Interlocked.Exchange(ref _warnedAboutContact, 1) == 1) return;

        logger.LogWarning(
            "Product catalog live lookup is enabled but PRODUCT_CATALOG_CONTACT_EMAIL is not set, so "
            + "no barcode will be resolved from Open Food Facts. Their API documentation asks callers "
            + "to identify themselves as AppName/Version (ContactEmail) and treats an anonymous one "
            + "as a bot. Scans still work; they will ask for a name the first time a barcode is seen. "
            + "Set the address, or set PRODUCT_CATALOG_LIVE_FILL_ENABLED=false to stop this warning.");
    }

    /// <summary>States the one thing an operator can do about it, once.</summary>
    private void WarnAboutProductTypeOnce(string barcode)
    {
        if (Interlocked.Exchange(ref _warnedAboutProductType, 1) == 1) return;

        logger.LogWarning(
            "Open Food Facts answered for {Barcode} with product_found_with_a_different_product_type "
            + "even though product_type=all was sent. That means the parameter did not arrive: "
            + "PRODUCT_CATALOG_API_BASE_URL may point at a single-flavour host (world.openbeautyfacts.org "
            + "and friends), or a proxy is stripping the query string. Barcodes held in the sibling "
            + "databases - cosmetics, cleaning and household products - will not resolve until this is "
            + "fixed. Point the base URL at https://world.openfoodfacts.org/ which answers for all of "
            + "them.", barcode);
    }

    /// <summary>The slice of the source's product document that the pantry stores.</summary>
    public sealed class ProductResponse
    {
        /// <summary>What v3 says the outcome was, as a stable identifier.</summary>
        [JsonPropertyName("result")] public ResultField? Result { get; set; }
        /// <summary>Read as a raw element because the two API versions disagree on its type: v2
        /// sends an integer (0 for not found), v3 sends a string ("failure"). Typing it as either
        /// makes the other one a parse error, which the caller would then read as an outage and
        /// retry forever.</summary>
        [JsonPropertyName("status")] public JsonElement Status { get; set; }

        [JsonPropertyName("product")] public ProductFields? Product { get; set; }

        /// <summary>"Product not found" delivered with a 200, which both versions do for some shapes
        /// of request. Treated as the 404 it means.</summary>
        public bool SaysNotFound() => Status.ValueKind switch
        {
            JsonValueKind.Number => Status.TryGetInt32(out var code) && code == 0,
            JsonValueKind.String =>
                string.Equals(Status.GetString(), "failure", StringComparison.OrdinalIgnoreCase),

            // Absent or anything else: judged on whether a product came back instead, which is the
            // only signal that cannot be misread.
            _ => false,
        };

        /// <summary>"The product exists, but not in the database you asked." Distinguished from a
        /// plain absence because it is the opposite of one: the API has just confirmed the barcode
        /// is real. See <see cref="LookupKind.WrongProductType"/>.</summary>
        public bool SaysWrongProductType() =>
            string.Equals(Result?.Id, WrongProductTypeResult, StringComparison.OrdinalIgnoreCase);

        public sealed class ResultField
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
        }

        public sealed class ProductFields
        {
            /// <summary>Which of the four databases holds this product: "food", "beauty", "product"
            /// or "petfood". Present because <c>product_type=all</c> was asked for, and the reason a
            /// single request can serve four databases without misattributing any of them.</summary>
            [JsonPropertyName("product_type")] public string? ProductType { get; set; }

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
