using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Quests;

/// <summary>
/// The spree detector. Runs on the same resolved <see cref="PlayerKillEvent"/> that
/// <c>PlayerKillEventHandler</c> writes to <c>KillLog</c> — the log stays the permanent record while
/// this drives the volatile session streak.
///
/// <para>Claim is checked before the streak is bumped, so a killer who takes down the marked player
/// gets paid on that same kill.</para>
/// </summary>
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
