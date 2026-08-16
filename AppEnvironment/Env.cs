using System.Text;
using Npgsql;
using static System.Environment;
using NSec.Cryptography;

namespace AppEnvironment;

public static class Env
{
    public static readonly RabbitMQConfig RabbitMq = new RabbitMQConfig()
    {
        HostName = GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost",
        Port = int.Parse(GetEnvironmentVariable("RABBITMQ_PORT") ?? "5672"),
        UserName = GetEnvironmentVariable("RABBITMQ_USERNAME") ?? "admin",
        Password = GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "admin"
    };
    
    public static string SentryUrl => GetEnvironmentVariable("SENTRY_URL") ?? string.Empty;
    
    public static readonly FederationConfiguration Federation = new();
    
    public static readonly ScyllaConfig Scylla = new();

    public static readonly DatabaseConfig Database = new();
    
    public static readonly RedisConfig Redis = new();
    
    // The SFU has no entry here on purpose.
    public static readonly MicrosoftGraph MicrosoftGraph = new();
    public static readonly AuthConfiguration AuthConfiguration = new();
    
    public static readonly MessagingConfiguration MessagingConfiguration = new();

    public static string PersonalAccessToken => GetEnvironmentVariable("PERSONAL_ACCESS_TOKEN") ?? string.Empty;
    
    public static string GoogleServiceAccountJsonBase64 => GetEnvironmentVariable("GOOGLE_SERVICE_ACCOUNT_JSON_BASE_64") ?? string.Empty;
    public static string FireBaseServiceAccountJsonBase64 => GetEnvironmentVariable("FIREBASE_SEVRICE_ACCOUNT_JSON_BASE_64") ?? string.Empty;
    
    public static readonly StorageConfiguration StorageConfiguration = new();

    public static GeneralConfiguration GeneralConfiguration = new();

    public static readonly SteamConfiguration Steam = new();

    public static readonly IsleConfiguration Isle = new();

    public static readonly DiscordImportConfiguration DiscordImport = new();

    public static readonly ApnsConfiguration Apns = new();

    public static readonly VapidConfiguration Vapid = new();

    public static readonly AccountDeletionConfiguration AccountDeletion = new();

    public static readonly HouseholdConfiguration Household = new();

    public static readonly RetentionConfiguration Retention = new();

    public static readonly PrivacyConfiguration Privacy = new();

    public static readonly LegalDocumentConfiguration Legal = new();

    public static readonly DataExportConfiguration DataExport = new();

    public static readonly SagaDeadlineConfiguration SagaDeadlines = new();

    public static readonly TelemetryConsentConfiguration TelemetryConsent = new();

    public static readonly UnfurlConfiguration Unfurl = new();

    public static readonly ProductCatalogConfiguration ProductCatalog = new();

    public static readonly LicenseConfiguration License = new();

}

/// <summary>
/// Whether this instance is somebody's own server or the hosted product, and what its operator will
/// let their own hardware do.
/// </summary>
public class LicenseConfiguration
{
    /// <summary>Somebody's own server. The default, and the mode the installers write.</summary>
    public const string SelfHost = "selfhost";

    /// <summary>The hosted product, where billing decides.</summary>
    public const string Hosted = "hosted";

    public string Mode { get; set; } =
        GetEnvironmentVariable("LICENSE_MODE")?.Trim() is { Length: > 0 } mode ? mode : SelfHost;

    public bool IsSelfHost => string.Equals(Mode, SelfHost, StringComparison.OrdinalIgnoreCase);

