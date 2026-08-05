using Isle.Api.Services.Rewards;
using Isle.Contracts.Events.KingOfTheHill;
using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Isle.Api.Services.KingOfTheHill;

/// <summary>
/// Resolves a King of the Hill match: persists the run, pays out, announces, and tears down the
/// Redis state.
/// </summary>
public sealed class KingOfTheHillCompletionService(
    MicroserviceContext context,
    KingOfTheHillControlLedger ledger,
    KingOfTheHillMatchStateStore stateStore,
    RewardGranter rewards,
    KingOfTheHillAnnouncer announcer,
    IMessageBus bus,
    ILogger<KingOfTheHillCompletionService> logger)
{
    /// <summary>Standings, after the winner, that still place above the participation tier.</summary>
    private const int TopTierSize = 3;

    /// <summary>Settles a match that timed out or was won early by a held-alone streak.</summary>
    public async Task ResolveAsync(GameModeInstance instance, KothTickOutcome outcome, CancellationToken ct = default)
    {
        instance.BeginResolving();

        var standings = instance.Behavior.GetStandings(instance);

        context.GameModeRuns.Add(new GameModeRun(instance, standings));
        await context.SaveChangesAsync(ct);

        var winnerName = standings.Count > 0 ? await ResolveNameAsync(standings[0].PlayerId, ct) : null;
        var paidPlayerIds = await PayOutAsync(instance, standings, ct);

        await ledger.ClearAsync(instance.InstanceId);
        await stateStore.ClearAsync();

        if (outcome == KothTickOutcome.HeldAloneToWin && winnerName is not null)
            await announcer.AnnounceWonAsync(instance.Definition, winnerName, ct);
        else
            await announcer.AnnounceTimedOutAsync(instance.Definition, winnerName, ct);

        instance.EnterCooldown();

        logger.LogInformation("KOTH {InstanceId} resolved ({Outcome}): {Count} standing(s), {Paid} paid",
            instance.InstanceId, outcome, standings.Count, paidPlayerIds.Count);

        await bus.PublishAsync(new KothMatchResolvedEvent
        {
            DefinitionId = instance.Definition.Id,
            InstanceId = instance.InstanceId,
            WinnerPlayerId = standings.Count > 0 ? standings[0].PlayerId : null,
            PaidPlayerIds = paidPlayerIds,
        });
    }

    /// <summary>Cancels the running match with no payout and no <c>GameModeRun</c> row - used by <c>!kothadmin end</c>.</summary>
    public async Task<bool> CancelAsync(CancellationToken ct = default)
    {
        var marker = await stateStore.ReadAsync();
        if (marker is null)
            return false;

        await ledger.ClearAsync(marker.InstanceId);
        await stateStore.ClearAsync();
        await announcer.AnnounceCancelledAsync(ct);

        await bus.PublishAsync(new KothMatchCancelledEvent
        {
            DefinitionId = marker.DefinitionId,
            InstanceId = marker.InstanceId,
        });

        return true;
    }

    private async Task<List<string>> PayOutAsync(
        GameModeInstance instance, IReadOnlyList<ParticipantStanding> standings, CancellationToken ct)
    {
        var rewardTable = instance.Definition.Rewards;
        if (standings.Count == 0)
            return [];

        var paid = new List<string>();

        foreach (var standing in standings)
        {
            var rank = standing.Rank == 1
                ? RankRequirement.Winner
                : standing.Rank <= TopTierSize
                    ? RankRequirement.Top3
                    : RankRequirement.AllParticipants;

            // The definition's own tiered rows, plus whatever mode-specific bonus this standing earns
            // (e.g. KOTH's held-the-hill bonus) - the bonus is pre-computed for this exact standing, so
            // it is granted as-is rather than filtered through RewardGranter.Applies a second time.
            var rewardRows = rewardTable
                .Where(r => RewardGranter.Applies(r, rank))
                .Concat(instance.Behavior.GetRewards(instance, standing))
                .ToList();

            if (rewardRows.Count == 0)
                continue;

            var granted = await rewards.GrantAsync(standing.PlayerId, rewardRows, ct);
            if (granted.Count == 0)
            {
                // Placed but paid nothing - almost always the granter finding no live pawn, because the
                // player logged off between their last sample and the match resolving.
                logger.LogInformation("KOTH {InstanceId}: {PlayerId} placed {Rank} but received nothing",
                    instance.InstanceId, standing.PlayerId, standing.Rank);
                continue;
            }

            paid.Add(standing.PlayerId);

            var player = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == standing.PlayerId, ct);
            if (!string.IsNullOrWhiteSpace(player?.SteamId))
                await announcer.WhisperAsync(player.SteamId, $"King of the Hill: {string.Join(", ", granted)}.", ct);
        }

        return paid;
    }

    private async Task<string?> ResolveNameAsync(string playerId, CancellationToken ct)
    {
        var player = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId, ct);
        return player?.InGameName ?? player?.FriendlyId;
    }
}
