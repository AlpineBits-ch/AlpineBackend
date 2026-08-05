using Isle.Api.Services.KingOfTheHill;
using Isle.Api.Services.Rewards;
using Isle.Contracts.Events.Player;
using Isle.Domain.ValueObjects;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Handlers.KingOfTheHill;

/// <summary>Pays out for killing whoever currently holds the hill alone.</summary>
public class KingOfTheHillKillHandler
{
    /// <summary>
    /// Fixed rather than template-authored: there is no <see cref="RankRequirement"/> tier for "killed
    /// the holder", so this has nowhere in <c>GameModeDefinition.Rewards</c> to live - same reasoning as
    /// <c>BountyService.DefaultClaimRewards</c>.
    /// </summary>
    private static readonly RewardConfig[] HolderKillRewards =
    [
        new() { RewardType = RewardType.Xp, Amount = 1500 },
        new() { RewardType = RewardType.FullHealth },
    ];

    public static async Task Handle(
        PlayerKillEvent @event,
        KingOfTheHillMatchStateStore stateStore,
        KingOfTheHillControlLedger ledger,
        RewardGranter rewards,
        KingOfTheHillAnnouncer announcer,
        MicroserviceContext context,
        ILogger<KingOfTheHillKillHandler> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@event.KilerId) || @event.KilerId == @event.VictimId)
            return;

        var marker = await stateStore.ReadAsync();
        if (marker is null)
            return;

        var holder = await ledger.GetHolderStreakAsync(marker.InstanceId);
        if (holder is null)
            return;

        var victim = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == @event.VictimId, ct);
        if (victim?.SteamId != holder.Value.SteamId)
            return;

        var granted = await rewards.GrantAsync(@event.KilerId, HolderKillRewards, ct);
        if (granted.Count == 0)
            return;

        var killer = await context.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == @event.KilerId, ct);
        if (!string.IsNullOrWhiteSpace(killer?.SteamId))
            await announcer.WhisperAsync(killer.SteamId, $"Took the hill's holder down: {string.Join(", ", granted)}.", ct);

        logger.LogInformation("KOTH {InstanceId}: {KillerId} killed holder {VictimId}, paid {Rewards}",
            marker.InstanceId, @event.KilerId, @event.VictimId, string.Join(", ", granted));
    }
}