    public bool IsHosted => string.Equals(Mode, Hosted, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where Billing lives.</summary>
    public string BillingServiceUrl { get; set; } = GetEnvironmentVariable("BILLING_SERVICE_URL") ?? string.Empty;

    // ── Stripe ───────────────────────────────────────────────────────────────
    //
    // None of the three has a compiled-in default. All of them come from the environment, and an
    // unset one means the corresponding surface is off rather than quietly working against somebody
    // else's account. See docs/specs/monetization-stripe-architecture.md section 10.
    //
    // An earlier revision shipped sandbox fallbacks for the secret key and the publishable key, to
    // save an operator a line of configuration. Two things killed it, and both are worth recording so
    // it does not come back as a convenience:
    //
    //   * GitHub push protection rejected the secret key outright, which is the correct behaviour and
    //     was the cheapest possible way to find out. A test-mode key cannot move money, but it is
    //     still full API access to that account for anybody reading the source.
    //   * More seriously, a default is a value that is used when somebody forgets. Once live keys
    //     exist, a missing variable would no longer fail: checkout would complete, cards would
    //     tokenise, and no money would ever arrive. That failure is invisible from inside the running
    //     system, which makes it exactly the wrong thing to make convenient.
    //
    // The webhook signing secret was never given one, for a third and sharper reason that still
    // stands: the webhook endpoint is anonymous, so the signature is the only thing between it and
    // the internet. A published default would let anybody who had read this file send a forged but
    // correctly signed customer.subscription.created, and re-reading the live object (architecture
    // section 5) would not save it, because a published secret key lets the same person create a real
    // sandbox subscription for us to go and read. That is an authentication bypass of billing rather
    // than a no-money-moves inconvenience.
    //
    // TestModeStripeCredentials below still exists and still matters: it names anything that is
    // test-mode or absent on a hosted instance, which is now the only way an operator learns that
    // half of this is unconfigured.

    public string StripeSecretKey { get; set; } =
        GetEnvironmentVariable("STRIPE_SECRET_KEY")?.Trim() ?? string.Empty;

    /// <summary>
    /// The half of the Stripe pair that is safe to hand a client, and is meant to be handed to one.
    /// </summary>
    public string StripePublishableKey { get; set; } =
        GetEnvironmentVariable("STRIPE_PUBLISHABLE_KEY")?.Trim() ?? string.Empty;

    /// <summary>The shared secret Stripe signs webhook deliveries with.</summary>
    public string StripeWebhookSecret { get; set; } =
        GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET")?.Trim() ?? string.Empty;

    /// <summary>Whether webhook deliveries can be authenticated at all.</summary>
    public bool IsStripeWebhookConfigured => !string.IsNullOrWhiteSpace(StripeWebhookSecret);

    /// <summary>
    /// Whether anything in <see cref="Hosted"/> can actually answer "what has this guild paid for".
    /// </summary>
    public bool IsBillingConfigured =>
        !string.IsNullOrWhiteSpace(BillingServiceUrl) || !string.IsNullOrWhiteSpace(StripeSecretKey);

    /// <summary>Whether this instance can talk to Stripe at all.</summary>
    public bool IsStripeConfigured => !string.IsNullOrWhiteSpace(StripeSecretKey);

    /// <summary>Stripe's own marker for a test-mode key, in both halves of the pair.</summary>
    private const string TestKeyMarker = "_test_";

    /// <summary>
    /// Which Stripe credentials still need an operator's attention, by environment variable name -
    /// the keys when they are still test-mode, the webhook secret when it is absent.
    /// </summary>
    public IReadOnlyList<string> TestModeStripeCredentials
    {
        get
        {
            var names = new List<string>(3);

            if (StripeSecretKey.Contains(TestKeyMarker, StringComparison.Ordinal))
                names.Add("STRIPE_SECRET_KEY");

            if (StripePublishableKey.Contains(TestKeyMarker, StringComparison.Ordinal))
                names.Add("STRIPE_PUBLISHABLE_KEY");

            // Reported when absent rather than when test-mode, because there is no such thing as a
            // test-mode signing secret to detect - a live one and a sandbox one are both whsec_ and
            // both unguessable.
            if (!IsStripeWebhookConfigured)
                names.Add("STRIPE_WEBHOOK_SECRET");

            return names;
        }
    }

    // ── Operator ceilings ────────────────────────────────────────────────────

    /// <summary>Hard cap on people in one voice room, whatever the guild's plan says. A number.</summary>
    public string VoiceMaxParticipants { get; set; } =
        GetEnvironmentVariable("VOICE_MAX_PARTICIPANTS") ?? string.Empty;

    /// <summary>Hard cap on published video quality.</summary>
    public string VoiceVideoCeiling { get; set; } =
        GetEnvironmentVariable("VOICE_VIDEO_CEILING") ?? string.Empty;

    /// <summary>Hard cap on a single upload, in bytes. A number.</summary>
    public string StorageUploadMaxBytes { get; set; } =
        GetEnvironmentVariable("STORAGE_UPLOAD_MAX_BYTES") ?? string.Empty;

    /// <summary>The ceilings keyed by entitlement key name, in the shape
    /// <c>Echo.Entitlements.Sources.OperatorCeilings.Parse</c> takes. Names rather than a typed key
    /// for the reason above; they are a stable public contract on that side.</summary>
    public IReadOnlyDictionary<string, string?> OperatorCeilings => new Dictionary<string, string?>
    {
        ["voice.max_participants"] = VoiceMaxParticipants,
        ["voice.video_ceiling"] = VoiceVideoCeiling,
        ["storage.upload_max_bytes"] = StorageUploadMaxBytes,
    };

    /// <summary>
    /// Refuses to start on a license mode that cannot work, and is called by the gateway before
    /// anything else.
    /// </summary>
    public void EnsureConfigured()
    {
        if (!IsSelfHost && !IsHosted)
        {
            throw new InvalidOperationException(
                $"LICENSE_MODE is '{Mode}', which is neither '{SelfHost}' nor '{Hosted}'. "
                + $"Leave it unset for '{SelfHost}', which is the default and needs no configuration.");
        }

        if (IsHosted && !IsBillingConfigured)
        {
            throw new InvalidOperationException(
                $"LICENSE_MODE is '{Hosted}' but neither BILLING_SERVICE_URL nor STRIPE_SECRET_KEY is set, "
                + "so nothing can say what any guild has paid for and every entitlement would resolve to "
                + $"unlimited. Configure billing, or leave LICENSE_MODE unset for '{SelfHost}'.");
        }
    }
}

/// <summary>The shared barcode-to-product catalog behind the pantry scanner.</summary>
public class ProductCatalogConfiguration
{
    /// <summary>
    /// Whether this instance may ask the live source about barcodes the local catalog could not
    /// answer - both inline on a scan and on the background sweep.
    /// </summary>
    public bool LiveFillEnabled { get; set; } =
        GetEnvironmentVariable("PRODUCT_CATALOG_LIVE_FILL_ENABLED")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true;

    /// <summary>Contact address put in the outbound User-Agent.</summary>
    public string ContactEmail { get; set; } =
        GetEnvironmentVariable("PRODUCT_CATALOG_CONTACT_EMAIL") ?? "hello@alpinebits.ch";

    /// <summary>Where lookups go.</summary>
    public string ApiBaseUrl { get; set; } =
        GetEnvironmentVariable("PRODUCT_CATALOG_API_BASE_URL") ?? "https://world.openfoodfacts.org/";

