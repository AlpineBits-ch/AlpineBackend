using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;
using Isle.Contracts.Events.Quest;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Handlers.Quests;

/// <summary>Closes a bounty when the target stops being huntable.</summary>
public class BountyLifecycleHandler
{
    public static Task Handle(UserDiedOnIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.ResolveDeathForSteamAsync(@event.SteamId, ct);

    public static Task Handle(UserLeftIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.EndForSteamAsync(@event.SteamId, QuestInstanceState.Cancelled, ct);
}

/// <summary>
/// Resolves a bounty death once the killfeed has had its window to claim it first.
/// </summary>
public class BountyDeathResolutionHandler
{
    public static async Task Handle(
        ResolveBountyDeathEvent @event,
        BountyService bounties,
        ILogger<BountyDeathResolutionHandler> logger,
        CancellationToken ct)
    {
        var open = await bounties.FindOpenBountyAsync(@event.PlayerId, ct);

        if (open is null)
        {
            logger.LogDebug("Bounty {InstanceId} on {PlayerId} was already resolved before the death grace " +
                            "period ran out; nothing to do", @event.QuestInstanceId, @event.PlayerId);
            return;
        }

        if (open.Id != @event.QuestInstanceId)
        {
            // The bounty they died under closed and a new one opened on them inside the grace period.
            // Resolving that one off a death that predates it would pay out for a hunt nobody ran.
            logger.LogInformation("Skipping death resolution for {PlayerId}: bounty {InstanceId} has been " +
                                  "replaced by {CurrentId}", @event.PlayerId, @event.QuestInstanceId, open.Id);
            return;
        }

        await bounties.TryResolveOnDeathAsync(@event.PlayerId, ct);
    }
}

/// <summary>Bridges the Steam-keyed game events to the player-keyed bounty service.</summary>
public sealed class BountyDispatcher(
    MicroserviceContext context,
    BountyService bounties,
    KillStreakTracker streaks,
    IMessageBus bus,
    ILogger<BountyDispatcher> logger)
{
    /// <summary>
    /// How long the killfeed gets to claim a dead target before the death feed is allowed to guess.
    /// </summary>
    public static readonly TimeSpan KillfeedGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>The target died.</summary>
    public async Task ResolveDeathForSteamAsync(string steamId, CancellationToken ct)
    {
        if (await ResolvePlayerIdAsync(steamId, ct) is not { } playerId)
            return;

        // Dying ends the run even when there was no bounty to close.
        await streaks.ResetAsync(playerId);

        // Nearly every death on the server is an unmarked player, and scheduling a durable message for
        // each one would put a Postgres row behind every one of them for nothing.
        if (await bounties.FindOpenBountyAsync(playerId, ct) is not { } instance)
            return;

        logger.LogDebug("Marked player {PlayerId} died; giving the killfeed {Grace} to claim bounty {InstanceId}",
            playerId, KillfeedGracePeriod, instance.Id);

        await bus.PublishAsync(new ResolveBountyDeathEvent
        {
            PlayerId = playerId,
            QuestInstanceId = instance.Id,
        }.DelayedFor(KillfeedGracePeriod));
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
