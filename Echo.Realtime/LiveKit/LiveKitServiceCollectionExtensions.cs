using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Echo.Realtime.LiveKit;

public static class LiveKitServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LiveKit control plane: the options, the token minter's configuration, the
    /// Twirp client and the room registry.
    /// </summary>
    /// <param name="options">Null reads the environment, which is what every host does.</param>
    public static IServiceCollection AddLiveKit(
        this IServiceCollection services, LiveKitOptions? options = null)
    {
        var resolved = options ?? LiveKitOptions.FromEnvironment();

        services.TryAddSingleton(resolved);

        // No base address and no default headers: every call names its own node, and every call
        // mints its own short-lived admin token rather than carrying one on the client.
        services.AddHttpClient(LiveKitRoomClient.HttpClientName, client =>
        {
            // Short.
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.TryAddSingleton<LiveKitRoomClient>();
        services.TryAddSingleton<LiveKitRoomRegistry>();

        services.AddHostedService<LiveKitReconciler>();

        return services;
    }
}