    /// <summary>
    /// Sent on every outbound lookup, in the exact shape the source's documentation specifies:
    /// <c>AppName/Version (ContactEmail)</c>.
    /// </summary>
    public string UserAgent =>
        GetEnvironmentVariable("PRODUCT_CATALOG_USER_AGENT")
        ?? $"VentaPantry/1.0 ({(string.IsNullOrWhiteSpace(ContactEmail) ? "unconfigured" : ContactEmail)})";

    /// <summary>
    /// Sustained lookups per minute for the whole instance, scans and sweep together.
    /// </summary>
    public int RequestsPerMinute { get; set; } =
        int.TryParse(GetEnvironmentVariable("PRODUCT_CATALOG_REQUESTS_PER_MINUTE"), out var r) ? r : 10;

    /// <summary>
    /// How many tokens the bucket holds, and therefore how many scans in a row can resolve inline
    /// before the rest fall through to the sweep.
    /// </summary>
    public int BurstCapacity { get; set; } =
        int.TryParse(GetEnvironmentVariable("PRODUCT_CATALOG_BURST_CAPACITY"), out var c) ? c : 4;

    /// <summary>Barcodes the sweep asks about per tick.</summary>
    public int FillBatchSize { get; set; } =
        int.TryParse(GetEnvironmentVariable("PRODUCT_CATALOG_FILL_BATCH_SIZE"), out var b) ? b : 5;

    /// <summary>
    /// Budget for a lookup made inline on a scan, where somebody is holding a phone and waiting.
    /// </summary>
    public TimeSpan InlineTimeout { get; set; } =
        TimeSpan.FromMilliseconds(
            int.TryParse(GetEnvironmentVariable("PRODUCT_CATALOG_INLINE_TIMEOUT_MS"), out var i) ? i : 1000);

    /// <summary>Budget for a lookup made on the sweep, where nothing waits on it.</summary>
    public TimeSpan RequestTimeout { get; set; } =
        TimeSpan.FromSeconds(
            int.TryParse(GetEnvironmentVariable("PRODUCT_CATALOG_REQUEST_TIMEOUT_SECONDS"), out var t) ? t : 5);

    // ── Automatic bulk import ────────────────────────────────────────────────

    /// <summary>
    /// Whether this instance downloads and loads the published product exports by itself, on a
    /// schedule, instead of an operator running <c>deploy/off-catalog-extract.sh</c> and posting
    /// the result.
    /// </summary>
    public bool AutoImportEnabled { get; set; } =
        GetEnvironmentVariable("PRODUCT_CATALOG_AUTO_IMPORT_ENABLED")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>Which databases the automatic import covers, comma-separated.</summary>
    public string AutoImportSources { get; set; } =
        GetEnvironmentVariable("PRODUCT_CATALOG_AUTO_IMPORT_SOURCES")
        ?? "openbeautyfacts,openproductsfacts";

    /// <summary>
    /// How old the newest row from a database may get before that database is re-imported.
    /// </summary>
    public TimeSpan AutoImportInterval { get; set; } =
        TimeSpan.FromDays(
            int.TryParse(GetEnvironmentVariable("PRODUCT_CATALOG_AUTO_IMPORT_INTERVAL_DAYS"), out var d)
                ? d
                : 30);

    /// <summary>The UTC hour the import is allowed to start in.</summary>
    public int AutoImportHourUtc { get; set; } =
        int.TryParse(GetEnvironmentVariable("PRODUCT_CATALOG_AUTO_IMPORT_HOUR_UTC"), out var h)
            ? Math.Clamp(h, 0, 23)
            : 3;

    /// <summary>
    /// Countries a product must be tagged with to be kept, comma-separated, matching the source's
    /// own <c>countries_tags</c> vocabulary.
    /// </summary>
    public string AutoImportCountries { get; set; } =
        GetEnvironmentVariable("PRODUCT_CATALOG_AUTO_IMPORT_COUNTRIES")
        ?? "en:switzerland,en:germany,en:austria,en:france";

