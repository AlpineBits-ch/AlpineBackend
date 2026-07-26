using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Quests;

/// <summary>The one way a quest instance is allowed to be closed by a racing caller.</summary>
public static class QuestInstanceCloser
{
    /// <summary>
    /// Claims the right to close this instance, atomically, and returns whether this caller won.
    /// </summary>
    public static async Task<bool> TryCloseQuestAtomicallyAsync(
        this MicroserviceContext context,
        QuestInstance instance,
        QuestInstanceState state,
        string? completedByPlayerId,
        CancellationToken ct)
    {
        if (state == QuestInstanceState.Active)
            return false;

        var endedAt = DateTimeOffset.UtcNow;

        var rows = await context.QuestInstances
            .Where(i => i.Id == instance.Id && i.State == QuestInstanceState.Active)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.State, state)
                .SetProperty(i => i.CompletedByPlayerId, completedByPlayerId)
                .SetProperty(i => i.EndedAt, endedAt)
                .SetProperty(i => i.UpdatedAt, endedAt), ct);

        if (rows == 0)
            return false;

        // Bring the tracked entity in step with the row: announcements and resolved events are both
        // built off it after this returns.
        instance.State = state;
        instance.CompletedByPlayerId = completedByPlayerId;
        instance.EndedAt = endedAt;
        instance.UpdatedAt = endedAt;

        return true;
    }
}
