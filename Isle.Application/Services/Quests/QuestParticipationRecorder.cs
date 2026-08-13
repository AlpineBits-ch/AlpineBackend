using Isle.Domain.Aggregates;
using Isle.Domain.Entity;
using Isle.Domain.Enums;
using Isle.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Isle.Api.Services.Quests;

/// <summary>One player's showing, as the resolution worked it out.</summary>
/// <param name="PlayerId">Resolved before it gets here; the ledger's Steam ids never reach the table.</param>
/// <param name="Progress">Dwell samples for an exploration, kills for a hunt.</param>
/// <param name="Rank">The tier earned, or null when they turned up but did not qualify.</param>
/// <param name="Rewards">Exactly what reached them. Empty means qualified-but-unpaid, which is a real outcome.</param>
public sealed record ResolvedParticipant(
    string PlayerId,
    int Progress,
    RankRequirement? Rank,
    IReadOnlyList<string> Rewards);

/// <summary>
/// Writes the durable record of who took part in a quest run and what came of it.
/// </summary>
public sealed class QuestParticipationRecorder(
    MicroserviceContext context,
    ILogger<QuestParticipationRecorder> logger)
{
    /// <summary>Records everyone who took part in <paramref name="instance"/>.</summary>
    /// <param name="goal">What the run asked for, snapshotted onto every row.</param>
    public async Task RecordAsync(
        QuestInstance instance,
        IReadOnlyCollection<ResolvedParticipant> participants,
        int goal,
        CancellationToken ct = default)
    {
        if (participants.Count == 0)
            return;

        try
        {
            var playerIds = participants.Select(participant => participant.PlayerId).Distinct(StringComparer.Ordinal).ToList();

            var already = await context.QuestParticipations
                .AsNoTracking()
                .Where(row => row.QuestInstanceId == instance.Id && playerIds.Contains(row.PlayerId))
                .Select(row => row.PlayerId)
                .ToListAsync(ct);

            var written = 0;
            foreach (var participant in participants)
            {
                if (already.Contains(participant.PlayerId, StringComparer.Ordinal))
                    continue;

                context.QuestParticipations.Add(QuestParticipation.Record(
                    instance.Id,
                    participant.PlayerId,
                    instance.State,
                    participant.Progress,
                    goal,
                    participant.Rank,
                    participant.Rewards));

                written++;
            }

            if (written == 0)
                return;

            await context.SaveChangesAsync(ct);

            logger.LogDebug("Recorded {Count} participation row(s) for quest {InstanceId}", written, instance.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record participation for quest {InstanceId}", instance.Id);
        }
    }
}
