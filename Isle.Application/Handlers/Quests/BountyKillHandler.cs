using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Quests;

/// <summary>The spree detector.</summary>
public class BountyKillHandler
{
    public static async Task Handle(
        PlayerKillEvent @event,
        KillStreakTracker streaks,
        BountyService bounties,
        ILogger<BountyKillHandler> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@event.KilerId) || string.IsNullOrWhiteSpace(@event.VictimId))
            return;

        // Suicides and self-inflicted deaths must not build a streak.
        if (@event.KilerId == @event.VictimId)
        {
            await streaks.ResetAsync(@event.VictimId);
            return;
        }

        await bounties.TryClaimAsync(@event.VictimId, @event.KilerId, ct);

        // The victim's own run is over regardless of whether they were marked.
        await streaks.ResetAsync(@event.VictimId);

        var streak = await streaks.RegisterKillAsync(@event.KilerId);
        if (streak is null)
            return;

        logger.LogDebug("Player {PlayerId} is on {Streak} kills", @event.KilerId, streak);

        await bounties.TryStartFromSpreeAsync(@event.KilerId, streak.Value, ct);
    }
}
