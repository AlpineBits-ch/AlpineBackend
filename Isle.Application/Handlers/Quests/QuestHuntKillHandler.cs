using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Quests;

/// <summary>
/// Settles an open <c>Hunt</c> quest when someone makes a kill in its region.
///
/// <para>A separate handler rather than a branch inside <see cref="BountyKillHandler"/>: Wolverine is
/// configured <c>MultipleHandlerBehavior.Separated</c>, so this runs alongside the spree detector and
/// the kill log on the same message without any of the three knowing about the others. A hunt claim and
/// a bounty claim are also not exclusive — killing a marked player inside a hunt's region legitimately
/// settles both.</para>
/// </summary>
public class QuestHuntKillHandler
{
    public static async Task Handle(
        PlayerKillEvent @event,
        QuestCompletionService quests,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(@event.KilerId) || string.IsNullOrWhiteSpace(@event.VictimId))
            return;

        // A hunt asks players to go and eat, not to drown. Self-inflicted deaths claim nothing.
        if (@event.KilerId == @event.VictimId)
            return;

        await quests.TryCompleteHuntAsync(@event.KilerId, ct);
    }
}
