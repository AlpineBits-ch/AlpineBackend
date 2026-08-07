using Guild.Contracts;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;

namespace Guild.Application.Services;

/// <summary>
/// The two chore alerts that are about people rather than about the clock: somebody being asked,
/// and somebody being handed a job that was not theirs.
/// </summary>
public class ChoreAlertService(
    HouseholdNotifier notifier,
    GuildPermissionService permissions,
    ILogger<ChoreAlertService> logger)
{
    /// <summary>Somebody in the house asked, and the app did the asking.</summary>
    public const string KindNudge = "chore.nudge";

    /// <summary>A chore moved to you because whoever had it is away.</summary>
    public const string KindReassigned = "chore.reassigned";

    /// <summary>The shortest gap between two nudges about the same occurrence.</summary>
    public static readonly TimeSpan NudgeCooldown = TimeSpan.FromHours(12);

    /// <summary>Asks the assignee, without saying who is asking.</summary>
    public async Task NudgeAsync(ChoreOccurrence occurrence, string choreTitle)
    {
        await SafelyAsync(nameof(NudgeAsync), async () =>
        {
            var recipients = await ViewersOfAsync(occurrence.ChannelId, [occurrence.AssignedUserId]);
            if (recipients.Count == 0) return;

            await notifier.AlertAsync(
                occurrence.GuildId, occurrence.ChannelId, recipients,
                KindNudge,
                AlertText.Raw(choreTitle),
                AlertText.Loc(HouseholdLocKeys.ChoreNudgeBody, "Still waiting on this one at home."),
                occurrence.Id,
                new { occurrence.ChoreId, occurrence.DueAt });
        });
    }

    /// <summary>Tells whoever inherited a chore that they now have it.</summary>
    public async Task ReassignedAsync(IReadOnlyList<ChoreHandover> handovers)
    {
        if (handovers.Count == 0) return;

        await SafelyAsync(nameof(ReassignedAsync), async () =>
        {
            // One alert per occurrence rather than one per new assignee.
            foreach (var handover in handovers.Take(MaxAlerts))
            {
                var recipients = await ViewersOfAsync(
                    handover.Occurrence.ChannelId, [handover.NewAssigneeUserId]);

                if (recipients.Count == 0) continue;

                await notifier.AlertAsync(
                    handover.Occurrence.GuildId, handover.Occurrence.ChannelId, recipients,
                    KindReassigned,
                    AlertText.Raw(handover.ChoreTitle),
                    AlertText.Loc(HouseholdLocKeys.ChoreReassignedBody,
                        "Picked up from a flatmate who's away."),
                    handover.Occurrence.Id,
                    new { handover.Occurrence.ChoreId, handover.Occurrence.DueAt });
            }
        });
    }

    /// <summary>Ceiling on how many handover alerts one absence may produce.</summary>
    private const int MaxAlerts = 25;

    private async Task<List<string>> ViewersOfAsync(string channelId, IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0) return [];

        return await permissions.FilterUsersWithChannelPermissionAsync(
            channelId, userIds, Permissions.ViewChannel);
    }

    private async Task SafelyAsync(string operation, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Chore alert {Operation} could not be delivered", operation);
        }
    }
}
