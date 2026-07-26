using Isle.Api.Services.Quests;
using Isle.Contracts.Events.Player;

namespace Isle.Api.Handlers.Quests;

/// <summary>Settles an open <c>Hunt</c> quest when someone makes a kill in its region.</summary>
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
