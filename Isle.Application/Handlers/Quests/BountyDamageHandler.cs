using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Quests;

/// <summary>
/// Credits damage dealt to a marked player.
///
/// <para>This runs on the busiest event on the bus, so it is written to get out of the way fast: the
/// only question asked before any work happens is whether the victim is currently marked, and that is
/// a single Redis hash read against a key the bounty system keeps warm anyway. Damage to anybody else
/// — which is nearly all damage on the server — costs one lookup and returns.</para>
///
/// <para>The ledger it writes to is what pays the players who wore the target down but did not land
/// the final hit, and what ranks them when the template carries <c>Top3</c> rewards.</para>
/// </summary>
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
