using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Quests;

/// <summary>
/// Closes a bounty when the target stops being huntable.
///
/// <para>Both paths are safe to run alongside <see cref="BountyKillHandler"/>: a player killed by
/// another player produces a killfeed event and a death event at roughly the same moment, and
/// whichever reaches <c>QuestInstance.TryClose</c> first wins. So a PvP death that was already paid
/// out as a claim is a no-op here, and a death with no killer (drown, fall, starve) closes the bounty
/// unclaimed.</para>
/// </summary>
public class BountyLifecycleHandler
{
    public static Task Handle(UserDiedOnIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.EndForSteamAsync(@event.SteamId, QuestInstanceState.Expired, ct);

    public static Task Handle(UserLeftIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.EndForSteamAsync(@event.SteamId, QuestInstanceState.Cancelled, ct);
}

/// <summary>
/// Bridges the Steam-keyed game events to the player-keyed bounty service. Exists so the handler
/// above stays a two-liner and the lookup is testable on its own.
/// </summary>
public sealed class BountyDispatcher(
    MicroserviceContext context,
    BountyService bounties,
    KillStreakTracker streaks)
{
    public async Task EndForSteamAsync(string steamId, QuestInstanceState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(steamId))
            return;

        var playerId = (await context.Players
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.SteamId == steamId, ct))?.Id;

        if (playerId is null)
            return;

        // Dying or leaving ends the run even when there was no bounty to close.
        await streaks.ResetAsync(playerId);
        await bounties.CancelForPlayerAsync(playerId, state, ct);
    }
}
