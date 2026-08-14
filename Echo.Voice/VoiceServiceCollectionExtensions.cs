using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
using Echo.Voice.Transport;
using Echo.Voice.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Echo.Voice;

public static class VoiceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the room-orchestration services shared by every deployment that runs voice rooms
    /// (guild channels, direct calls).
    /// </summary>
    public static IServiceCollection AddVoiceRooms(this IServiceCollection services)
    {
        services.AddSingleton<SfuSessionOwnership>();
        services.AddScoped<IVoiceMediaTransport, CloudflareMediaTransport>();
        services.AddSingleton<VoiceRoomStore>();
        services.AddSingleton<VoiceAnnouncer>();

        // Registered before the two services that take it, and unconditionally: with planning
        // disabled every room stays all-to-all, which is the behaviour these services had before
        // subscription planning existed.
        services.TryAddSingleton(VoiceSubscriptionOptions.FromEnvironment());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<VoiceAttentionStore>();
        services.AddSingleton<VoiceSubscriptions>();

        services.AddSingleton<VoiceRoomService>();
        services.AddSingleton<VoiceReconciler>();

        // Resolved rather than required, because GuildInfrastructure registers the multiplexer
        // inside a try/catch: a service that starts while Redis is unreachable has none in the
        // container, and a required dependency here would turn "the meter cannot record" into "the
        // service will not start". Metering is never allowed to be load-bearing for voice.
        services.TryAddSingleton<IVoiceUsageBackend>(sp =>
            sp.GetService<IConnectionMultiplexer>() is { } redis
                ? new RedisVoiceUsageBackend(redis)
                : new DisabledVoiceUsageBackend());
        services.AddSingleton<VoiceUsageMeter>();
        return services;
    }
}
