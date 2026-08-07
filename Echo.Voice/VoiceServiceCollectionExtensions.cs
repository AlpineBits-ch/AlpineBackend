using Echo.Voice.Rooms;
using Echo.Voice.Sessions;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<VoiceRoomStore>();
        services.AddSingleton<VoiceAnnouncer>();
        services.AddSingleton<VoiceRoomService>();
        services.AddSingleton<VoiceReconciler>();
        return services;
    }
}
