using Guild.Application.Dtos.Response;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The other half of the inbox: not "what have I not read" but "what is waiting on me".
/// </summary>
public class InboxTaskService(
    MicroserviceContext ctx,
    GuildPermissionService permissions)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 50;

    /// <summary>How far ahead a chore counts as waiting on you.</summary>
    private static readonly TimeSpan ChoreLookahead = TimeSpan.FromDays(1);

    /// <summary>Ceiling per source before filtering.</summary>
    private const int CandidatesPerSource = 100;

    /// <summary>Ceiling on the badge count, matching <see cref="InboxService.MaxSummaryCount"/>.</summary>
    public const int MaxSummaryCount = 99;

    /// <summary>One candidate before feature and permission filtering.</summary>
    internal sealed record TaskRow(
        InboxTaskKind Kind,
        string TargetId,
        string GuildId,
        string GuildName,
        string ChannelId,
        string ChannelName,
        ChannelType ChannelType,
        string? CategoryId,
        string? CategoryName,
        string Title,
        string Subtitle,
        DateTimeOffset? DueAt,
        int GraceHours,
        DateTimeOffset CreatedAt);

    public async Task<InboxTaskPageDto> GetTasksAsync(string userId, int limit)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);

        var rows = await CollectAsync(userId);

        var tasks = rows.Take(limit).Select(Project).ToList();

        return new InboxTaskPageDto
        {
            Tasks = tasks,
            Truncated = rows.Count > limit,
        };
    }

    /// <summary>The badge number.</summary>
    public async Task<int> CountAsync(string userId)
    {
        var rows = await CollectAsync(userId);
        return Math.Min(rows.Count, MaxSummaryCount);
    }

    /// <summary>Every candidate from all three sources, filtered and merged into display order.</summary>
    private async Task<List<TaskRow>> CollectAsync(string userId)
    {
        var now = DateTimeOffset.UtcNow;

        var chores = await BuildChoreQuery(ctx, userId, now + ChoreLookahead)
            .Take(CandidatesPerSource).ToListAsync();
        var decisions = await BuildDecisionQuery(ctx, userId, now)
            .Take(CandidatesPerSource).ToListAsync();
        var assignments = await BuildAssignmentQuery(ctx, userId)
            .Take(CandidatesPerSource).ToListAsync();

        var rows = new List<TaskRow>(chores.Count + decisions.Count + assignments.Count);
        rows.AddRange(await KeepVisibleAsync(userId, chores, GuildFeatures.Chores));
        rows.AddRange(await KeepVisibleAsync(userId, decisions, GuildFeatures.Decisions));
        rows.AddRange(await KeepVisibleAsync(userId, assignments, GuildFeatures.Lists));

        // Anything with a deadline sorts ahead of anything without, soonest first; the undated tail
        // falls back to age.
        return rows
            .OrderBy(r => r.DueAt ?? DateTimeOffset.MaxValue)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.TargetId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Chores assigned to the caller and still owed.</summary>
    internal static IQueryable<TaskRow> BuildChoreQuery(
        MicroserviceContext ctx, string userId, DateTimeOffset horizon) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join occurrence in ctx.ChoreOccurrences.AsNoTracking()
                on member.GuildId equals occurrence.GuildId
            where occurrence.AssignedUserId == userId
                  && occurrence.CompletedAt == null
                  && occurrence.SkippedAt == null
                  && occurrence.DueAt <= horizon
            join chore in ctx.Chores.AsNoTracking() on occurrence.ChoreId equals chore.Id
            join channel in ctx.Channels.AsNoTracking() on occurrence.ChannelId equals channel.Id
            orderby occurrence.DueAt
            select new TaskRow(
                InboxTaskKind.ChoreDue,
                occurrence.Id,
                channel.GuildId,
                channel.Guild.Name,
                channel.Id,
                channel.Name,
                channel.Type,
                channel.CategoryId,
                channel.Category != null ? channel.Category.Name : null,
                chore.Title,
                "Your turn",
                occurrence.DueAt,
                chore.GraceHours,
                occurrence.CreatedAt);

    /// <summary>Open decisions the caller has not voted on.</summary>
    internal static IQueryable<TaskRow> BuildDecisionQuery(
        MicroserviceContext ctx, string userId, DateTimeOffset now) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join decision in ctx.Decisions.AsNoTracking() on member.GuildId equals decision.GuildId
            where decision.Status == DecisionStatus.Open
                  && (decision.ClosesAt == null || decision.ClosesAt > now)
                  && !decision.Votes.Any(v => v.UserId == userId)
            join channel in ctx.Channels.AsNoTracking() on decision.ChannelId equals channel.Id
            orderby decision.ClosesAt
            select new TaskRow(
                InboxTaskKind.DecisionVote,
                decision.Id,
                channel.GuildId,
                channel.Guild.Name,
                channel.Id,
                channel.Name,
                channel.Type,
                channel.CategoryId,
                channel.Category != null ? channel.Category.Name : null,
                decision.Title,
                "Waiting on your vote",
                decision.ClosesAt,
                0,
                decision.CreatedAt);

    /// <summary>Unchecked list items assigned to the caller.</summary>
    internal static IQueryable<TaskRow> BuildAssignmentQuery(MicroserviceContext ctx, string userId) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join item in ctx.ListItems.AsNoTracking() on member.GuildId equals item.GuildId
            where item.AssigneeUserId == userId && !item.IsChecked
            join channel in ctx.Channels.AsNoTracking() on item.ChannelId equals channel.Id
            orderby item.CreatedAt
            select new TaskRow(
                InboxTaskKind.ListAssignment,
                item.Id,
                channel.GuildId,
                channel.Guild.Name,
                channel.Id,
                channel.Name,
                channel.Type,
                channel.CategoryId,
                channel.Category != null ? channel.Category.Name : null,
                item.Text,
                "Assigned to you",
                null,
                0,
                item.CreatedAt);

    /// <summary>Drops rows whose module has since been switched off, and rows in channels the
    /// caller can no longer see. Both are resolved once per guild rather than once per row.</summary>
    private async Task<List<TaskRow>> KeepVisibleAsync(
        string userId, List<TaskRow> rows, GuildFeatures feature)
    {
        if (rows.Count == 0) return rows;

        var kept = new List<TaskRow>(rows.Count);

        foreach (var group in rows.GroupBy(r => r.GuildId, StringComparer.Ordinal))
        {
            if (!await permissions.IsFeatureEnabledAsync(group.Key, feature)) continue;

            var channelIds = group.Select(r => r.ChannelId).Distinct(StringComparer.Ordinal).ToList();

            var visible = await permissions.FilterChannelsWithPermissionAsync(
                userId, group.Key, channelIds, Permissions.ViewChannel);

            kept.AddRange(group.Where(r => visible.Contains(r.ChannelId)));
        }

        return kept;
    }

    private static InboxTaskDto Project(TaskRow row) => new()
    {
        Kind = row.Kind,
        TargetId = row.TargetId,
        Breadcrumb = new InboxBreadcrumbDto
        {
            GuildId = row.GuildId,
            GuildName = row.GuildName,
            GuildIconUrl = InboxService.GuildIconUrl(row.GuildId),
            GuildIconThumbnailUrl = InboxService.GuildIconThumbnailUrl(row.GuildId),
            CategoryId = row.CategoryId,
            CategoryName = row.CategoryName,
            ChannelId = row.ChannelId,
            ChannelName = row.ChannelName,
            ChannelType = (int)row.ChannelType,
        },
        Title = row.Title,
        Subtitle = row.Subtitle,
        DueAt = row.DueAt,
        // Grace applies to chores and is zero for everything else, so one expression covers all
        // three kinds: a decision is overdue the moment it closes, a chore not until its grace
        // period runs out.
        IsOverdue = row.DueAt is not null
                    && row.DueAt.Value.AddHours(row.GraceHours) < DateTimeOffset.UtcNow,
    };
}
