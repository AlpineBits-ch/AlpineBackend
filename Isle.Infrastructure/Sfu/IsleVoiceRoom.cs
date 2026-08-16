using Echo.Realtime.LiveKit;
using Microsoft.Extensions.Logging;

namespace Isle.Infrastructure.Sfu;

/// <summary>The one SFU room proximity voice runs in, and where it lives.</summary>
public sealed class IsleVoiceRoom(
    LiveKitOptions options,
    LiveKitRoomRegistry registry,
    LiveKitRoomClient client,
    ILogger<IsleVoiceRoom> logger)
{
    /// <summary>The room name, stable for the life of the deployment.</summary>
    public string Name { get; } = $"prox-isle-{Shard}";

    private static string Shard =>
        Environment.GetEnvironmentVariable("ISLE_VOICE_SHARD")?.Trim() is { Length: > 0 } shard
            ? shard
            : LiveKitRegions.Default;

    public bool IsConfigured => options.IsConfigured;

    /// <summary>The region this room belongs to. The game server's, per the class remarks.</summary>
    private static string Region =>
        Environment.GetEnvironmentVariable("ISLE_VOICE_REGION")?.Trim() is { Length: > 0 } region
            ? region
            : LiveKitRegions.Default;

    /// <summary>Makes sure the room exists and answers the node hosting it.</summary>
    public Task<LiveKitNode> EnsureAsync(CancellationToken ct = default) =>
        // Placed and created as one locked operation, because Isle runs behind a load balancer too:
        // several pods can reach the first connection of a shard at once, and a split placement
        // would put half the players in a room the other half is not in.
        registry.PlaceAsync(
            Name, Region,
            (node, token) => client.CreateRoomAsync(node, Name, maxParticipants: null, token),
            ct);

    /// <summary>The node hosting the room, or null when it has never been created.</summary>
    public async Task<LiveKitNode?> FindAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        var node = await registry.FindAsync(Name, ct);
        if (node is null)
            logger.LogDebug("Proximity room {Room} does not exist yet", Name);
        return node;
    }
}