    /// <summary>
    /// How long to pause between committed batches, to keep a refresh from being the heaviest thing
    /// the database is doing.
    /// </summary>
    public TimeSpan AutoImportBatchPause { get; set; } =
        TimeSpan.FromMilliseconds(
            int.TryParse(GetEnvironmentVariable("PRODUCT_CATALOG_AUTO_IMPORT_BATCH_PAUSE_MS"), out var p)
                ? p
                : 50);
}

/// <summary>Link-preview generation (docs/specs/message-previews.md).</summary>
public class UnfurlConfiguration
{
    /// <summary>Master switch.</summary>
    public bool Enabled { get; set; } =
        GetEnvironmentVariable("UNFURL_ENABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true;

    /// <summary>Whether the fetcher may dial private/loopback addresses.</summary>
    public bool AllowPrivateTargets { get; set; } =
        GetEnvironmentVariable("UNFURL_ALLOW_PRIVATE_TARGETS")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>Total budget for one page fetch.</summary>
    public TimeSpan FetchTimeout { get; set; } =
        TimeSpan.FromSeconds(int.TryParse(GetEnvironmentVariable("UNFURL_FETCH_TIMEOUT_SECONDS"), out var t) ? t : 5);

    /// <summary>How many redirect hops to follow, each re-validated against the SSRF guard.</summary>
    public int MaxRedirects { get; set; } =
        int.TryParse(GetEnvironmentVariable("UNFURL_MAX_REDIRECTS"), out var r) ? r : 5;

    /// <summary>Hard cap on an HTML body, enforced while reading rather than from Content-Length -
    /// which a hostile origin simply lies about.</summary>
    public int MaxHtmlBytes { get; set; } =
        int.TryParse(GetEnvironmentVariable("UNFURL_MAX_HTML_BYTES"), out var h) ? h : 2 * 1024 * 1024;

    /// <summary>Hard cap on a preview image before decoding.</summary>
    public int MaxImageBytes { get; set; } =
        int.TryParse(GetEnvironmentVariable("UNFURL_MAX_IMAGE_BYTES"), out var i) ? i : 8 * 1024 * 1024;

    /// <summary>Longest edge of the re-encoded preview image.</summary>
    public int MaxImageEdge { get; set; } =
        int.TryParse(GetEnvironmentVariable("UNFURL_MAX_IMAGE_EDGE"), out var e) ? e : 1280;

    /// <summary>
    /// Refuse to decode an image larger than this many megapixels regardless of how few bytes it
    /// arrived in.
    /// </summary>
    public int MaxImageMegapixels { get; set; } =
        int.TryParse(GetEnvironmentVariable("UNFURL_MAX_IMAGE_MEGAPIXELS"), out var m) ? m : 50;

    /// <summary>Floor and ceiling for the cache TTL derived from the origin's own Cache-Control.
    /// A page claiming a one-second lifetime should not make us re-fetch on every mention, and one
    /// claiming a year should not pin a stale card forever.</summary>
    public TimeSpan MinCacheTtl { get; set; } =
        TimeSpan.FromMinutes(int.TryParse(GetEnvironmentVariable("UNFURL_MIN_CACHE_TTL_MINUTES"), out var mn) ? mn : 15);

    public TimeSpan MaxCacheTtl { get; set; } =
        TimeSpan.FromHours(int.TryParse(GetEnvironmentVariable("UNFURL_MAX_CACHE_TTL_HOURS"), out var mx) ? mx : 24);

    public TimeSpan DefaultCacheTtl { get; set; } =
        TimeSpan.FromHours(int.TryParse(GetEnvironmentVariable("UNFURL_DEFAULT_CACHE_TTL_HOURS"), out var d) ? d : 6);

    /// <summary>How long a failure is remembered, so a dead link is not re-fetched every time
    /// somebody quotes it.</summary>
    public TimeSpan FailureCacheTtl { get; set; } =
        TimeSpan.FromMinutes(int.TryParse(GetEnvironmentVariable("UNFURL_FAILURE_CACHE_TTL_MINUTES"), out var f) ? f : 10);

    /// <summary>Concurrent in-flight fetches per origin host.</summary>
    public int MaxConcurrentPerHost { get; set; } =
        int.TryParse(GetEnvironmentVariable("UNFURL_MAX_CONCURRENT_PER_HOST"), out var c) ? c : 2;

    /// <summary>Concurrent fetches across all hosts, bounding the service's own resource use.</summary>
    public int MaxConcurrentTotal { get; set; } =
        int.TryParse(GetEnvironmentVariable("UNFURL_MAX_CONCURRENT_TOTAL"), out var ct) ? ct : 32;

    /// <summary>Sent on every outbound request.</summary>
    public string UserAgent { get; set; } =
        GetEnvironmentVariable("UNFURL_USER_AGENT")
        ?? $"EchoBot/1.0 (+{GetEnvironmentVariable("INSTANCE_URL") ?? "https://api.venta.gg"}/bot)";

    /// <summary>Public base URL that <c>proxy_url</c> values are built from.</summary>
    public string PublicBaseUrl { get; set; } =
        GetEnvironmentVariable("UNFURL_PUBLIC_BASE_URL")
        ?? GetEnvironmentVariable("INSTANCE_URL")
        ?? "https://api.venta.gg";

    /// <summary>Key prefix for stored preview media in the shared bucket.</summary>
    public string MediaPrefix { get; set; } = GetEnvironmentVariable("UNFURL_MEDIA_PREFIX") ?? "previews";
}

public class RabbitMQConfig
{
    public string HostName { get; set; }
    public int Port { get; set; }
    public string UserName { get; set; }
    public string Password { get; set; }
    
}

public class ScyllaConfig
{
    public string Host { get; set; } = GetEnvironmentVariable("SCYLLA_HOST") ?? "localhost";
    public int Port { get; set; } = int.Parse(GetEnvironmentVariable("SCYLLA_PORT") ?? "9042");
    public string UserName { get; set; } = GetEnvironmentVariable("SCYLLA_USERNAME") ?? "scylla";
    public string Password { get; set; } = GetEnvironmentVariable("SCYLLA_PASSWORD") ?? "scylla";
}

public class DatabaseConfig
{
    public bool Enabled { get; set; }
    public string DatabaseHostname { get; set; } = Environment.GetEnvironmentVariable("DATABASE_HOSTNAME") ?? "localhost";
    public string DatabasePort { get; set; } = Environment.GetEnvironmentVariable("DATABASE_PORT") ?? "5433";
    public string DatabaseName { get; set; } = Environment.GetEnvironmentVariable("DATABASE_NAME") ?? "postgres";
    public string? DatabaseSchemaName { get; set; } = "public";
    public string DatabaseUsername { get; set; } = Environment.GetEnvironmentVariable("DATABASE_USERNAME") ?? "postgres";
    public string DatabasePassword { get; set; } = Environment.GetEnvironmentVariable("DATABASE_PASSWORD") ?? "postgres";

    public int PoolSize { get; set; } = 50;

