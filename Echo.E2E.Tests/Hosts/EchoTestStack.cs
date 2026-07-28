using Echo.E2E.Tests.Fixtures;

namespace Echo.E2E.Tests.Hosts;

/// <summary>
/// Boots one full "instance" of the Echo backend - Identity, Guild, Messaging, Social,
/// Federation, Import, and the Echo gateway - as real child processes sharing one
/// <see cref="EchoInfraSet"/>. Each service gets its own database (see
/// <see cref="EchoInfraFixture.DatabaseNames"/>) but shares the broker/cache within that infra
/// set, exactly as in compose.yaml.
///
/// The Echo gateway is NOT just a YARP reverse proxy - it also hosts a Wolverine host with
/// cross-service sagas (e.g. <c>Echo.Sagas.UserRegistrationSaga</c>, which turns Identity's
/// UserCreatedEvent into the CreateUserProfileCommand Social actually materializes a profile
/// from). Omitting it originally seemed safe ("just routing") but silently broke that saga,
/// which made registration look like a dead RabbitMQ event when the real cause was a missing
/// orchestrator - discovered the hard way by this harness. Tests still call each service's HTTP
/// API directly rather than through the gateway's proxy routes, since exercising YARP routing
/// itself isn't this harness's concern - only running the gateway process so its non-proxy
/// responsibilities (sagas, the realtime hub) are present.
/// </summary>
public sealed class EchoTestStack : IAsyncDisposable
{
    public SpawnedServiceProcess Identity { get; private set; } = null!;
    public SpawnedServiceProcess Guild { get; private set; } = null!;
    public SpawnedServiceProcess Messaging { get; private set; } = null!;
    public SpawnedServiceProcess Social { get; private set; } = null!;
    public SpawnedServiceProcess Federation { get; private set; } = null!;
    public SpawnedServiceProcess Import { get; private set; } = null!;
    public SpawnedServiceProcess Gateway { get; private set; } = null!;

    public string InstanceName { get; }

    private EchoTestStack(string instanceName) => InstanceName = instanceName;

    /// <param name="infra">
    /// The infra set to run against. Pass <see cref="EchoInfraFixture.Default"/> for ordinary
    /// scenario tests, or a freshly-provisioned independent <see cref="EchoInfraSet"/> per
    /// simulated instance in two-instance federation tests.
    /// </param>
    /// <param name="databaseSuffix">
    /// Distinguishes this stack's databases from another stack sharing the same infra set
    /// (irrelevant when each stack has its own independent infra set, but kept explicit so a
    /// single infra set could host more than one stack if a test ever needs that).
    /// </param>
    /// <param name="instanceName">Federation instance display name (Env.Federation.InstanceName).</param>
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
            ["SCYLLA_HOST"] = infra.ScyllaHost,
            ["SCYLLA_PORT"] = infra.ScyllaPort.ToString(),
            ["AUTH_REQUIRE_USER_EMAIL_VERIFICATION"] = "false",
        };

        // Identity mints tokens with itself as OpenIddict issuer, and every other service
        // validates JWTs against Authority = INSTANCE_URL by fetching
        // {INSTANCE_URL}/.well-known/openid-configuration + jwks. Reserve Identity's port up
        // front so every service (including Identity itself) can be told the same real address.
        var identityPort = SpawnedServiceProcess.ReserveFreeTcpPort();
        var identityUrl = $"http://127.0.0.1:{identityPort}";

        var identityEnv = Common($"identity_{databaseSuffix}");
        identityEnv["INSTANCE_URL"] = identityUrl;
        stack.Identity = await SpawnedServiceProcess.StartAsync(
            "Identity.Application", "/identity/health", identityEnv, identityPort);

        var guildEnv = Common($"guild_{databaseSuffix}");
        guildEnv["INSTANCE_URL"] = identityUrl;
        var messagingEnv = Common($"messaging_{databaseSuffix}");
        messagingEnv["INSTANCE_URL"] = identityUrl;
        var socialEnv = Common($"social_{databaseSuffix}");
        socialEnv["INSTANCE_URL"] = identityUrl;
        var federationEnv = Common($"federation_{databaseSuffix}");
        federationEnv["INSTANCE_URL"] = identityUrl;
        federationEnv["INSTANCE_NAME"] = instanceName;
        var importEnv = Common($"import_{databaseSuffix}");
        importEnv["INSTANCE_URL"] = identityUrl;
        var gatewayEnv = Common($"echo_{databaseSuffix}");
        gatewayEnv["INSTANCE_URL"] = identityUrl;

        // Started sequentially (not in parallel) so a failure surfaces against the specific
        // service that failed, with that service's captured stdout/stderr, instead of an
        // ambiguous aggregate exception from Task.WhenAll.
        //
        // If a later service fails to start, everything already started here is a real OS
        // process holding real ports and file locks on its own build output - it must be killed
        // before the exception propagates, or it leaks for the rest of the test run (and blocks
        // the next `dotnet build`, since the DLL stays locked). NUnit won't call DisposeAsync for
        // a stack that never finished constructing, so StartAsync has to clean up after itself.
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
                "Import.Application", "/imports/health", importEnv);
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
        var started = new[] { Identity, Guild, Messaging, Social, Federation, Import, Gateway }
            .Where(p => p is not null);
        await Task.WhenAll(started.Select(p => p.DisposeAsync().AsTask()));
    }
}
