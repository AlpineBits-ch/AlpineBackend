using Isle.Domain.Aggregates;

namespace Isle.Api.Chat.CommandController.Commands;

/// <summary>
/// Shared gate for invite teleports: the acting player must be a fresh spawn — at or under <see
/// cref="PlayerInvite.MaxGrowthForTeleport"/> growth and within <see
/// cref="PlayerInvite.SpawnWindow"/> of spawning.
/// </summary>
internal static class InviteTeleportEligibility
{
    public static (bool Ok, string? Error) Check(double growth, DateTimeOffset? lastSpawn)
    {
        if (growth > PlayerInvite.MaxGrowthForTeleport)
        {
            return (false,
                $"Invite teleports are for fresh spawns only (max {PlayerInvite.MaxGrowthForTeleport:P0} growth). You are at {growth:P0}.");
        }

        if (lastSpawn is null || DateTimeOffset.UtcNow - lastSpawn.Value > PlayerInvite.SpawnWindow)
        {
            return (false,
                $"You can only do this within {PlayerInvite.SpawnWindow.TotalMinutes:0} minutes of spawning.");
        }

        return (true, null);
    }
}