    public string ConnectionString(int pool = -1, bool usePooling = true)
    {
        var poolSize = pool == -1 ? PoolSize : pool;
        NpgsqlConnectionStringBuilder builder = new NpgsqlConnectionStringBuilder()
        {
            Host = DatabaseHostname,
            Port = int.Parse(DatabasePort),
            Database = DatabaseName,
            Username = DatabaseUsername,
            Password = DatabasePassword,
            Pooling = usePooling,
            MaxPoolSize = poolSize,
            NoResetOnClose = true,
            MaxAutoPrepare = 0,
            ConnectionIdleLifetime = 20,
        };
        return builder.ConnectionString;
    }
}

public class RedisConfig
{
    public string Host { get; set; } = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "localhost";
    
    public string UserName { get; set; } = Environment.GetEnvironmentVariable("REDIS_USERNAME") ?? "";
    public string Password { get; set; } = Environment.GetEnvironmentVariable("REDIS_PASSWORD") ?? "devpassword";
    public string Port { get; set; } = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";

    public string ConnectionString => $"{Host}:{Port},password={Password}";

}



public class MicrosoftGraph
{
    public string ClientId { get; set; } = Environment.GetEnvironmentVariable("MICROSOFT_GRAPH_CLIENT_ID") ?? "";
    public string ClientSecret { get; set; } = Environment.GetEnvironmentVariable("MICROSOFT_GRAPH_CLIENT_SECRET") ?? "";
}

public class AuthConfiguration
{
    /// <summary>The label the SSO site and the OIDC issuer live under. See docs/specs/sso.md.</summary>
    public const string AuthSiteLabel = "auth";

    public bool RequireUserEmailVerification { get; set; } =
        (Environment.GetEnvironmentVariable("AUTH_REQUIRE_USER_EMAIL_VERIFICATION")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true);


    public string IdentitySecretPassword { get; set; } = GetEnvironmentVariable("IDENTITY_KEY_PASSWORD") ?? "devpassword";
    public string IdentitySigningCert { get; set; } = GetEnvironmentVariable("IDENTITY_SIGNING_CERT") ?? string.Empty;

    /// <summary>
    /// The OIDC issuer: the value Identity stamps into every <c>iss</c> claim, and the identity
    /// every partner site configures.
    /// </summary>
    public string IssuerUrl { get; set; } =
        GetEnvironmentVariable("AUTH_ISSUER_URL")
        ?? InstanceHosts.DeriveSiblingUrl(AuthSiteLabel, GetEnvironmentVariable("INSTANCE_URL") ?? "https://api.venta.gg");

    /// <summary>The OIDC client allowlist, as a JSON array.</summary>
    public string Clients { get; set; } = GetEnvironmentVariable("AUTH_CLIENTS") ?? string.Empty;

