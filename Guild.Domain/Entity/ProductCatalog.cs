namespace Guild.Domain.Entity;

/// <summary>
/// The names and licences of the third-party databases the catalog is built from, kept as constants
/// so the attribution a client renders and the notice the export serves cannot drift apart.
/// </summary>
public static class ProductCatalogSources
{
    /// <summary>The value stored in <see cref="ProductCatalogEntry.Source"/>.</summary>
    public const string OpenFoodFacts = "openfoodfacts";

    public const string OpenFoodFactsName = "Open Food Facts";
    public const string OpenFoodFactsUrl = "https://openfoodfacts.org";

    public const string LicenseName = "Open Database License (ODbL) v1.0";
    public const string LicenseUrl = "https://opendatacommons.org/licenses/odbl/1-0/";

    /// <summary>ODbL 4.3's model notice, which the licence itself states is sufficient for a
    /// Produced Work. Written out rather than assembled from the parts above so that the exact
    /// wording the licence blesses is what ships.</summary>
    public const string Attribution =
        "Contains information from Open Food Facts, which is made available here under the "
        + "Open Database License (ODbL).";

    /// <summary>The product page for one barcode.</summary>
    public static string ProductUrl(string barcode) =>
        $"https://world.openfoodfacts.org/product/{barcode}";
}

/// <summary>One product name in one language, and which language that turned out to be.</summary>
public sealed record ProductCatalogName(string Language, string Text);

/// <summary>
/// A global, barcode-keyed copy of what a public product database says a code means.
/// </summary>
public class ProductCatalogEntry
{
    /// <summary>Matches <c>PantryCaptureService.MaxBarcodeLength</c>.</summary>
    public const int MaxBarcodeLength = 64;

    public const int MaxNameLength = 200;
    public const int MaxBrandLength = 200;
    public const int MaxUnitLength = 16;
    public const int MaxSourceLength = 32;
    public const int MaxSourceVersionLength = 64;

    /// <summary>The key.</summary>
    public string Barcode { get; set; } = null!;

    /// <summary>
    /// One column per language rather than a names table, because Switzerland has exactly three
    /// official languages plus English and that is not going to grow.
    /// </summary>
    public string? NameDe { get; set; }

    public string? NameFr { get; set; }
    public string? NameIt { get; set; }
    public string? NameEn { get; set; }

    /// <summary>Free text and inconsistently cased at source ("denner" and "Denner" both occur, and
    /// one observed row reads "Nutella, Ferrero, Yum yum"). Kept as it arrives rather than
    /// normalised, because a guess at which comma-separated fragment is the real brand is a guess
    /// we would then have to defend on every screen.</summary>
    public string? Brand { get; set; }

    /// <summary>Pack size, normalised by the source: 380 with a unit of "g".</summary>
    public decimal? Quantity { get; set; }

    /// <summary>The source's normalised unit ("g", "ml"), not its free-text quantity string, which
    /// is unnormalised enough to be useless ("380g", "1 Kilogramm", "250 g ℮" all occur).</summary>
    public string? QuantityUnit { get; set; }

    /// <summary>Which database this row came from - see <see cref="ProductCatalogSources"/>. This
    /// is what tells a reader which licence and which attribution apply, so it is stored rather
    /// than assumed.</summary>
    public string Source { get; set; } = ProductCatalogSources.OpenFoodFacts;

    /// <summary>Which extract produced this row, as the operator named it (a dataset date, an
    /// export tag, or "live" for a row the best-effort filler fetched one at a time). ODbL 4.6 asks
    /// us to publish the derived database; being able to say which snapshot a row came from is what
    /// makes that publication auditable rather than a shrug.</summary>
    public string SourceVersion { get; set; } = null!;

    public DateTimeOffset ImportedAt { get; set; }

    /// <summary>The order names are tried when the caller expressed no preference, and the
    /// exhaustive list of columns after that. German first because the primary market is
    /// German-speaking Switzerland; English last because it is the least likely to be what a Swiss
    /// package actually says.</summary>
    public static readonly string[] FallbackLanguages = ["de", "fr", "it", "en"];

