using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.Quests;

/// <summary>Closes a bounty when the target stops being huntable.</summary>
public class BountyLifecycleHandler
{
    public static Task Handle(UserDiedOnIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.ResolveDeathForSteamAsync(@event.SteamId, ct);

    public static Task Handle(UserLeftIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.EndForSteamAsync(@event.SteamId, QuestInstanceState.Cancelled, ct);
}

/// <summary>Bridges the Steam-keyed game events to the player-keyed bounty service.</summary>
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
