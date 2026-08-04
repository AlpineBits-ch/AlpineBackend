using Echo.E2E.Tests.Fixtures;

namespace Echo.E2E.Tests.Hosts;

/// <summary>
/// Boots one full "instance" of the Echo backend - Identity, Guild, Messaging, Social, Federation,
/// Import, and the Echo gateway - as real child processes sharing one <see cref="EchoInfraSet"/>.
/// </summary>
public sealed class EchoTestStack : IAsyncDisposable
{
    public SpawnedServiceProcess Identity { get; private set; } = null!;
    public SpawnedServiceProcess Guild { get; private set; } = null!;
    public SpawnedServiceProcess Messaging { get; private set; } = null!;
    public SpawnedServiceProcess Social { get; private set; } = null!;
    public SpawnedServiceProcess Federation { get; private set; } = null!;
    public SpawnedServiceProcess Import { get; private set; } = null!;

    /// <summary>Link previews (docs/specs/message-previews.md).</summary>
    public SpawnedServiceProcess Unfurl { get; private set; } = null!;

    public SpawnedServiceProcess Gateway { get; private set; } = null!;

    /// <summary>
    /// The Unfurl service's own base URL, which is also what it builds <c>proxy_url</c> values
    /// from.
    /// </summary>
    public string UnfurlBaseUrl { get; private set; } = null!;

    public string InstanceName { get; }

    /// <summary>This stack's Identity database.</summary>
    public string IdentityDatabaseName { get; private set; } = null!;

    /// <summary>This stack's Messaging database.</summary>
    public string MessagingDatabaseName { get; private set; } = null!;

    private EchoTestStack(string instanceName) => InstanceName = instanceName;

