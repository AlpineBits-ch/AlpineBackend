using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Quests;

/// <summary>Credits damage dealt to a marked player.</summary>
public class BountyDamageHandler
{
    public static async Task Handle(
        PlayerDamagedEvent @event,
        BountyRegistry registry,
        BountyParticipantLedger ledger,
        CancellationToken ct)
    {
        var mark = await registry.GetBySteamAsync(@event.VictimSteamId);
        if (mark is null)
            return;

        // Self-damage is filtered at ingestion, but a target hitting themselves through some other
        // path must never earn them credit for their own bounty.
        if (mark.SteamId == @event.AttackerSteamId)
            return;

        var at = @event.OccurredAt > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(@event.OccurredAt)
            : DateTimeOffset.UtcNow;

        await ledger.RecordAsync(mark.QuestInstanceId, @event.AttackerSteamId, @event.Damage, at);
    }
}