    /// <summary>The environment variable a confidential client's secret is read from.</summary>
    public static string ClientSecretVariable(string clientId)
    {
        var normalised = new string([.. clientId.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_')]);

        return $"AUTH_CLIENT_SECRET_{normalised}";
    }

    public string? ClientSecret(string clientId) => GetEnvironmentVariable(ClientSecretVariable(clientId));
}

public class GeneralConfiguration
{
    public bool IsUserHashGenerationEnabled { get; set; } = (GetEnvironmentVariable("IS_USER_HASH_GENERATION_ENABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true);
    public string InstanceUrl { get; set; } = GetEnvironmentVariable("INSTANCE_URL") ?? "https://api.venta.gg";

    /// <summary>
    /// <see cref="InstanceUrl"/> with any trailing slash removed, for composing a public URL by
    /// appending a rooted path to it.
    /// </summary>
    public string InstanceBaseUrl => InstanceUrl.TrimEnd('/');
}

public class SteamConfiguration
{
    /// <summary>Public base URL Steam redirects back to after authentication.</summary>
    public string PublicBaseUrl { get; set; } =
        GetEnvironmentVariable("STEAM_PUBLIC_BASE_URL")
        ?? GetEnvironmentVariable("INSTANCE_URL")
        ?? "https://api.venta.gg";

    /// <summary>
    /// Public, browser-facing path Steam redirects back to - this MUST include the YARP gateway
    /// prefix (/api/v1/identity), which the gateway strips before the request reaches the Identity
    /// service. The controller itself listens on /api/v1/authentication/steam/callback.
    /// </summary>
    public string PublicCallbackPath { get; set; } =
        GetEnvironmentVariable("STEAM_PUBLIC_CALLBACK_PATH")
        ?? "/api/v1/identity/authentication/steam/callback";

    /// <summary>
    /// Target the callback redirects the browser to once the flow finishes (deep link or web URL).
    /// </summary>
    public string ClientReturnUrl { get; set; } =
        GetEnvironmentVariable("STEAM_CLIENT_RETURN_URL") ?? "venta://steam-auth";

    /// <summary>Optional Steam Web API key.</summary>
    public string WebApiKey { get; set; } = GetEnvironmentVariable("STEAM_WEB_API_KEY") ?? string.Empty;
}

public class IsleConfiguration
{
    /// <summary>Host running the Isle dedicated server; serves both the bridge plugin and RCON.</summary>
    public string IpAddress { get; set; } = GetEnvironmentVariable("ISLE_IP_ADDRESS") ?? "10.0.0.0";

    /// <summary>HTTP port of the IsleBridge plugin (chat / event / stats streams and commands).</summary>
    public int BridgePort { get; set; } = int.Parse(GetEnvironmentVariable("ISLE_BRIDGE_PORT") ?? "8080");

    public int RconPort { get; set; } = int.Parse(GetEnvironmentVariable("ISLE_RCON_PORT") ?? "8888");

    public string RconPassword { get; set; } = GetEnvironmentVariable("RCON_PASSWORD") ?? string.Empty;

    public string BridgeBaseAddress => $"http://{IpAddress}:{BridgePort}";
}

public class MessagingConfiguration
{
    public bool UseScyllaDb { get; set; } = (GetEnvironmentVariable("USE_SCYLLA_DB")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true);
}

public class StorageConfiguration
{
    public string BucketName { get; set;  } = GetEnvironmentVariable("BUCKET_NAME") ?? "echo-chat";
    public string AccessKey { get; set; } = GetEnvironmentVariable("ACCESS_KEY_ID") ?? "mock_access_key";
    public string SecretKey { get; set; } = GetEnvironmentVariable("SECRET_ACCESS_KEY") ?? "mock_secret_key";
    public string PublicUrl { get; set; } = GetEnvironmentVariable("PUBLIC_URL") ?? "https://storage.googleapis.com";
    public string ServiceUrl { get; set; } = GetEnvironmentVariable("SERVICE_URL") ?? "https://storage.googleapis.com";
    public bool UseServiceUrl { get; set; } = (GetEnvironmentVariable("USE_SERVICE_URL")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true);

    /// <summary>
    /// Whether the object store is reached over plain HTTP - which is what <c>compose.yaml</c> and
    /// the self-hosting installers ship (<c>SERVICE_URL: "http://minio:9000"</c>).
    /// </summary>
    public bool ServiceUrlIsPlainHttp =>
        UseServiceUrl && ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);


    public string Region { get; set; } = GetEnvironmentVariable("REGION") ?? "us-east-1";
}


public class FederationConfiguration
{
    public string InstanceName { get; set; } = GetEnvironmentVariable("INSTANCE_NAME") ?? "Venta.gg";
    public string Version { get; set; } = GetEnvironmentVariable("VERSION") ?? "1.0.0";
    public byte[] PrivateKey { get; set; } =  Array.Empty<byte>();
    public byte[] PublicKey { get; set; } = Array.Empty<byte>();

    public FederationConfiguration()
    {
        
        var privKeyB64 = GetEnvironmentVariable("FEDERATION_PRIVATE_KEY_BASE_64");
        var pubKeyB64 = GetEnvironmentVariable("FEDERATION_PUBLIC_KEY_BASE_64");
        

        if (!string.IsNullOrEmpty(privKeyB64) && !string.IsNullOrEmpty(pubKeyB64))
        {
            PrivateKey = Convert.FromBase64String(privKeyB64);
            PublicKey = Convert.FromBase64String(pubKeyB64);
            Console.WriteLine("Loaded federation keys from environment variables");
        }
        
        if (PrivateKey.Length == 0 || PublicKey.Length == 0)
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var key = Key.Create(algorithm, new KeyCreationParameters
            {
                ExportPolicy = KeyExportPolicies.AllowPlaintextExport
            });

            PrivateKey =(key.Export(KeyBlobFormat.PkixPrivateKeyText));
            PublicKey = (key.PublicKey.Export(KeyBlobFormat.PkixPublicKeyText));
        }
    }
}
/// <summary>
/// Direct-APNs credentials for the one push path FCM can't cover: VoIP/CallKit (see
/// Messaging.Application/Services/CallPushService.cs).
/// </summary>
public class ApnsConfiguration
{
    /// <summary>
    /// Plain app bundle id - NOT the VoIP topic. dotAPNS's ApnsClient.GetTopic() appends ".voip"
    /// itself for ApplePushType.Voip pushes (CallPushService.SendVoipAsync), so a pre-suffixed
    /// value here would double up into "...voip.voip" and Apple would reject every push.
    /// </summary>
    public string BundleId { get; set; } = GetEnvironmentVariable("APNS_BUNDLE_ID") ?? "gg.venta.mobile";
    public string KeyId { get; set; } = GetEnvironmentVariable("APNS_KEY_ID") ?? string.Empty;
    public string TeamId { get; set; } = GetEnvironmentVariable("APNS_TEAM_ID") ?? string.Empty;
    public string AuthKeyBase64 { get; set; } = GetEnvironmentVariable("APNS_AUTH_KEY_BASE_64") ?? string.Empty;

    /// <summary>
    /// A device's push token is only valid against the APNs gateway that issued it - a debug/
    /// Xcode-run build (whose Runner.entitlements sets aps-environment=development) registers a
    /// sandbox token, and Apple's production gateway silently rejects those (BadDeviceToken) rather
    /// than delivering them. dotAPNS defaults every push to production
    /// (ApplePush.SendToDevelopmentServer() must be called explicitly for sandbox) - see
    /// CallPushService.SendVoipAsync.
    /// </summary>
    public bool UseSandbox { get; set; } =
        GetEnvironmentVariable("APNS_USE_SANDBOX")?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;

    public string AuthKeyContent => string.IsNullOrEmpty(AuthKeyBase64)
        ? string.Empty
        : Encoding.UTF8.GetString(Convert.FromBase64String(AuthKeyBase64));
}

/// <summary>
/// The VAPID keypair every Web Push send is signed with (RFC 8292), and the browser client's
/// <c>applicationServerKey</c>.
/// </summary>
public class VapidConfiguration
{
    /// <summary>Uncompressed P-256 public key, base64url, 87 chars.</summary>
    public string PublicKey { get; set; } = GetEnvironmentVariable("VAPID_PUBLIC_KEY") ?? string.Empty;

    /// <summary>Raw 32-byte P-256 private scalar, base64url.</summary>
    public string PrivateKey { get; set; } = GetEnvironmentVariable("VAPID_PRIVATE_KEY") ?? string.Empty;

    /// <summary>
    /// The <c>sub</c> claim of the VAPID JWT: a <c>mailto:</c> or <c>https:</c> URI a push service
    /// operator can use to reach whoever runs this instance.
    /// </summary>
    public string Subject { get; set; } =
        GetEnvironmentVariable("VAPID_SUBJECT")
        ?? GetEnvironmentVariable("INSTANCE_URL")
        ?? "https://api.venta.gg";

    /// <summary>How long a signed JWT is valid.</summary>
    public TimeSpan TokenLifetime { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("VAPID_TOKEN_LIFETIME_SECONDS") ?? (12 * 60 * 60).ToString()));

    /// <summary>
    /// The <c>TTL</c> header on every push: how long the push service may hold a message for a device
    /// that is offline. Required by RFC 8030 - omitting it is a 400 from some services.
    /// </summary>
    public TimeSpan MessageTtl { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("VAPID_MESSAGE_TTL_SECONDS") ?? (4 * 60 * 60).ToString()));

