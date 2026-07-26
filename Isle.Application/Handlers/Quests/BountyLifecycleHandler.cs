using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;
using Isle.Contracts.Events.Quest;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Handlers.Quests;

/// <summary>
/// Closes a bounty when the target stops being huntable.
///
/// <para>The death path deliberately does not resolve the bounty itself. A player kill produces a
/// killfeed event and a death event on two independent feeds, and only the killfeed knows who did it —
/// the death feed can do no better than guess from the damage ledger. Since either one closing the
/// bounty locks the other out, the guess has to wait: the death is turned into a delayed
/// <see cref="ResolveBountyDeathEvent"/> that <see cref="BountyDeathResolutionHandler"/> picks up once
/// the killfeed has had its chance. A real accident (drown, fall, starve) resolves there, a moment
/// later than before and to nobody's cost.</para>
///
/// <para>Leaving is not a death: nobody is paid for a target who logged off, so that path stays a
/// plain cancel, resolved inline.</para>
/// </summary>
public class BountyLifecycleHandler
{
    public static Task Handle(UserDiedOnIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.ResolveDeathForSteamAsync(@event.SteamId, ct);

    public static Task Handle(UserLeftIsleServerEvent @event, BountyDispatcher dispatcher, CancellationToken ct) =>
        dispatcher.EndForSteamAsync(@event.SteamId, QuestInstanceState.Cancelled, ct);
}

/// <summary>
/// Resolves a bounty death once the killfeed has had its window to claim it first.
///
/// <para>In the ordinary case — a player kill — the killfeed got here already and there is nothing
/// open to resolve, so this is a no-op. What is left is the deaths the killfeed never reported: real
/// accidents, and PvP kills whose killfeed line the bridge dropped. Both are handled the same way they
/// always were, off the damage ledger.</para>
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

/// <summary>
/// Bridges the Steam-keyed game events to the player-keyed bounty service. Exists so the handler
/// above stays a two-liner and the lookup is testable on its own.
/// </summary>
public sealed class BountyDispatcher(
    MicroserviceContext context,
    BountyService bounties,
    KillStreakTracker streaks,
    IMessageBus bus,
    ILogger<BountyDispatcher> logger)
{
    /// <summary>
    /// How long the killfeed gets to claim a dead target before the death feed is allowed to guess.
    ///
    /// <para>The killfeed path is two durable hops and two lookups — bridge to
    /// <c>PlayerKillfeedReportedEvent</c>, resolved to <c>PlayerKillEvent</c>, then
    /// <see cref="BountyKillHandler"/> — which is well under a second in normal running, and this
    /// leaves room for a retry on top. All it costs is that the "died to the environment" broadcast
    /// lands a few seconds after the death, which nobody in the lobby can tell apart from the bridge's
    /// own lag.</para>
    /// </summary>
    public static readonly TimeSpan KillfeedGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The target died. Hands the killfeed right of way and schedules the fallback resolution for
    /// <see cref="KillfeedGracePeriod"/> later.
    /// </summary>
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

        await bus.ScheduleAsync(new ResolveBountyDeathEvent
        {
            PlayerId = playerId,
            QuestInstanceId = instance.Id,
        }, KillfeedGracePeriod);
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
