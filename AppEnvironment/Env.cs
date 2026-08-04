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
    
    public static readonly CloudflareConfig CloudflareConfig = new();
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

    public static readonly AccountDeletionConfiguration AccountDeletion = new();

    public static readonly RetentionConfiguration Retention = new();

    public static readonly PrivacyConfiguration Privacy = new();

    public static readonly LegalDocumentConfiguration Legal = new();

    public static readonly DataExportConfiguration DataExport = new();

    public static readonly SagaDeadlineConfiguration SagaDeadlines = new();

    public static readonly TelemetryConsentConfiguration TelemetryConsent = new();

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



public class CloudflareConfig
{
    public string AppName { get; set; } = "echo";
    public string AppId { get; set; } = GetEnvironmentVariable("CLOUDFLARE_APP_ID") ?? "mock_app_id";
    public string ApiToken { get; set; } = GetEnvironmentVariable("CLOUDFLARE_API_TOKEN") ?? "mock_tocken";
}

public class MicrosoftGraph
{
    public string ClientId { get; set; } = Environment.GetEnvironmentVariable("MICROSOFT_GRAPH_CLIENT_ID") ?? "";
    public string ClientSecret { get; set; } = Environment.GetEnvironmentVariable("MICROSOFT_GRAPH_CLIENT_SECRET") ?? "";
}

public class AuthConfiguration
{
    public bool RequireUserEmailVerification { get; set; } = 
        (Environment.GetEnvironmentVariable("AUTH_REQUIRE_USER_EMAIL_VERIFICATION")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true);


    public string IdentitySecretPassword { get; set; } = GetEnvironmentVariable("IDENTITY_KEY_PASSWORD") ?? "devpassword";
    public string IdentitySigningCert { get; set; } = GetEnvironmentVariable("IDENTITY_SIGNING_CERT") ?? string.Empty;
}

