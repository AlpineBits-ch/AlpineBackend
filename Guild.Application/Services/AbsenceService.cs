using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>One occurrence that changed hands because its assignee is going away, with what the
/// new assignee needs to be told about it.</summary>
public record ChoreHandover(ChoreOccurrence Occurrence, string ChoreTitle, string NewAssigneeUserId);

/// <summary>Declared absences, and what the rota does about them.</summary>
public class AbsenceService(MicroserviceContext ctx, ChoreRotationService rotation)
{
    /// <summary>The longest single absence.</summary>
    public const int MaxAbsenceDays = 180;

    /// <summary>How many live absences one member may hold.</summary>
    public const int MaxLiveAbsencesPerMember = 20;

    public const int MaxNoteLength = 100;

    // ── Reads, shared with the rotation ──────────────────────────────────────

    /// <summary>Who in this guild is away at <paramref name="instant"/>.</summary>
    public static async Task<HashSet<string>> AbsentUserIdsAsync(
        MicroserviceContext ctx, GuildPermissionService? permissions,
        string guildId, DateTimeOffset instant)
    {
        if (!await IsAbsenceTrackedAsync(permissions, guildId)) return [];

        var userIds = await ctx.Set<MemberAbsence>()
            .AsNoTracking()
            .Where(a => a.GuildId == guildId && a.StartAt <= instant && a.EndAt > instant)
            .Select(a => a.UserId)
            .Distinct()
            .ToListAsync();

        return new HashSet<string>(userIds, StringComparer.Ordinal);
    }

    /// <summary>Every absence in this guild overlapping [<paramref name="from"/>,
    /// <paramref name="to"/>). Empty when the Presence module is off.</summary>
    public static async Task<List<MemberAbsence>> InWindowAsync(
        MicroserviceContext ctx, GuildPermissionService? permissions,
        string guildId, DateTimeOffset from, DateTimeOffset to)
    {
        if (!await IsAbsenceTrackedAsync(permissions, guildId)) return [];

        return await ctx.Set<MemberAbsence>()
            .AsNoTracking()
            .Where(a => a.GuildId == guildId && a.StartAt < to && a.EndAt > from)
            .ToListAsync();
    }

    /// <summary>A null permission service means the caller could not be given one - the pre-absence
    /// unit tests construct the rotation with a context alone - and the safe reading of "I cannot
    /// evaluate the feature gate" is that the feature is off.</summary>
    private static async Task<bool> IsAbsenceTrackedAsync(GuildPermissionService? permissions, string guildId) =>
        permissions is not null && await permissions.IsFeatureEnabledAsync(guildId, GuildFeatures.Presence);

    /// <summary>One member's absences overlapping a window, newest first.</summary>
    public async Task<List<MemberAbsence>> ListAsync(
        string guildId, DateTimeOffset from, DateTimeOffset to, string? userId = null)
    {
        var query = ctx.Set<MemberAbsence>()
            .AsNoTracking()
            .Where(a => a.GuildId == guildId && a.StartAt < to && a.EndAt > from);

        if (userId is not null) query = query.Where(a => a.UserId == userId);

        return await query.OrderBy(a => a.StartAt).ToListAsync();
    }

    public async Task<MemberAbsence?> FindAsync(string absenceId) =>
        await ctx.Set<MemberAbsence>().FirstOrDefaultAsync(a => a.Id == absenceId);

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>Whatever is wrong with a proposed window, or null if nothing is.</summary>
    public async Task<string?> ValidateAsync(
        string guildId, string userId, DateTimeOffset startAt, DateTimeOffset endAt,
        string? note, string? ignoreAbsenceId)
    {
        if (endAt <= startAt) return "EndAt must be after StartAt";

        if ((endAt - startAt).TotalDays > MaxAbsenceDays)
            return $"An absence may not be longer than {MaxAbsenceDays} days";

        if (note is { Length: > MaxNoteLength })
            return $"Note must be {MaxNoteLength} characters or fewer";

        var existing = await ctx.Set<MemberAbsence>()
            .AsNoTracking()
            .Where(a => a.GuildId == guildId && a.UserId == userId && a.Id != ignoreAbsenceId)
            .ToListAsync();

        if (existing.Any(a => a.Overlaps(startAt, endAt)))
            return "That overlaps an absence you have already declared";

        // Counted against what is still ahead: a member who has been in the house for five years
        // has a long history of holidays and none of them is a reason to refuse the next one.
        var now = DateTimeOffset.UtcNow;
        if (existing.Count(a => a.EndAt > now) >= MaxLiveAbsencesPerMember)
            return $"You can have at most {MaxLiveAbsencesPerMember} upcoming absences at a time";

        return null;
    }

    // ── The handover ─────────────────────────────────────────────────────────

    /// <summary>
    /// Hands the chores <paramref name="absence"/>'s member is holding over the window to whoever
    /// is next lightest and actually here for them.
    /// </summary>
    public async Task<List<ChoreHandover>> StageChoreHandoverAsync(MemberAbsence absence)
    {
        var open = await ctx.ChoreOccurrences
            .Where(o => o.GuildId == absence.GuildId
                        && o.AssignedUserId == absence.UserId
                        && o.CompletedAt == null
                        && o.SkippedAt == null
                        && o.DueAt >= absence.StartAt
                        && o.DueAt < absence.EndAt)
            .ToListAsync();

        if (open.Count == 0) return [];

        var choreIds = open.Select(o => o.ChoreId).Distinct().ToList();
        var chores = await ctx.Chores
            .Where(c => choreIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);

        var handovers = new List<ChoreHandover>();

        foreach (var occurrence in open)
        {
            if (!chores.TryGetValue(occurrence.ChoreId, out var chore)) continue;

            var replacement = await rotation.PickNextAssigneeAsync(
                chore, excludeUserId: absence.UserId, dueAt: occurrence.DueAt);

            // PickNextAssigneeAsync returns the excluded member when they are the only one in the
            // pool, which here means there is nobody to hand it to.
            if (replacement is null || replacement == absence.UserId) continue;

            occurrence.AssignedUserId = replacement;

            // Both stamps released.
            occurrence.RemindedAt = null;
            occurrence.NudgedAt = null;

            handovers.Add(new ChoreHandover(occurrence, chore.Title, replacement));
        }

        return handovers;
    }
}
