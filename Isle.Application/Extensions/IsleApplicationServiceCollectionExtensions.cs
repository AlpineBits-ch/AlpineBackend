using System.Net;
using AppEnvironment;
using Isle.Api.Chat;
using Isle.Api.Games.KingOfTheHill;
using Isle.Api.Handlers.Quests;
using Isle.Api.Services;
using Isle.Api.Services.Hosted;
using Isle.Api.Services.Ingestion;
using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.Quests;
using Isle.Api.Services.Rcon;
using Isle.Api.Services.Rewards;
using Isle.Api.Services.State;
using Isle.Api.Services.World;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity.Voice;
using Isle.Domain.Interfaces;
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
        services.AddIsleWorld();
        services.AddIsleRewards();
        services.AddIsleQuests();
        services.AddIsleGameModes();
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

    /// <summary>
    /// Map knowledge and the whole-server roster snapshot. Registered ahead of the quest services
    /// because both the director and the bounty announcer are built on them.
    /// </summary>
    private static IServiceCollection AddIsleWorld(this IServiceCollection services)
    {
        services.AddSingleton<RegionMap>();
        services.AddSingleton<WorldRosterCache>();
        services.AddSingleton<PopulationHeatmap>();

        return services;
    }

    /// <summary>The reward granter, shared by quests and every game mode. Scoped: it touches <c>MicroserviceContext</c>.</summary>
    private static IServiceCollection AddIsleRewards(this IServiceCollection services)
    {
        services.AddScoped<RewardGranter>();

        return services;
    }

    /// <summary>
    /// The automatic quest giver and the killing-spree bounty system.
    ///
    /// <para>Streak and mark state are singletons (they are Redis-backed and read from hot paths);
    /// everything that touches the database is scoped, because a <c>MicroserviceContext</c> captured
    /// for the process lifetime is a leak waiting to happen.</para>
    /// </summary>
    private static IServiceCollection AddIsleQuests(this IServiceCollection services)
    {
        services.AddSingleton<KillStreakTracker>();
        services.AddSingleton<BountyRegistry>();
        services.AddSingleton<BountyParticipantLedger>();
        services.AddSingleton<QuestProgressLedger>();

        services.AddScoped<QuestDirector>();
        services.AddScoped<QuestSpawner>();
        services.AddScoped<QuestAnnouncer>();
        services.AddScoped<QuestCompletionService>();
        services.AddScoped<BountyService>();
        services.AddScoped<BountyDispatcher>();

        return services;
    }

    /// <summary>
    /// King of the Hill, the first of what the Games readme calls out as more than one planned mode.
    ///
    /// <para>The control ledger and the durable match marker are singletons (Redis-backed, read from
    /// the tick loop); everything that touches the database — including <c>IGameMode</c> itself, since
    /// <see cref="KingOfTheHillMode"/> resolves standings against <c>MicroserviceContext</c> — is scoped,
    /// same rule <see cref="AddIsleQuests"/> follows and for the same reason.</para>
    /// </summary>
    private static IServiceCollection AddIsleGameModes(this IServiceCollection services)
    {
        services.AddSingleton<KingOfTheHillControlLedger>();
        services.AddSingleton<KingOfTheHillMatchStateStore>();

        services.AddScoped<IGameMode, KingOfTheHillMode>();
        services.AddScoped<KingOfTheHillDirector>();
        services.AddScoped<KingOfTheHillSpawner>();
        services.AddScoped<KingOfTheHillTickService>();
        services.AddScoped<KingOfTheHillCompletionService>();
        services.AddScoped<KingOfTheHillAnnouncer>();

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

        // Roster first: the quest director refuses to place anything against a stale snapshot, so it
        // needs WorldRosterService to have landed at least one read.
        services.AddHostedService<WorldRosterService>();
        services.AddHostedService<QuestDirectorService>();

        // Same reason, more strictly: quest presence is credited straight off the roster snapshot, and
        // crediting a stale one pays players who are no longer there. It gates on the timestamp rather
        // than trusting startup order.
        services.AddHostedService<QuestProgressService>();

        // Same roster dependency as the quest director, one level up: it also reads WorldRosterCache
        // directly for zone-presence checks.
        services.AddHostedService<KingOfTheHillDirectorService>();

        // Re-drives the proximity subscription graph on a short interval so a dropped/mistimed
        // SubscribeMutual push converges instead of leaving one side deaf (the "I hear them, they
        // see 0" asymmetry). Idempotent and symmetric — healthy subscriptions are untouched.
        services.AddHostedService<VoiceSubscriptionReconcileService>();

        return services;
    }
}
