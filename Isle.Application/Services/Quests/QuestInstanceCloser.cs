using Isle.Domain.Aggregates;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Quests;

/// <summary>
/// The one way a quest instance is allowed to be closed by a racing caller.
/// </summary>
public static class QuestInstanceCloser
{
    /// <summary>
    /// Claims the right to close this instance, atomically, and returns whether this caller won.
    ///
    /// <para>Terminal quest events arrive on independent feeds and Wolverine hands them to separate
    /// handlers in separate scopes with separate <c>MicroserviceContext</c>s.
    /// <see cref="QuestInstance.TryClose"/> guards one in-memory instance and cannot see the other
    /// scope, so both could pass it — which used to mean the state was written twice and paid once, and
    /// now that every path pays would mean paid twice. This is the real gate: a conditional UPDATE that
    /// only matches while the row is still active, so exactly one caller gets a row back and everyone
    /// else walks away. Under Postgres read committed the loser blocks on the row lock, re-evaluates
    /// the predicate after the winner commits, and matches nothing.</para>
    ///
    /// <para>Shared by both racing subsystems: a bounty races a killfeed claim against a death
    /// resolution against the expiry sweep, and a hunt races a kill against the same sweep. They need
    /// the identical gate, and two copies of a concurrency primitive is two chances for one of them to
    /// drift.</para>
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