    /// <summary>
    /// The best name for a caller who prefers <paramref name="preferred"/>, or null when this row
    /// has no usable name in any language.
    /// </summary>
    public ProductCatalogName? NameFor(IReadOnlyList<string>? preferred)
    {
        if (preferred is not null)
            foreach (var language in preferred)
                if (Pick(language) is { } wanted)
                    return wanted;

        foreach (var language in FallbackLanguages)
            if (Pick(language) is { } fallback)
                return fallback;

        return null;
    }

    /// <summary>True when this row can fill a name for somebody.</summary>
    public bool HasAnyName() =>
        !string.IsNullOrWhiteSpace(NameDe) || !string.IsNullOrWhiteSpace(NameFr)
        || !string.IsNullOrWhiteSpace(NameIt) || !string.IsNullOrWhiteSpace(NameEn);

    private ProductCatalogName? Pick(string language) => language switch
    {
        "de" => Wrap("de", NameDe),
        "fr" => Wrap("fr", NameFr),
        "it" => Wrap("it", NameIt),
        "en" => Wrap("en", NameEn),
        _ => null,
    };

    private static ProductCatalogName? Wrap(string language, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new ProductCatalogName(language, text.Trim());
}

/// <summary>
/// A barcode the catalog was asked for and could not answer, with a date after which it is worth
/// asking the source again.
/// </summary>
public class ProductCatalogMiss
{
    /// <summary>
    /// How long a miss is left alone after each confirmed absence: a week, then a month, then a
    /// quarter.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromDays(90)];

    /// <summary>How long to wait after the source failed to answer at all.</summary>
    private static readonly TimeSpan UnreachableRetry = TimeSpan.FromHours(6);

    public string Barcode { get; set; } = null!;

    /// <summary>Which source came up empty.</summary>
    public string Source { get; set; } = ProductCatalogSources.OpenFoodFacts;

    public DateTimeOffset FirstMissedAt { get; set; }

    /// <summary>When the source was last actually asked, or null while the miss has only ever been
    /// a local one. Distinct from <see cref="FirstMissedAt"/> because a miss is recorded the moment
    /// a scan fails to resolve, which is long before anything goes out over the network - and on a
    /// deployment with the live filler switched off, nothing ever does.</summary>
    public DateTimeOffset? LastAttemptedAt { get; set; }

    /// <summary>How many times the source has confirmed it does not have this product.</summary>
    public int Attempts { get; set; }

    /// <summary>When it is worth asking again, or null meaning never.</summary>
    public DateTimeOffset? RetryAfter { get; set; }

    public static ProductCatalogMiss Create(string barcode, string source, DateTimeOffset now) => new()
    {
        Barcode = barcode,
        Source = source,
        FirstMissedAt = now,
        Attempts = 0,

        // Eligible immediately: the scan that created this row proved somebody wants the answer,
        // and the first ask is the one most likely to succeed.
        RetryAfter = now,
    };

    /// <summary>True when the source may be asked about this barcode now.</summary>
    public bool MayQuery(DateTimeOffset now) => RetryAfter is { } due && due <= now;

    /// <summary>The source answered, and it does not have this product.</summary>
    public void RecordAbsent(DateTimeOffset now)
    {
        Attempts++;
        LastAttemptedAt = now;
        RetryAfter = Attempts <= Backoff.Length ? now + Backoff[Attempts - 1] : null;
    }

    /// <summary>The source could not be reached, or answered with something that is not an answer.
    /// Retried soon and without consuming an attempt, so an outage cannot settle a real product as
    /// permanently absent.</summary>
    public void RecordUnreachable(DateTimeOffset now)
    {
        LastAttemptedAt = now;
        RetryAfter = now + UnreachableRetry;
    }
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ─────────────── Both blocks are
// already present in Guild.Infrastructure/Persistence/MicroserviceContext.cs.
