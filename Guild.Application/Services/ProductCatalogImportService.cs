using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Guild.Domain.Entity;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Loads a filtered product extract into the shared catalog.</summary>
public class ProductCatalogImportService(
    MicroserviceContext ctx, ILogger<ProductCatalogImportService> logger)
{
    /// <summary>Rows per committed batch.</summary>
    public const int BatchSize = 500;

    /// <summary>Refuses a line longer than this rather than buffering it.</summary>
    private const int MaxLineLength = 16 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>One line of the extract.</summary>
    public sealed class Line
    {
        public string? Barcode { get; set; }
        public string? NameDe { get; set; }
        public string? NameFr { get; set; }
        public string? NameIt { get; set; }
        public string? NameEn { get; set; }
        public string? Brand { get; set; }
        public decimal? Quantity { get; set; }
        public string? QuantityUnit { get; set; }
    }

    public sealed class ImportReport
    {
        public required string SourceVersion { get; init; }
        public int Read { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int Malformed { get; set; }
        public int MissesResolved { get; set; }
    }

    /// <summary>Reads newline-delimited JSON and upserts it, committing as it goes.</summary>
    public async Task<ImportReport> ImportAsync(
        Stream ndjson, string source, string sourceVersion, CancellationToken ct = default)
    {
        var report = new ImportReport { SourceVersion = sourceVersion };

        using var reader = new StreamReader(ndjson, Encoding.UTF8, leaveOpen: true);

        // Keyed rather than a list so a barcode repeated inside one batch collapses to its last
        // occurrence.
        var batch = new Dictionary<string, Line>(BatchSize, StringComparer.Ordinal);

        while (await reader.ReadLineAsync(ct) is { } raw)
        {
            if (raw.Length == 0) continue;

            report.Read++;

            if (raw.Length > MaxLineLength)
            {
                report.Malformed++;
                continue;
            }

            var line = TryParse(raw, report);
            if (line is null) continue;

            var barcode = Normalise(line.Barcode, ProductCatalogEntry.MaxBarcodeLength);

            // No barcode is nothing to key on, and no name in any of the four languages can never
            // fill anything - storing it would grow the table and the export for no reader.
            if (barcode is null || !HasAnyName(line))
            {
                report.Skipped++;
                continue;
            }

            batch[barcode] = line;

            if (batch.Count >= BatchSize) await FlushAsync(batch, source, sourceVersion, report, ct);
        }

        await FlushAsync(batch, source, sourceVersion, report, ct);

        logger.LogInformation(
            "Product catalog import {SourceVersion} finished: read {Read}, created {Created}, "
            + "updated {Updated}, skipped {Skipped}, malformed {Malformed}, misses resolved {Misses}",
            sourceVersion, report.Read, report.Created, report.Updated, report.Skipped,
            report.Malformed, report.MissesResolved);

        return report;
    }

    private Line? TryParse(string raw, ImportReport report)
    {
        try
        {
            var line = JsonSerializer.Deserialize<Line>(raw, Json);
            if (line is not null) return line;
        }
        catch (JsonException)
        {
            // Swallowed rather than thrown: one bad line in a hundred-thousand-line extract must
            // not abandon the other ninety-nine thousand.
        }

        report.Malformed++;
        return null;
    }

    private async Task FlushAsync(
        Dictionary<string, Line> batch, string source, string sourceVersion, ImportReport report,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;

        var barcodes = batch.Keys.ToList();
        var now = DateTimeOffset.UtcNow;

        var existing = await ctx.ProductCatalogEntries
            .Where(e => barcodes.Contains(e.Barcode))
            .ToDictionaryAsync(e => e.Barcode, ct);

        foreach (var (barcode, line) in batch)
        {
            if (existing.TryGetValue(barcode, out var entry))
            {
                report.Updated++;
            }
            else
            {
                entry = new ProductCatalogEntry { Barcode = barcode };
                ctx.ProductCatalogEntries.Add(entry);
                report.Created++;
            }

            Apply(entry, line, source, sourceVersion, now);
        }

        var answered = await ctx.ProductCatalogMisses
            .Where(m => barcodes.Contains(m.Barcode))
            .ToListAsync(ct);

        if (answered.Count > 0)
        {
            ctx.ProductCatalogMisses.RemoveRange(answered);
            report.MissesResolved += answered.Count;
        }

        await ctx.SaveChangesAsync(ct);

        // Otherwise the tracker keeps every row of the whole extract alive and each batch's
        // change detection gets slower than the one before it.
        ctx.ChangeTracker.Clear();

        batch.Clear();
    }

    private static void Apply(
        ProductCatalogEntry entry, Line line, string source, string sourceVersion, DateTimeOffset now)
    {
        entry.NameDe = Normalise(line.NameDe, ProductCatalogEntry.MaxNameLength);
        entry.NameFr = Normalise(line.NameFr, ProductCatalogEntry.MaxNameLength);
        entry.NameIt = Normalise(line.NameIt, ProductCatalogEntry.MaxNameLength);
        entry.NameEn = Normalise(line.NameEn, ProductCatalogEntry.MaxNameLength);
        entry.Brand = Normalise(line.Brand, ProductCatalogEntry.MaxBrandLength);
        entry.QuantityUnit = Normalise(line.QuantityUnit, ProductCatalogEntry.MaxUnitLength);

        // A zero or negative pack size is not a pack size.
        entry.Quantity = line.Quantity is > 0 ? line.Quantity : null;

        entry.Source = Normalise(source, ProductCatalogEntry.MaxSourceLength)
                       ?? ProductCatalogSources.OpenFoodFacts;
        entry.SourceVersion = Normalise(sourceVersion, ProductCatalogEntry.MaxSourceVersionLength)
                              ?? now.ToString("yyyy-MM-dd");
        entry.ImportedAt = now;
    }

    private static bool HasAnyName(Line line) =>
        !string.IsNullOrWhiteSpace(line.NameDe) || !string.IsNullOrWhiteSpace(line.NameFr)
        || !string.IsNullOrWhiteSpace(line.NameIt) || !string.IsNullOrWhiteSpace(line.NameEn);

    /// <summary>Trims, empties to null, and truncates to the column.</summary>
    private static string? Normalise(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
