using Isle.Domain.Aggregates;

namespace Isle.Api.Chat.CommandController.Commands;

/// <summary>
/// Shared gate for friend teleports: the acting player must be a fresh spawn — at or under <see
/// cref="FriendRequest.MaxGrowthForTeleport"/> growth and within <see
/// cref="FriendRequest.SpawnWindow"/> of spawning.
/// </summary>
internal static class FriendTeleportEligibility
{
    public static (bool Ok, string? Error) Check(double growth, DateTimeOffset? lastSpawn)
    {
        if (growth > FriendRequest.MaxGrowthForTeleport)
        {
            return (false,
                $"Friend teleports are for fresh spawns only (max {FriendRequest.MaxGrowthForTeleport:P0} growth). You're at {growth:P0}.");
        }

        if (lastSpawn is null || DateTimeOffset.UtcNow - lastSpawn.Value > FriendRequest.SpawnWindow)
        {
            return (false,
                $"You can only do this within {FriendRequest.SpawnWindow.TotalMinutes:0} minutes of spawning.");
        }

        return (true, null);
    }
}
