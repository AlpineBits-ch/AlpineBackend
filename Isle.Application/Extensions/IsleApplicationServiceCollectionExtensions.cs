using System.Net;
using AppEnvironment;
using Isle.Api.Chat;
using Isle.Api.Services;
using Isle.Api.Services.Hosted;
using Isle.Api.Services.Ingestion;
using Isle.Api.Services.Rcon;
using Isle.Api.Services.State;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using IsleBridge.Sdk;
using TheIsleEvrimaRconClient;

namespace Isle.Api.Extensions;

public static class IsleApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Everything this service owns: shared state, the two links to the game server (bridge + RCON),
    /// the stream ingestion services, and the background loops. Framework wiring (auth, SignalR,
    /// Wolverine, OpenAPI) stays in <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddIsleApplication(this IServiceCollection services)
    {
        services.AddIsleState();
        services.AddIsleGameServerLinks();
        services.AddIsleVoice();
        services.AddIsleStreamIngestion();
        services.AddIsleBackgroundWork();

        return services;
    }

    /// <summary>In-memory / Redis-backed caches and registries shared across handlers.</summary>
    private static IServiceCollection AddIsleState(this IServiceCollection services)
    {
        services.AddSingleton<VoicePlayerRegistry>();
        services.AddSingleton<VoiceTrackRegistry>();
        services.AddSingleton<PlayerPresenceManager>();
        services.AddSingleton<PlayerSpawnTracker>();
        services.AddSingleton<CommandCooldownService>();

        services.AddSingleton<PlayerPositionCache>();
        services.AddSingleton<Repositories.IPlayerPositionProvider>(sp => sp.GetRequiredService<PlayerPositionCache>());

        return services;
    }

    /// <summary>The two channels to the dedicated server: the bridge plugin (HTTP/SSE) and RCON.</summary>
    private static IServiceCollection AddIsleGameServerLinks(this IServiceCollection services)
    {
        var isle = Env.Isle;

        services.AddIsleBridge(cfg =>
        {
            cfg.BaseAddress = new Uri(isle.BridgeBaseAddress);
            cfg.SlowCommandTimeout = TimeSpan.FromSeconds(10);
            cfg.EnableSkinReapply = true;
            cfg.SkinReapplyDelay = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<ISkinStore, SkinStore>();

        // The client itself is never registered: RconGateway owns it so every command goes through
        // one serialised, reconnecting path (see IRconGateway).
        services.AddSingleton(new EvrimaRconClientConfiguration
        {
            Host = IPAddress.Parse(isle.IpAddress),
            Port = isle.RconPort,
            Password = isle.RconPassword,
        });
        services.AddSingleton<IRconGateway, RconGateway>();

        services.AddSingleton<SpeciesPopulationLimits>();
        services.AddSingleton<WorldCleaner>();

        return services;
    }

    private static IServiceCollection AddIsleVoice(this IServiceCollection services)
    {
        // CellSize is the coarse audible-membership filter and must stay >= the client's
        // attenuation radius. Proximity voice range is 80 m, so 8000 UE units (cm).
        services.AddSingleton(new VoiceGridConfig { CellSize = 8000f });
        services.AddSingleton<VoiceCluster>();

        return services;
    }

    /// <summary>
    /// One consumer per bridge feed. Each opens a single SSE connection and republishes what it
    /// reads onto the bus; everything downstream is a Wolverine handler.
    /// </summary>
    private static IServiceCollection AddIsleStreamIngestion(this IServiceCollection services)
    {
        services.AddSingleton<ChatCommandRegistry>();

        services.AddHostedService<ChatStreamIngestionService>();
        services.AddHostedService<GameEventStreamIngestionService>();
        services.AddHostedService<StatsStreamIngestionService>();

        return services;
    }

    private static IServiceCollection AddIsleBackgroundWork(this IServiceCollection services)
    {
        services.AddHostedService<RconConnectionService>();
        services.AddHostedService<PopulationLimitService>();
        services.AddHostedService<WorldCleanupService>();
        services.AddHostedService<InviteTimeoutService>();
        services.AddHostedService<VoicePresenceReconcileService>();

        // Re-drives the proximity subscription graph on a short interval so a dropped/mistimed
        // SubscribeMutual push converges instead of leaving one side deaf (the "I hear them, they
        // see 0" asymmetry). Idempotent and symmetric — healthy subscriptions are untouched.
        services.AddHostedService<VoiceSubscriptionReconcileService>();

        return services;
    }
}