public class GeneralConfiguration
{
    public bool IsUserHashGenerationEnabled { get; set; } = (GetEnvironmentVariable("IS_USER_HASH_GENERATION_ENABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true);
    public string InstanceUrl { get; set; } = GetEnvironmentVariable("INSTANCE_URL") ?? "https://api.venta.gg";

}

public class SteamConfiguration
{
    /// <summary>
    /// Public base URL Steam redirects back to after authentication. Must be reachable from the
    /// user's browser. Falls back to the general instance URL when unset.
    /// </summary>
    public string PublicBaseUrl { get; set; } =
        GetEnvironmentVariable("STEAM_PUBLIC_BASE_URL")
        ?? GetEnvironmentVariable("INSTANCE_URL")
        ?? "https://api.venta.gg";

    /// <summary>
    /// Public, browser-facing path Steam redirects back to — this MUST include the YARP gateway
    /// prefix (/api/v1/identity), which the gateway strips before the request reaches the Identity
    /// service. The controller itself listens on /api/v1/authentication/steam/callback.
    /// </summary>
    public string PublicCallbackPath { get; set; } =
        GetEnvironmentVariable("STEAM_PUBLIC_CALLBACK_PATH")
        ?? "/api/v1/identity/authentication/steam/callback";

    /// <summary>
    /// Target the callback redirects the browser to once the flow finishes (deep link or web URL).
    /// The callback appends query params such as ?status=linked or ?status=ok&amp;ticket=...
    /// </summary>
    public string ClientReturnUrl { get; set; } =
        GetEnvironmentVariable("STEAM_CLIENT_RETURN_URL") ?? "venta://steam-auth";

    /// <summary>
    /// Optional Steam Web API key. Not required for the login/link flow; reserved for future
    /// profile enrichment (persona name, avatar).
    /// </summary>
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
/// Messaging.Application/Services/CallPushService.cs). The auth key is an Apple .p8 token-signing
/// key, base64-encoded the same way as the other service-account secrets in this file.
/// </summary>
public class ApnsConfiguration
{
    /// <summary>
    /// Plain app bundle id — NOT the VoIP topic. dotAPNS's ApnsClient.GetTopic() appends ".voip"
    /// itself for ApplePushType.Voip pushes (CallPushService.SendVoipAsync), so a pre-suffixed
    /// value here would double up into "...voip.voip" and Apple would reject every push.
    /// </summary>
    public string BundleId { get; set; } = GetEnvironmentVariable("APNS_BUNDLE_ID") ?? "gg.venta.mobile";
    public string KeyId { get; set; } = GetEnvironmentVariable("APNS_KEY_ID") ?? string.Empty;
    public string TeamId { get; set; } = GetEnvironmentVariable("APNS_TEAM_ID") ?? string.Empty;
    public string AuthKeyBase64 { get; set; } = GetEnvironmentVariable("APNS_AUTH_KEY_BASE_64") ?? string.Empty;

    /// <summary>
    /// A device's push token is only valid against the APNs gateway that issued it — a debug/
    /// Xcode-run build (whose Runner.entitlements sets aps-environment=development) registers a
    /// *sandbox* token, and Apple's production gateway silently rejects those
    /// (BadDeviceToken) rather than delivering them. dotAPNS defaults every push to production
    /// (ApplePush.SendToDevelopmentServer() must be called explicitly for sandbox) — see
    /// CallPushService.SendVoipAsync. Defaults true because every current build being tested
    /// against this backend is a debug build; flip to false (APNS_USE_SANDBOX=false) once real
    /// TestFlight/App Store (Runner-Release.entitlements, aps-environment=production) builds are
    /// what's actually being tested.
    /// </summary>
    public bool UseSandbox { get; set; } =
        GetEnvironmentVariable("APNS_USE_SANDBOX")?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;

    public string AuthKeyContent => string.IsNullOrEmpty(AuthKeyBase64)
        ? string.Empty
        : Encoding.UTF8.GetString(Convert.FromBase64String(AuthKeyBase64));
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
/// Data-retention TTLs (T1-8 of docs/specs/privacy.md), swept by Identity's
/// <c>RetentionSweepService</c>.
///
/// <para><b>Every one of these is a scrub, not a delete, except where it says delete.</b> The audit
/// log is append-only: the event is the record and the IP is the incidental detail, so a row that
/// ages past its window loses its IP and keeps everything else. Deleting the row instead would mean
/// the retention policy silently destroys the evidence trail that exists to detect account
/// takeover.</para>
///
/// <para>Defaults are the ones the spec names. E2E and unit tests override them to single-digit
/// seconds so the real sweep path can be exercised without waiting out a real window.</para>
/// </summary>
public class RetentionConfiguration
{
    /// <summary>How long a login's IP address and user agent are kept before being scrubbed. The
    /// row survives - a user's session list is how they notice a session they did not start, and
    /// deleting it would remove the thing they are meant to look at.</summary>
    public TimeSpan LoginSessionIpAndUserAgent { get; set; } =
        TimeSpan.FromDays(int.Parse(GetEnvironmentVariable("RETENTION_LOGIN_SESSION_IP_DAYS") ?? "90"));

    /// <summary>How long an audit event's IP address is kept. The row itself is kept
    /// <b>forever</b>; only this column ages out.</summary>
    public TimeSpan AuditEventIpAddress { get; set; } =
        TimeSpan.FromDays(int.Parse(GetEnvironmentVariable("RETENTION_AUDIT_EVENT_IP_DAYS") ?? "180"));

    /// <summary>How long a <i>revoked</i> login session row is kept before deletion. Measured from
    /// revocation, not from creation: a session revoked yesterday is still the answer to "what did I
    /// just cut off", regardless of how old the login was.</summary>
    public TimeSpan RevokedLoginSession { get; set; } =
        TimeSpan.FromDays(int.Parse(GetEnvironmentVariable("RETENTION_REVOKED_SESSION_DAYS") ?? "180"));

    /// <summary>Gap between sweep passes. Six hours rather than daily so a deployment that restarts
    /// often still sweeps, and so a badly-set TTL is discovered within a working day.</summary>
    public TimeSpan SweepInterval { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("RETENTION_SWEEP_INTERVAL_SECONDS") ?? (6 * 60 * 60).ToString()));

    /// <summary>Rows touched per pass, per category. Bounded so a first run against a database that
    /// has never been swept cannot hold a transaction open over the whole table; the remainder is
    /// picked up by the next tick.</summary>
    public int SweepBatchSize { get; set; } =
        int.Parse(GetEnvironmentVariable("RETENTION_SWEEP_BATCH_SIZE") ?? "5000");

    /// <summary>
    /// Whether Messaging's user-set DM retention sweep (T2-22) may delete from the <b>Scylla</b>
    /// message store. <b>Off by default</b>, and the only knob in this class that gates a code path
    /// rather than tuning one.
    ///
    /// <para><b>Why a kill switch on one backend.</b> The retention read is a range scan bounded at
    /// both ends by CQL row tuples - <c>(created_at, message_id) &gt; (?, ?)</c> against
    /// <c>(created_at) &lt; (?)</c>, the upper bound written as a one-component tuple because
    /// Cassandra refuses to mix multi-column and single-column relations on the same clustering
    /// column. That shape was written and unit tested against a fake mapper, which can check the
    /// text of a statement but cannot reject it. What the statement selects is what gets deleted, so
    /// a query that is merely accepted-but-wrong on a real node deletes the wrong user messages, and
    /// deleted messages do not come back. The default is therefore "visibly inert": the sweep logs
    /// at startup that DM retention is configured and the Scylla path is disabled, rather than
    /// quietly doing nothing or quietly doing the wrong thing.</para>
    ///
    /// <para>Set <c>RETENTION_DM_SCYLLA_ENABLED=true</c> once the deployment has run
    /// <c>ScyllaDmRetentionRangeDeleteTests</c> (Messaging.Tests, gated on <c>ECHO_TEST_SCYLLA</c>)
    /// against a node of the same version it runs in production. The Postgres/EF path is unaffected
    /// by this flag - it is exercised by the ordinary provider-backed suite.</para>
    /// </summary>
    public bool DmScyllaDeleteEnabled { get; set; } =
        GetEnvironmentVariable("RETENTION_DM_SCYLLA_ENABLED")?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
}

/// <summary>Privacy-policy knobs that are not per-user settings - currently only the age at which
/// the minor protections of T1-11 stop applying.</summary>
public class PrivacyConfiguration
{
    /// <summary>
    /// The age at which an account stops being treated as a minor. Configurable because the
    /// jurisdictional age of majority is not universally 18, and because the digital-consent age
    /// (16 in much of the EU, lower in some member states) is what applies in some deployments.
    ///
    /// <para>Evaluated live from the birth date on every read and every write, never cached onto the
    /// account - which is what makes a birthday rollover unlock the settings by itself instead of
    /// needing a sweep to notice.</para>
    /// </summary>
    public int AgeOfMajority { get; set; } =
        int.Parse(GetEnvironmentVariable("PRIVACY_AGE_OF_MAJORITY") ?? "18");

    /// <summary>
    /// Whether a purged account keeps the single non-identifying <c>WasVerifiedAdult</c> boolean
    /// (T1-9). True by default; set <c>PRIVACY_RETAIN_WAS_VERIFIED_ADULT=false</c> to erase even
    /// that. Everything else about the age record - the birth date and all three verification
    /// timestamps - is destroyed either way.
    /// </summary>
    public bool RetainWasVerifiedAdult { get; set; } =
        GetEnvironmentVariable("PRIVACY_RETAIN_WAS_VERIFIED_ADULT")?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;

    /// <summary>The statutory window for answering a data-subject request (T1-13). Thirty days is
    /// the GDPR default; some regimes are shorter.</summary>
    public TimeSpan DataSubjectRequestResponseWindow { get; set; } =
        TimeSpan.FromDays(int.Parse(GetEnvironmentVariable("PRIVACY_DSR_RESPONSE_WINDOW_DAYS") ?? "30"));
}

/// <summary>Where the versioned legal documents (T1-12) live and how their public URLs are
/// built.</summary>
public class LegalDocumentConfiguration
{
    /// <summary>
    /// Directory holding <c>manifest.json</c> and the document files. Defaults to the <c>legal</c>
    /// folder copied next to the Identity binary at build time (see Identity.Application.csproj);
    /// override for a deployment that mounts the documents from outside the image.
    /// </summary>
    public string DirectoryPath { get; set; } =
        GetEnvironmentVariable("LEGAL_DOCUMENTS_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "legal");

    /// <summary>
    /// Public, browser-facing base URL the document routes hang off. This MUST include the YARP
    /// gateway prefix (<c>/api/v1/identity</c>), which the gateway rewrites to <c>/api/v1</c> before
    /// the request reaches Identity - the controller itself listens on
    /// <c>/api/v1/legal/documents/...</c>. Same convention as
    /// <see cref="SteamConfiguration.PublicCallbackPath"/>, and for the same reason: a URL stored in
    /// a row is quoted back to clients and to auditors, so it has to be the address they can
    /// actually fetch.
    /// </summary>
    public string PublicBaseUrl { get; set; } =
        GetEnvironmentVariable("LEGAL_DOCUMENTS_PUBLIC_BASE_URL")
        ?? ((GetEnvironmentVariable("INSTANCE_URL") ?? "https://api.venta.gg") + "/api/v1/identity/legal/documents");
}

/// <summary>
/// The GDPR Art. 15/20 access-and-portability path (T1-7 of docs/specs/privacy.md): how often an
/// account may ask for its data, how long the finished archive survives, and how long the signed
/// download URL is good for.
///
/// <para><b>The artifact TTL is the one T1-8's retention table names</b> ("DataExportRequest
/// artifacts, delete at 7 days, Identity"). It lives here rather than on
/// <see cref="RetentionConfiguration"/> because everything on that type is a scrub of a column on a
/// row that stays, swept by one shared pass; this is a delete of an object in blob storage, swept by
/// its own loop, and folding it in would have made "every window on RetentionConfiguration is a
/// scrub" untrue.</para>
///
/// <para>E2E and unit tests override the windows to single-digit seconds so the real rate limit and
/// the real expiry sweep can be exercised without waiting out a day or a week.</para>
/// </summary>
public class DataExportConfiguration
{
    /// <summary>
    /// How long an account must wait between export requests. Assembling an export reads every
    /// service's copy of a person's data, so an unthrottled endpoint is both a denial-of-service
    /// lever and a way to keep an indefinite number of live, downloadable copies of the densest
    /// personal-data bundle in the system in circulation.
    /// </summary>
    public TimeSpan RateLimitWindow { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_RATE_LIMIT_SECONDS") ?? (24 * 60 * 60).ToString()));

    /// <summary>How long a finished archive stays downloadable before the sweep marks the row
    /// <c>Expired</c> and deletes the object.</summary>
    public TimeSpan ArtifactTtl { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_ARTIFACT_TTL_SECONDS") ?? (7 * 24 * 60 * 60).ToString()));

    /// <summary>Lifetime of the signed URL the download route redirects to. Short on purpose: the
    /// URL carries its own authorization, so anything that captures it - a referrer header, a proxy
    /// log, a screenshot of a browser address bar - is a bearer credential for the whole archive
    /// until it expires.</summary>
    public TimeSpan DownloadUrlLifetime { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_DOWNLOAD_URL_SECONDS") ?? "300"));

    /// <summary>Gap between expiry sweep passes.</summary>
    public TimeSpan SweepInterval { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_SWEEP_INTERVAL_SECONDS") ?? (6 * 60 * 60).ToString()));

    /// <summary>Rows an expiry sweep pass touches. Bounded for the same reason
    /// <see cref="RetentionConfiguration.SweepBatchSize"/> is.</summary>
    public int SweepBatchSize { get; set; } =
        int.Parse(GetEnvironmentVariable("DATA_EXPORT_SWEEP_BATCH_SIZE") ?? "500");

    /// <summary>
    /// Cap on the messages one export carries out of a single conversation.
    ///
    /// <para>A cap rather than "everything" because every fragment travels as a bus message and is
    /// held in saga state until the archive is assembled - see <c>Echo.Sagas.ExportUserDataSaga</c>.
    /// When it bites, the fragment says so explicitly (<c>truncated: true</c>) rather than silently
    /// handing the subject a partial archive that looks complete, which under Art. 15 would be a
    /// worse answer than a slow one.</para>
    /// </summary>
    public int MaxMessagesPerConversation { get; set; } =
        int.Parse(GetEnvironmentVariable("DATA_EXPORT_MAX_MESSAGES_PER_CONVERSATION") ?? "5000");
}

/// <summary>
/// How long Echo's two cross-service privacy sagas (<c>Echo.Sagas.AccountDeletionSaga</c> and
/// <c>Echo.Sagas.ExportUserDataSaga</c>) wait for the last participant before declaring the fan-out
/// stalled.
///
/// <para><b>Why a deadline exists at all.</b> Both sagas complete only when every one of their eight
/// participants has answered, and neither had any upper bound: a service that was not deployed, was
/// deployed without its handler registered, or simply dropped a message left an erasure sitting at
/// <c>PurgeInProgress</c> or an access request sitting at <c>Running</c> forever, with nothing
/// logged and nobody paged. Silence is the worst possible outcome for a statutory obligation,
/// because the clock keeps running whether or not anyone is watching.</para>
///
/// <para><b>Why one hour.</b> Each participant's purge or export is a handful of writes over one
/// account's rows, so the healthy fan-out finishes in seconds. An hour is long enough to sit through
/// a rolling deploy, a broker restart and the full retry-with-cooldown ladder in
/// <c>Messaging.ConfigureWolverine</c> without crying wolf, and short enough that a genuinely stuck
/// erasure is discovered on the same working day rather than at the end of the statutory window -
/// two orders of magnitude inside the 30 days that both GDPR Art. 15 and Art. 17 allow. It is not a
/// service-level objective for the purge; it is the point at which "still working" stops being a
/// plausible explanation.</para>
///
/// <para>Both are overridable so a test can exercise the real deadline path in seconds instead of
/// waiting out an hour.</para>
/// </summary>
public class SagaDeadlineConfiguration
{
    public TimeSpan AccountPurge { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("ACCOUNT_PURGE_SAGA_DEADLINE_SECONDS") ?? (60 * 60).ToString()));

    public TimeSpan DataExport { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("DATA_EXPORT_SAGA_DEADLINE_SECONDS") ?? (60 * 60).ToString()));
}

/// <summary>
/// Tuning for the per-service telemetry consent gate (T0-4) - the in-memory mirror that lets
/// <see cref="SentryPrivacy.HasDataCollectionConsent"/>, a synchronous delegate called from inside
/// the Sentry SDK, answer without a Redis round trip on the error path.
///
/// <para>See <see cref="TelemetryConsentSnapshot"/> for the shape. What matters here is the pair of
/// windows: <see cref="RefreshInterval"/> is how quickly a withdrawal is picked up in the normal
/// case, and <see cref="EntryLifetime"/> is the hard ceiling on how long a cached "yes" can survive
/// if refreshes stop happening - after it, the entry is treated as unknown, which fails closed.</para>
/// </summary>
public class TelemetryConsentConfiguration
{
    /// <summary>How often the tracked ids are re-resolved through the service's privacy-settings
    /// cache. Short because the underlying Redis entry is already evicted the moment Identity
    /// publishes <c>UserPrivacySettingsChangedEvent</c>, so this interval - not the cache TTL - is
    /// what bounds how long a withdrawal keeps having no effect.</summary>
    public TimeSpan RefreshInterval { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("TELEMETRY_CONSENT_REFRESH_SECONDS") ?? "15"));

    /// <summary>How long a resolved answer is trusted without being re-confirmed. Deliberately a
    /// small multiple of <see cref="RefreshInterval"/>: one failed refresh pass degrades to
    /// pseudonymized telemetry rather than to an indefinitely stale "this user consented".</summary>
    public TimeSpan EntryLifetime { get; set; } =
        TimeSpan.FromSeconds(int.Parse(GetEnvironmentVariable("TELEMETRY_CONSENT_ENTRY_LIFETIME_SECONDS") ?? "45"));

    /// <summary>Ceiling on how many accounts are tracked at once. The set is populated by whoever
    /// happens to produce an error event, so it is attacker-influenceable; bounded so a burst of
    /// failing requests across many accounts cannot grow it without limit. Overflow is dropped, and
    /// a dropped id simply resolves to "no consent".</summary>
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