    /// <param name="infra">The infra set to run against.</param>
    /// <param name="databaseSuffix">
    /// Distinguishes this stack's databases from another stack sharing the same infra set
    /// (irrelevant when each stack has its own independent infra set, but kept explicit so a single
    /// infra set could host more than one stack if a test ever needs that).
    /// </param>
    /// <param name="instanceName">
    /// Federation instance display name (Env.Federation.InstanceName).
    /// </param>
    public static async Task<EchoTestStack> StartAsync(
        EchoInfraSet infra,
        string databaseSuffix,
        string instanceName)
    {
        var stack = new EchoTestStack(instanceName);

        Dictionary<string, string> Common(string databaseName) => new()
        {
            ["DATABASE_HOSTNAME"] = infra.PostgresHost,
            ["DATABASE_PORT"] = infra.PostgresPort.ToString(),
            ["DATABASE_NAME"] = databaseName,
            ["DATABASE_USERNAME"] = "postgres",
            ["DATABASE_PASSWORD"] = "postgres",
            ["RABBITMQ_HOST"] = infra.RabbitMqHost,
            ["RABBITMQ_PORT"] = infra.RabbitMqPort.ToString(),
            ["RABBITMQ_USERNAME"] = EchoInfraSet.RabbitMqUser,
            ["RABBITMQ_PASSWORD"] = EchoInfraSet.RabbitMqPassword,
            ["REDIS_HOST"] = infra.RedisHost,
            ["REDIS_PORT"] = infra.RedisPort.ToString(),
            ["REDIS_PASSWORD"] = EchoInfraSet.RedisPassword,
            ["AUTH_REQUIRE_USER_EMAIL_VERIFICATION"] = "false",
        };

        // Identity mints tokens with itself as OpenIddict issuer, and every other service validates
        // JWTs against Authority = INSTANCE_URL by fetching
        // {INSTANCE_URL}/.well-known/openid-configuration + jwks.
        var identityPort = SpawnedServiceProcess.ReserveFreeTcpPort();
        var identityUrl = $"http://127.0.0.1:{identityPort}";

        stack.IdentityDatabaseName = $"identity_{databaseSuffix}";

        var identityEnv = Common(stack.IdentityDatabaseName);
        identityEnv["INSTANCE_URL"] = identityUrl;
        // Real 30-day default grace period/sweep interval (AppEnvironment.AccountDeletionConfiguration)
        // would make AccountDeletionFlowTests wait days for the real scheduled-purge path to fire -
        // shrunk to single-digit seconds so the harness can still exercise the real
        // AccountDeletionPurgeSweepService -> AccountPurgeStartedEvent -> AccountDeletionSaga chain
        // over the real broker instead of bypassing it with a test-only trigger endpoint.
        identityEnv["ACCOUNT_DELETION_GRACE_PERIOD_SECONDS"] = "3";
        identityEnv["ACCOUNT_DELETION_SWEEP_INTERVAL_SECONDS"] = "2";
        // Identity is the only service that touches the export artifact bucket - it owns the
        // DataExportRequest row, the upload in AssembleUserDataExportCommandHandler and the signed
        // URL the download route redirects to.
        identityEnv["BUCKET_NAME"] = EchoInfraSet.ObjectStorageBucket;
        identityEnv["ACCESS_KEY_ID"] = EchoInfraSet.ObjectStorageAccessKey;
        identityEnv["SECRET_ACCESS_KEY"] = EchoInfraSet.ObjectStorageSecretKey;
        identityEnv["SERVICE_URL"] = infra.ObjectStorageUrl;
        identityEnv["PUBLIC_URL"] = infra.ObjectStorageUrl;
        identityEnv["USE_SERVICE_URL"] = "true";
        stack.Identity = await SpawnedServiceProcess.StartAsync(
            "Identity.Application", "/identity/health", identityEnv, identityPort);

        var guildEnv = Common($"guild_{databaseSuffix}");
        guildEnv["INSTANCE_URL"] = identityUrl;
        stack.MessagingDatabaseName = $"messaging_{databaseSuffix}";
        var messagingEnv = Common(stack.MessagingDatabaseName);
        messagingEnv["INSTANCE_URL"] = identityUrl;
        // Production defaults message storage to Scylla (compose.yaml sets USE_SCYLLA_DB=true; see
        // Messaging.Infrastructure.MessagingInfrastructure).
        messagingEnv["USE_SCYLLA_DB"] = "false";
        var socialEnv = Common($"social_{databaseSuffix}");
        socialEnv["INSTANCE_URL"] = identityUrl;
        var federationEnv = Common($"federation_{databaseSuffix}");
        federationEnv["INSTANCE_URL"] = identityUrl;
        federationEnv["INSTANCE_NAME"] = instanceName;
        var importEnv = Common($"import_{databaseSuffix}");
        importEnv["INSTANCE_URL"] = identityUrl;
        // Reserved up front, like Identity's, because the service has to be told its own public
        // address: proxy_url values are built from UNFURL_PUBLIC_BASE_URL and stored inside
        // messages, so it cannot be discovered after the fact.
        var unfurlPort = SpawnedServiceProcess.ReserveFreeTcpPort();
        stack.UnfurlBaseUrl = $"http://127.0.0.1:{unfurlPort}";

        var unfurlDatabase = $"unfurl_{databaseSuffix}";

        // Created here because nothing else will.
        await infra.EnsureDatabasesAsync(unfurlDatabase);

        var unfurlEnv = Common(unfurlDatabase);
        unfurlEnv["INSTANCE_URL"] = identityUrl;
        unfurlEnv["UNFURL_PUBLIC_BASE_URL"] = stack.UnfurlBaseUrl;

        // The one setting that is deliberately the opposite of production.
        unfurlEnv["UNFURL_ALLOW_PRIVATE_TARGETS"] = "true";

        // Preview images are re-hosted rather than hot-linked, so the media half of the pipeline
        // needs a real bucket the same way Identity's export does - decode, resize, thumbhash, PUT,
        // and then serve it back on the proxy route.
        unfurlEnv["BUCKET_NAME"] = EchoInfraSet.ObjectStorageBucket;
        unfurlEnv["ACCESS_KEY_ID"] = EchoInfraSet.ObjectStorageAccessKey;
        unfurlEnv["SECRET_ACCESS_KEY"] = EchoInfraSet.ObjectStorageSecretKey;
        unfurlEnv["SERVICE_URL"] = infra.ObjectStorageUrl;
        unfurlEnv["PUBLIC_URL"] = infra.ObjectStorageUrl;
        unfurlEnv["USE_SERVICE_URL"] = "true";

        var gatewayEnv = Common($"echo_{databaseSuffix}");
        gatewayEnv["INSTANCE_URL"] = identityUrl;
        // ExportUserDataSaga runs in the gateway process, and its deadline
        // (Env.SagaDeadlines.DataExport, AppEnvironment/Env.cs) defaults to an hour - the point at
        // which "still working" stops being a plausible explanation in production, and far longer
        // than any test can wait.
        gatewayEnv["DATA_EXPORT_SAGA_DEADLINE_SECONDS"] = "30";

        // Started sequentially (not in parallel) so a failure surfaces against the specific service
        // that failed, with that service's captured stdout/stderr, instead of an ambiguous
        // aggregate exception from Task.WhenAll.
        try
        {
            stack.Guild = await SpawnedServiceProcess.StartAsync(
                "Guild.Application", "/guild/health", guildEnv);
            stack.Messaging = await SpawnedServiceProcess.StartAsync(
                "Messaging.Application", "/messaging/health", messagingEnv);
            stack.Social = await SpawnedServiceProcess.StartAsync(
                "Social.Application", "/social/health", socialEnv);
            stack.Federation = await SpawnedServiceProcess.StartAsync(
                "Federation.Application", "/federation/health", federationEnv);
            stack.Import = await SpawnedServiceProcess.StartAsync(
                "Import.Application", "/import/health", importEnv);
            stack.Unfurl = await SpawnedServiceProcess.StartAsync(
                "Unfurl.Application", "/unfurl/health", unfurlEnv, unfurlPort);
            stack.Gateway = await SpawnedServiceProcess.StartAsync(
                "Echo", "/health", gatewayEnv);
        }
        catch
        {
            await stack.DisposeStartedAsync();
            throw;
        }

        return stack;
    }

    public ValueTask DisposeAsync() => DisposeStartedAsync();

    private async ValueTask DisposeStartedAsync()
    {
        var started = new[] { Identity, Guild, Messaging, Social, Federation, Import, Unfurl, Gateway }
            .Where(p => p is not null);
        await Task.WhenAll(started.Select(p => p.DisposeAsync().AsTask()));
    }
}
