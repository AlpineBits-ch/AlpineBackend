using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Quests;

/// <summary>
/// Closes a bounty when the target stops being huntable.
///
/// <para>The death path is safe to run alongside <see cref="BountyKillHandler"/>: a player killed by
/// another player produces a killfeed event and a death event at roughly the same moment, and
/// whichever reaches <c>QuestInstance.TryClose</c> first wins. Both now resolve to the same outcome,
/// because <c>BountyService.TryResolveOnDeathAsync</c> consults the damage ledger rather than assuming
/// a death with no killfeed attached was an accident — so a PvP death still pays the killer even when
/// the death event arrives first, and a real accident (drown, fall, starve) completes the bounty and
/// pays the hunters who wore the target down.</para>
///
/// <para>Leaving is not a death: nobody is paid for a target who logged off, so that path stays a
/// plain cancel.</para>
/// </summary>
public class BountyLifecycleHandler
{
    public static Task Handle(UserDiedOnIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.ResolveDeathForSteamAsync(@event.SteamId, ct);

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
    /// <summary>The target died. Lets the bounty service decide between a claim and a natural death.</summary>
    public async Task ResolveDeathForSteamAsync(string steamId, CancellationToken ct)
    {
        if (await ResolvePlayerIdAsync(steamId, ct) is not { } playerId)
            return;

        // Dying ends the run even when there was no bounty to close.
        await streaks.ResetAsync(playerId);
        await bounties.TryResolveOnDeathAsync(playerId, ct);
    }

    /// <summary>Closes an open bounty with no payout — the target logged off, or an admin called it off.</summary>
    public async Task EndForSteamAsync(string steamId, QuestInstanceState state, CancellationToken ct)
    {
        if (await ResolvePlayerIdAsync(steamId, ct) is not { } playerId)
            return;

        await streaks.ResetAsync(playerId);
        await bounties.CancelForPlayerAsync(playerId, state, ct);
    }

    private async Task<string?> ResolvePlayerIdAsync(string steamId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(steamId))
            return null;

        return (await context.Players
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.SteamId == steamId, ct))?.Id;
    }
}
