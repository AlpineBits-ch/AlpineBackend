using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AppEnvironment;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Guild.Application.Services;

/// <summary>
/// Downloads the published product exports and loads them, on a schedule, without anybody running a
/// script.
/// </summary>
public class ProductCatalogAutoImportService(
    IServiceProvider services,
    IHttpClientFactory clients,
    ILogger<ProductCatalogAutoImportService> logger) : BackgroundService
{
    /// <summary>How often the schedule is examined.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);

    /// <summary>Arbitrary but fixed: the key that identifies this job to Postgres.</summary>
    private const long AdvisoryLockKey = 8_070_320_260_808;

    /// <summary>Where each database publishes its full export.</summary>
    private static readonly Dictionary<string, string> Exports = new(StringComparer.OrdinalIgnoreCase)
    {
        [ProductCatalogSources.OpenBeautyFacts] =
            "https://static.openbeautyfacts.org/data/openbeautyfacts-products.jsonl.gz",
        [ProductCatalogSources.OpenProductsFacts] =
            "https://static.openproductsfacts.org/data/openproductsfacts-products.jsonl.gz",
        [ProductCatalogSources.OpenPetFoodFacts] =
            "https://static.openpetfoodfacts.org/data/openpetfoodfacts-products.jsonl.gz",
    };

    public const string HttpClientName = "ProductCatalogExport";

    /// <summary>The databases this service can load by itself.</summary>
    public static IEnumerable<string> ImportableSources => Exports.Keys;

    public static bool CanImport(string? source) =>
        source is not null && Exports.ContainsKey(source);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A refresh that failed is a catalog that is a month older than it could be, which
                // is a thing nobody notices. Never a reason to take the service down.
                logger.LogError(e, "Product catalog automatic import failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var config = Env.ProductCatalog;

        if (!config.AutoImportEnabled) return;

        // The quiet hour.
        if (DateTimeOffset.UtcNow.Hour != config.AutoImportHourUtc) return;

        var sources = config.AutoImportSources
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Exports.ContainsKey)
            .ToList();

        if (sources.Count == 0) return;

        await using var scope = services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<MicroserviceContext>();

        var due = new List<string>();

        foreach (var source in sources)
            if (await IsDueAsync(ctx, source, config.AutoImportInterval, ct))
                due.Add(source);

        if (due.Count == 0) return;

        // Taken only once something is actually due, so the usual tick costs one query and no
        // connection held open.
        await using var connection = new NpgsqlConnection(ctx.Database.GetConnectionString());
        await connection.OpenAsync(ct);

        if (!await TryTakeLockAsync(connection, ct))
        {
            logger.LogDebug("Another pod holds the product catalog import lock; skipping this tick");
            return;
        }

        try
        {
            foreach (var source in due)
            {
                ct.ThrowIfCancellationRequested();
                await ImportAsync(source, ct);
            }
        }
        finally
        {
            // Released explicitly rather than left to the disconnect, so a long-lived pod does not
            // hold it until its next restart.
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlock.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlock.ExecuteScalarAsync(ct);
        }
    }

    /// <summary>
    /// True when nothing from this database has been imported inside the interval.
    /// </summary>
    private static async Task<bool> IsDueAsync(
        MicroserviceContext ctx, string source, TimeSpan interval, CancellationToken ct)
    {
        var newest = await ctx.ProductCatalogEntries
            .Where(e => e.Source == source)
            .MaxAsync(e => (DateTimeOffset?)e.ImportedAt, ct);

        return newest is null || DateTimeOffset.UtcNow - newest >= interval;
    }

    private async Task<bool> TryTakeLockAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        // try_ rather than the blocking form: a pod that cannot have the lock has nothing to wait
        // for, because whoever holds it is doing the same work.
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        command.Parameters.AddWithValue("key", AdvisoryLockKey);

        return await command.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Streams one database's export through the filter and into the catalog.</summary>
    public async Task<ProductCatalogImportService.ImportReport> ImportAsync(
        string source, CancellationToken ct)
    {
        var config = Env.ProductCatalog;
        var url = Exports[source];

        var countries = config.AutoImportCountries
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var version = $"{source}-{DateTimeOffset.UtcNow:yyyy-MM-dd}";

        logger.LogInformation(
            "Product catalog automatic import starting for {Source} from {Url}", source, url);

        await using var scope = services.CreateAsyncScope();
        var import = scope.ServiceProvider.GetRequiredService<ProductCatalogImportService>();

        var client = clients.CreateClient(HttpClientName);

        using var response = await client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        await using var network = await response.Content.ReadAsStreamAsync(ct);

        // Decompressed as it arrives.
        await using var gzip = new GZipStream(network, CompressionMode.Decompress);

        var report = await import.ImportAsync(
            ReadExportAsync(gzip, countries, ct), source, version, config.AutoImportBatchPause, ct);

        logger.LogInformation(
            "Product catalog automatic import finished for {Source} {Version}: read {Read}, "
            + "created {Created}, updated {Updated}, skipped {Skipped}, misses resolved {Misses}",
            source, version, report.Read, report.Created, report.Updated, report.Skipped,
            report.MissesResolved);

        return report;
    }

    /// <summary>
    /// The source's own export rows, filtered to our markets and projected onto the seven columns
    /// the pantry stores.
    /// </summary>
    internal static async IAsyncEnumerable<ProductCatalogImportService.Line> ReadExportAsync(
        Stream jsonl, IReadOnlySet<string> countries, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(jsonl);

        while (await reader.ReadLineAsync(ct) is { } raw)
        {
            if (raw.Length == 0) continue;

            ProductCatalogImportService.Line? line;

            try
            {
                using var document = JsonDocument.Parse(raw);
                line = Project(document.RootElement, countries);
            }
            catch (JsonException)
            {
                // One unreadable row in a hundred thousand is not a reason to abandon the rest, and
                // the import report's own counts are what would show a systematically wrong file.
                continue;
            }

            if (line is not null) yield return line;
        }
    }

    private static ProductCatalogImportService.Line? Project(
        JsonElement product, IReadOnlySet<string> countries)
    {
        var barcode = Text(product, "code");
        if (barcode is null) return null;

        if (!SoldHere(product, countries)) return null;

        var nameEn = Text(product, "product_name_en");

        return new ProductCatalogImportService.Line
        {
            Barcode = barcode,
            NameDe = Text(product, "product_name_de"),
            NameFr = Text(product, "product_name_fr"),
            NameIt = Text(product, "product_name_it"),

            // The unlocalised name as a last resort, which is what the live lookup does too.
            NameEn = nameEn ?? Text(product, "product_name"),

            Brand = Text(product, "brands"),
            Quantity = Number(product, "product_quantity"),
            QuantityUnit = Text(product, "product_quantity_unit"),
        };
    }

    /// <summary>Whether this product is tagged with any market we serve.</summary>
    private static bool SoldHere(JsonElement product, IReadOnlySet<string> countries)
    {
        if (!product.TryGetProperty("countries_tags", out var tags)
            || tags.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var tag in tags.EnumerateArray())
            if (tag.ValueKind == JsonValueKind.String && countries.Contains(tag.GetString()!))
                return true;

        return false;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A number the source files inconsistently as a number and as a string, which is why
    /// this reads both rather than trusting the type.</summary>
    private static decimal? Number(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }
}
