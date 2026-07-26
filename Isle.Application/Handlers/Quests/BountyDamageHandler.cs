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

        // Stamped with our own clock rather than the bridge's. The only thing this time is ever
        // compared against is DateTimeOffset.UtcNow, in BountyService.PvpAttributionWindow, so the two
        // must come off the same clock: the bridge's ts carries no documented unit — the SDK stamps
        // outbound ts in seconds — and reading seconds as milliseconds would put every hit in 1970 and
        // silently make the window unmatchable. The damage feed is a buffered in-process queue, so the
        // few milliseconds between the swing landing and this line running do not register against a
        // twenty-second window. @event.OccurredAt stays on the contract as the bridge's own record.
        await ledger.RecordAsync(mark.QuestInstanceId, @event.AttackerSteamId, @event.Damage, DateTimeOffset.UtcNow);
    }
}