    /// <summary>Whether this instance can send Web Push at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>Grace-period/sweep tuning for the "delete my account" flow (Identity's
/// ApplicationUser.RequestDeletion + AccountDeletionPurgeSweepService). Defaults match a
/// realistic 30-day cancellable window; E2E tests override both to single-digit seconds so the
/// real scheduled-purge path can be exercised without an actual multi-day wait.</summary>
public class AccountDeletionConfiguration
{
    public TimeSpan GracePeriod { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("ACCOUNT_DELETION_GRACE_PERIOD_SECONDS") ?? (30 * 24 * 60 * 60).ToString()));

    public TimeSpan SweepInterval { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("ACCOUNT_DELETION_SWEEP_INTERVAL_SECONDS") ?? (5 * 60).ToString()));
}

/// <summary>
/// Sweep tuning for Guild's <c>HouseholdReconcileService</c>, which generates chore occurrences and
/// bill occurrences, sends chore reminders, pantry expiry and low-stock warnings, cooking reminders
/// and maintenance service warnings, expires decisions, and tidies lapsed guest roles.
/// </summary>
public class HouseholdConfiguration
{
    public TimeSpan SweepInterval { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("HOUSEHOLD_SWEEP_INTERVAL_SECONDS") ?? (5 * 60).ToString()));
}

/// <summary>
/// Data-retention TTLs (T1-8 of docs/specs/privacy.md), swept by Identity's
/// <c>RetentionSweepService</c>.
/// </summary>
public class RetentionConfiguration
{
    /// <summary>How long a login's IP address and user agent are kept before being scrubbed. The
    /// row survives - a user's session list is how they notice a session they did not start, and
    /// deleting it would remove the thing they are meant to look at.</summary>
    public TimeSpan LoginSessionIpAndUserAgent { get; set; } =
        TimeSpan.FromDays(int.Parse(GetEnvironmentVariable("RETENTION_LOGIN_SESSION_IP_DAYS") ?? "90"));

    /// <summary>How long an audit event's IP address is kept.</summary>
    public TimeSpan AuditEventIpAddress { get; set; } =
        TimeSpan.FromDays(int.Parse(GetEnvironmentVariable("RETENTION_AUDIT_EVENT_IP_DAYS") ?? "180"));

    /// <summary>How long a revoked login session row is kept before deletion.</summary>
    public TimeSpan RevokedLoginSession { get; set; } =
        TimeSpan.FromDays(int.Parse(GetEnvironmentVariable("RETENTION_REVOKED_SESSION_DAYS") ?? "180"));

    /// <summary>Gap between sweep passes.</summary>
    public TimeSpan SweepInterval { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("RETENTION_SWEEP_INTERVAL_SECONDS") ?? (6 * 60 * 60).ToString()));

    /// <summary>Rows touched per pass, per category.</summary>
    public int SweepBatchSize { get; set; } =
        int.Parse(GetEnvironmentVariable("RETENTION_SWEEP_BATCH_SIZE") ?? "5000");

    /// <summary>
    /// Whether Messaging's user-set DM retention sweep (T2-22) may delete from the Scylla message
    /// store.
    /// </summary>
    public bool DmScyllaDeleteEnabled { get; set; } =
        GetEnvironmentVariable("RETENTION_DM_SCYLLA_ENABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
}

/// <summary>Privacy-policy knobs that are not per-user settings - currently only the age at which
/// the minor protections of T1-11 stop applying.</summary>
public class PrivacyConfiguration
{
    /// <summary>The age at which an account stops being treated as a minor.</summary>
    public int AgeOfMajority { get; set; } =
        int.Parse(GetEnvironmentVariable("PRIVACY_AGE_OF_MAJORITY") ?? "18");

    /// <summary>
    /// Whether a purged account keeps the single non-identifying <c>WasVerifiedAdult</c> boolean
    /// (T1-9).
    /// </summary>
    public bool RetainWasVerifiedAdult { get; set; } =
        GetEnvironmentVariable("PRIVACY_RETAIN_WAS_VERIFIED_ADULT")?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;

    /// <summary>The statutory window for answering a data-subject request (T1-13).</summary>
    public TimeSpan DataSubjectRequestResponseWindow { get; set; } =
        TimeSpan.FromDays(int.Parse(GetEnvironmentVariable("PRIVACY_DSR_RESPONSE_WINDOW_DAYS") ?? "30"));
}

/// <summary>Where the versioned legal documents (T1-12) live and how their public URLs are
/// built.</summary>
public class LegalDocumentConfiguration
{
    /// <summary>Directory holding <c>manifest.json</c> and the document files.</summary>
    public string DirectoryPath { get; set; } =
        GetEnvironmentVariable("LEGAL_DOCUMENTS_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "legal");

    /// <summary>Public, browser-facing base URL the document routes hang off.</summary>
    public string PublicBaseUrl { get; set; } =
        GetEnvironmentVariable("LEGAL_DOCUMENTS_PUBLIC_BASE_URL")
        ?? ((GetEnvironmentVariable("INSTANCE_URL") ?? "https://api.venta.gg") + "/api/v1/identity/legal/documents");
}

/// <summary>The GDPR Art.</summary>
public class DataExportConfiguration
{
    /// <summary>How long an account must wait between export requests.</summary>
    public TimeSpan RateLimitWindow { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_RATE_LIMIT_SECONDS") ?? (24 * 60 * 60).ToString()));

    /// <summary>How long a finished archive stays downloadable before the sweep marks the row
    /// <c>Expired</c> and deletes the object.</summary>
    public TimeSpan ArtifactTtl { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_ARTIFACT_TTL_SECONDS") ?? (7 * 24 * 60 * 60).ToString()));

    /// <summary>Lifetime of the signed URL the download route redirects to.</summary>
    public TimeSpan DownloadUrlLifetime { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_DOWNLOAD_URL_SECONDS") ?? "300"));

    /// <summary>Gap between expiry sweep passes.</summary>
    public TimeSpan SweepInterval { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_SWEEP_INTERVAL_SECONDS") ?? (6 * 60 * 60).ToString()));

    /// <summary>Rows an expiry sweep pass touches.</summary>
    public int SweepBatchSize { get; set; } =
        int.Parse(GetEnvironmentVariable("DATA_EXPORT_SWEEP_BATCH_SIZE") ?? "500");

    /// <summary>Cap on the messages one export carries out of a single conversation.</summary>
    public int MaxMessagesPerConversation { get; set; } =
        int.Parse(GetEnvironmentVariable("DATA_EXPORT_MAX_MESSAGES_PER_CONVERSATION") ?? "5000");
}

/// <summary>
/// How long Echo's two cross-service privacy sagas (<c>Echo.Sagas.AccountDeletionSaga</c> and
/// <c>Echo.Sagas.ExportUserDataSaga</c>) wait for the last participant before declaring the fan-out
/// stalled.
/// </summary>
public class SagaDeadlineConfiguration
{
    public TimeSpan AccountPurge { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("ACCOUNT_PURGE_SAGA_DEADLINE_SECONDS") ?? (60 * 60).ToString()));

    public TimeSpan DataExport { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_SAGA_DEADLINE_SECONDS") ?? (60 * 60).ToString()));
}

/// <summary>
/// Tuning for the per-service telemetry consent gate (T0-4) - the in-memory mirror that lets <see
/// cref="SentryPrivacy.HasDataCollectionConsent"/>, a synchronous delegate called from inside the
/// Sentry SDK, answer without a Redis round trip on the error path.
/// </summary>
public class TelemetryConsentConfiguration
{
    /// <summary>
    /// How often the tracked ids are re-resolved through the service's privacy-settings cache.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("TELEMETRY_CONSENT_REFRESH_SECONDS") ?? "15"));

    /// <summary>How long a resolved answer is trusted without being re-confirmed.</summary>
    public TimeSpan EntryLifetime { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("TELEMETRY_CONSENT_ENTRY_LIFETIME_SECONDS") ?? "45"));

    /// <summary>Ceiling on how many accounts are tracked at once.</summary>
    public int MaxTrackedUsers { get; set; } =
        int.Parse(GetEnvironmentVariable("TELEMETRY_CONSENT_MAX_TRACKED_USERS") ?? "5000");
}

public class DiscordImportConfiguration
{
    /// <summary>Echo-owned Discord Application's bot token, used to call the real discord.com
    /// API and (for live sync) maintain a Gateway connection. A deployment secret - never
    /// persisted in a DB row. Registered once, by hand, in Discord's Developer Portal.</summary>
    public string BotToken { get; set; } = GetEnvironmentVariable("DISCORD_IMPORT_BOT_TOKEN") ?? string.Empty;

    public string ClientId { get; set; } = GetEnvironmentVariable("DISCORD_IMPORT_CLIENT_ID") ?? string.Empty;

    /// <summary>Public, browser-facing callback path - must include the YARP gateway prefix
    /// (/api/v1/imports), which the gateway strips before the request reaches the Import
    /// service. Configured as this application's OAuth2 redirect URI in Discord's Developer
    /// Portal.</summary>
    public string PublicCallbackPath { get; set; } =
        GetEnvironmentVariable("DISCORD_IMPORT_PUBLIC_CALLBACK_PATH")
        ?? "/api/v1/imports/discord/callback";

    public string PublicBaseUrl { get; set; } =
        GetEnvironmentVariable("DISCORD_IMPORT_PUBLIC_BASE_URL")
        ?? GetEnvironmentVariable("INSTANCE_URL")
        ?? "https://api.venta.gg";

    /// <summary>
    /// Deep link the OAuth callback redirects the browser to once the import job is queued -
    /// same convention as SteamConfiguration.ClientReturnUrl. The client appends ?jobId=... itself.
    /// </summary>
    public string ClientReturnUrl { get; set; } =
        GetEnvironmentVariable("DISCORD_IMPORT_CLIENT_RETURN_URL") ?? "venta://discord-import";
}
