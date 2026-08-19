using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
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

    /// <summary>How far ahead a bill counts as waiting on you.</summary>
    private static readonly TimeSpan BillLookahead = TimeSpan.FromDays(RecurringExpense.DefaultLeadDays);

    /// <summary>How much of the cook's own grace a meal gets before it counts as missed.</summary>
    private const int MealGraceHours = 24;

    /// <summary>Ceiling per source before filtering.</summary>
    private const int CandidatesPerSource = 100;

    /// <summary>How long a dismissal outlives the row it was about.</summary>
    private static readonly TimeSpan DismissalRetention = TimeSpan.FromDays(90);

    /// <summary>Ceiling on the badge count, matching <see cref="InboxService.MaxSummaryCount"/>.</summary>
    public const int MaxSummaryCount = 99;

    /// <summary>One candidate before feature and permission filtering. The channel is null for a
    /// guild-scoped row: an approval queue lives in a guild's cast rather than in a channel.</summary>
    internal sealed record TaskRow(
        InboxTaskKind Kind,
        string TargetId,
        string GuildId,
        string GuildName,
        string? ChannelId,
        string? ChannelName,
        ChannelType? ChannelType,
        string? CategoryId,
        string? CategoryName,
        string Title,
        string Subtitle,
        DateTimeOffset? DueAt,
        int GraceHours,
        DateTimeOffset CreatedAt,
        DateOnly? PlanDate = null,
        DateTimeOffset? FreshAt = null);

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

    /// <summary>Every candidate from every source, filtered and merged into display order.</summary>
    private async Task<List<TaskRow>> CollectAsync(string userId)
    {
        var now = DateTimeOffset.UtcNow;

        // The plan's calendar today, and tomorrow with it: a cook wants the evening they are
        // cooking to appear before the morning of it, and a plan has no timezone to be precise
        // about anyway - see MealPlanEntry.Date.
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var chores = await BuildChoreQuery(ctx, userId, now + ChoreLookahead)
            .Take(CandidatesPerSource).ToListAsync();
        var decisions = await BuildDecisionQuery(ctx, userId, now)
            .Take(CandidatesPerSource).ToListAsync();
        var assignments = await BuildAssignmentQuery(ctx, userId)
            .Take(CandidatesPerSource).ToListAsync();
        var bills = await BuildBillQuery(ctx, userId, now + BillLookahead)
            .Take(CandidatesPerSource).ToListAsync();
        var meals = await BuildMealQuery(ctx, userId, today, today.AddDays(1))
            .Take(CandidatesPerSource).ToListAsync();
        var maintenance = await BuildMaintenanceQuery(ctx, userId, now)
            .Take(CandidatesPerSource).ToListAsync();
        var turns = await BuildSceneTurnQuery(ctx, userId, now)
            .Take(CandidatesPerSource).ToListAsync();
        var reviews = await BuildPersonaReviewQuery(ctx, userId)
            .Take(CandidatesPerSource).ToListAsync();
        var sentBack = await BuildPersonaChangesRequestedQuery(ctx, userId)
            .Take(CandidatesPerSource).ToListAsync();
        var guildSentBack = await BuildGuildPersonaChangesRequestedQuery(ctx, userId)
            .Take(CandidatesPerSource).ToListAsync();

        // The one conversion the queries cannot do. See TaskRow's PlanDate.
        meals = meals
            .Select(r => r with
            {
                DueAt = r.PlanDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            })
            .ToList();

        var rows = new List<TaskRow>(
            chores.Count + decisions.Count + assignments.Count
            + bills.Count + meals.Count + maintenance.Count
            + turns.Count + reviews.Count + sentBack.Count + guildSentBack.Count);

        rows.AddRange(await KeepVisibleAsync(userId, chores, GuildFeatures.Chores));
        rows.AddRange(await KeepVisibleAsync(userId, decisions, GuildFeatures.Decisions));
        rows.AddRange(await KeepVisibleAsync(userId, assignments, GuildFeatures.Lists));

        // Bills are gated on the ledger rather than on a module of their own: a bill is an expense
        // before it is one, so a house with the ledger switched off has nothing to be told about.
        rows.AddRange(await KeepVisibleAsync(userId, bills, GuildFeatures.Ledger));
        rows.AddRange(await KeepVisibleAsync(userId, meals, GuildFeatures.Meals));
        rows.AddRange(await KeepVisibleAsync(userId, maintenance, GuildFeatures.Maintenance));

        // A scene is a channel, so the same ViewChannel rule applies; the two approval rows are not,
        // and are cut by the module permission the queue itself is gated on.
        rows.AddRange(await KeepVisibleAsync(userId, turns, GuildFeatures.Scenes));
        rows.AddRange(await KeepPermittedAsync(
            userId, reviews, GuildFeatures.Personas, ModulePermissions.ApprovePersonas));
        rows.AddRange(await KeepPermittedAsync(userId, sentBack, GuildFeatures.Personas));
        rows.AddRange(await KeepPermittedAsync(
            userId, guildSentBack, GuildFeatures.Personas, ModulePermissions.ManageAnyPersona));

        rows = await DropDismissedAsync(userId, rows);

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

    /// <summary>Pending bills the caller has a share in, due or nearly.</summary>
    internal static IQueryable<TaskRow> BuildBillQuery(
        MicroserviceContext ctx, string userId, DateTimeOffset horizon) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join occurrence in ctx.Set<BillOccurrence>().AsNoTracking()
                on member.GuildId equals occurrence.GuildId
            where occurrence.Status == BillStatus.Pending && occurrence.DueAt <= horizon
            join template in ctx.Set<RecurringExpense>().AsNoTracking()
                on occurrence.RecurringExpenseId equals template.Id
            where template.Shares.Any(s => s.UserId == userId)
                  || (template.SplitKind == ExpenseSplitKind.Equal && !template.Shares.Any())
            join channel in ctx.Channels.AsNoTracking() on occurrence.ChannelId equals channel.Id
            orderby occurrence.DueAt
            select new TaskRow(
                InboxTaskKind.BillDue,
                occurrence.Id,
                channel.GuildId,
                channel.Guild.Name,
                channel.Id,
                channel.Name,
                channel.Type,
                channel.CategoryId,
                channel.Category != null ? channel.Category.Name : null,
                occurrence.Description,
                "You have a share",
                occurrence.DueAt,
                0,
                occurrence.CreatedAt);

    /// <summary>Meals the caller is down to cook today or tomorrow.</summary>
    internal static IQueryable<TaskRow> BuildMealQuery(
        MicroserviceContext ctx, string userId, DateOnly fromDate, DateOnly toDate) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join entry in ctx.Set<MealPlanEntry>().AsNoTracking()
                on member.GuildId equals entry.GuildId
            where entry.CookUserId == userId && entry.Date >= fromDate && entry.Date <= toDate
            join channel in ctx.Channels.AsNoTracking() on entry.ChannelId equals channel.Id
            join recipe in ctx.Set<Recipe>().AsNoTracking() on entry.RecipeId equals recipe.Id into recipes
            from recipe in recipes.DefaultIfEmpty()
            orderby entry.Date, entry.Slot, entry.Position
            select new TaskRow(
                InboxTaskKind.CookingToday,
                entry.Id,
                channel.GuildId,
                channel.Guild.Name,
                channel.Id,
                channel.Name,
                channel.Type,
                channel.CategoryId,
                channel.Category != null ? channel.Category.Name : null,
                recipe != null ? recipe.Title : entry.FreeText ?? "",
                "You're cooking",
                // Resolved from PlanDate after materialization - see TaskRow.
                null,
                MealGraceHours,
                entry.CreatedAt,
                entry.Date);

    /// <summary>Assets that are broken or overdue a service.</summary>
    internal static IQueryable<TaskRow> BuildMaintenanceQuery(
        MicroserviceContext ctx, string userId, DateTimeOffset now) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join asset in ctx.Set<MaintenanceAsset>().AsNoTracking()
                on member.GuildId equals asset.GuildId
            where asset.Status == AssetStatus.Broken
                  || (asset.NextServiceAt != null && asset.NextServiceAt <= now)
            join channel in ctx.Channels.AsNoTracking() on asset.ChannelId equals channel.Id
            orderby asset.NextServiceAt
            select new TaskRow(
                InboxTaskKind.MaintenanceDue,
                asset.Id,
                channel.GuildId,
                channel.Guild.Name,
                channel.Id,
                channel.Name,
                channel.Type,
                channel.CategoryId,
                channel.Category != null ? channel.Category.Name : null,
                asset.Name,
                asset.Status == AssetStatus.Broken ? "Marked as broken" : "Due for a service",
                // Broken carries no deadline even when a service is also overdue: the date says
                // when somebody should have looked at it, and the thing is already unusable.
                asset.Status == AssetStatus.Broken ? null : asset.NextServiceAt,
                0,
                asset.CreatedAt,
                null,
                asset.UpdatedAt);

    /// <summary>
    /// Scenes waiting on a character the caller answers for - their own, or one they hold a grant
    /// on. The grant is read here rather than through PersonaService because that cache is keyed
    /// per guild and the inbox spans every guild the caller is in.
    /// </summary>
    internal static IQueryable<TaskRow> BuildSceneTurnQuery(
        MicroserviceContext ctx, string userId, DateTimeOffset now) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join scene in ctx.Set<SceneState>().AsNoTracking() on member.GuildId equals scene.GuildId
            where scene.Status == SceneStatus.Active && scene.CurrentTurnPersonaId != null
            join persona in ctx.Set<Persona>().AsNoTracking()
                on scene.CurrentTurnPersonaId equals persona.Id
            where !persona.IsRetired
                  && ((persona.Scope == PersonaScope.User && persona.OwnerUserId == userId)
                      || ctx.Set<PersonaGrant>().Any(g =>
                          g.PersonaId == persona.Id
                          && (g.UserId == userId
                              || ctx.RoleMembers.Any(rm =>
                                  rm.RoleId == g.RoleId
                                  && rm.MemberId == member.Id
                                  && (rm.ExpiresAt == null || rm.ExpiresAt > now)))))
            join profile in ctx.Set<PersonaGuildProfile>().AsNoTracking()
                on new { persona.Id, scene.GuildId } equals
                   new { Id = profile.PersonaId, profile.GuildId }
            join channel in ctx.Channels.AsNoTracking() on scene.ChannelId equals channel.Id
            orderby scene.TurnDeadlineAt
            select new TaskRow(
                InboxTaskKind.SceneTurn,
                scene.ChannelId,
                channel.GuildId,
                channel.Guild.Name,
                channel.Id,
                channel.Name,
                channel.Type,
                channel.CategoryId,
                channel.Category != null ? channel.Category.Name : null,
                channel.Name,
                "Your turn as " + (profile.DisplayName ?? persona.Name),
                scene.TurnDeadlineAt,
                0,
                scene.TurnStartedAt ?? scene.CreatedAt);

    /// <summary>
    /// Characters sitting in an approval queue. Approved profiles whose page has been edited past
    /// what was signed off are a queue row too, but resolving that costs a revision lookup per
    /// character, so the inbox counts first approvals and the queue route counts both.
    /// </summary>
    internal static IQueryable<TaskRow> BuildPersonaReviewQuery(MicroserviceContext ctx, string userId) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join profile in ctx.Set<PersonaGuildProfile>().AsNoTracking()
                on member.GuildId equals profile.GuildId
            where profile.ApprovalState == PersonaApprovalState.Submitted
            join persona in ctx.Set<Persona>().AsNoTracking() on profile.PersonaId equals persona.Id
            join guild in ctx.Guilds.AsNoTracking() on profile.GuildId equals guild.Id
            orderby profile.UpdatedAt
            select new TaskRow(
                InboxTaskKind.PersonaReview,
                profile.PersonaId,
                guild.Id,
                guild.Name,
                null,
                null,
                null,
                null,
                null,
                profile.DisplayName ?? persona.Name,
                "Waiting on your review",
                null,
                0,
                profile.UpdatedAt);

    /// <summary>The caller's own characters that a reviewer sent back.</summary>
    internal static IQueryable<TaskRow> BuildPersonaChangesRequestedQuery(
        MicroserviceContext ctx, string userId) =>
        from profile in ctx.Set<PersonaGuildProfile>().AsNoTracking()
            where profile.ApprovalState == PersonaApprovalState.ChangesRequested
            join persona in ctx.Set<Persona>().AsNoTracking() on profile.PersonaId equals persona.Id
            where persona.Scope == PersonaScope.User && persona.OwnerUserId == userId
            join guild in ctx.Guilds.AsNoTracking() on profile.GuildId equals guild.Id
            orderby profile.UpdatedAt
            select new TaskRow(
                InboxTaskKind.PersonaChangesRequested,
                profile.PersonaId,
                guild.Id,
                guild.Name,
                null,
                null,
                null,
                null,
                null,
                profile.DisplayName ?? persona.Name,
                profile.ChangesRequestedReason ?? "Changes were requested",
                null,
                0,
                profile.UpdatedAt);

    /// <summary>
    /// Guild-owned characters that were sent back. Nobody owns one, so it lands on whoever manages
    /// characters here rather than on a player.
    /// </summary>
    internal static IQueryable<TaskRow> BuildGuildPersonaChangesRequestedQuery(
        MicroserviceContext ctx, string userId) =>
        from member in ctx.GuildMembers.AsNoTracking()
            where member.UserId == userId
            join profile in ctx.Set<PersonaGuildProfile>().AsNoTracking()
                on member.GuildId equals profile.GuildId
            where profile.ApprovalState == PersonaApprovalState.ChangesRequested
            join persona in ctx.Set<Persona>().AsNoTracking() on profile.PersonaId equals persona.Id
            where persona.Scope == PersonaScope.Guild
            join guild in ctx.Guilds.AsNoTracking() on profile.GuildId equals guild.Id
            orderby profile.UpdatedAt
            select new TaskRow(
                InboxTaskKind.PersonaChangesRequested,
                profile.PersonaId,
                guild.Id,
                guild.Name,
                null,
                null,
                null,
                null,
                null,
                profile.DisplayName ?? persona.Name,
                profile.ChangesRequestedReason ?? "Changes were requested",
                null,
                0,
                profile.UpdatedAt);

    /// <summary>
    /// Drops the rows the caller has put away, and keeps the ones whose own stamp has moved since:
    /// the next turn of a scene, a character resubmitted after being sent back and an asset broken
    /// a second time are all new work rather than work already dismissed.
    /// </summary>
    private async Task<List<TaskRow>> DropDismissedAsync(string userId, List<TaskRow> rows)
    {
        if (rows.Count == 0) return rows;

        // The whole set in one read rather than a filter per row: a dismissal is only written by
        // hand and swept at 90 days, so there are never many.
        var dismissals = await ctx.InboxTaskDismissals
            .AsNoTracking()
            .Where(d => d.UserId == userId)
            .Select(d => new { d.Kind, d.GuildId, d.TargetId, d.DismissedAt })
            .ToListAsync();

        if (dismissals.Count == 0) return rows;

        var byTask = dismissals.ToDictionary(
            d => (d.Kind, d.GuildId, d.TargetId),
            d => d.DismissedAt);

        return rows
            .Where(r => !byTask.TryGetValue((r.Kind.ToString(), r.GuildId, r.TargetId), out var at)
                        || (r.FreshAt ?? r.CreatedAt) > at)
            .ToList();
    }

    /// <summary>Puts one row away until whatever it is about moves again.</summary>
    /// <param name="userId">The caller, who can only ever dismiss their own row.</param>
    /// <param name="kind">Which kind of task the row is.</param>
    /// <param name="guildId">The guild the row is in, which is part of the key.</param>
    /// <param name="targetId">The row this task is about.</param>
    /// <returns>False when the caller is not in that guild.</returns>
    public async Task<bool> DismissAsync(
        string userId, InboxTaskKind kind, string guildId, string targetId)
    {
        var isMember = await ctx.GuildMembers
            .AsNoTracking()
            .AnyAsync(m => m.UserId == userId && m.GuildId == guildId);

        // Refused rather than accepted-and-ignored: a row written for a guild the caller is not in
        // is unreachable by the read path and would only ever be swept.
        if (!isMember) return false;

        var name = kind.ToString();
        var now = DateTimeOffset.UtcNow;

        var existing = await ctx.InboxTaskDismissals.FirstOrDefaultAsync(d =>
            d.UserId == userId && d.Kind == name && d.GuildId == guildId && d.TargetId == targetId);

        if (existing is null)
        {
            ctx.InboxTaskDismissals.Add(new InboxTaskDismissal
            {
                Id = InboxTaskDismissal.GenerateId(),
                CreatedAt = now,
                UpdatedAt = now,
                UserId = userId,
                Kind = name,
                GuildId = guildId,
                TargetId = targetId,
                DismissedAt = now,
            });
        }
        else
        {
            // Re-stamped rather than left alone, so a row that came back and was put away again
            // stays away.
            existing.DismissedAt = now;
            existing.UpdatedAt = now;
        }

        // Swept on the write instead of on a schedule: only the owner ever writes here, so there
        // is no reaper to forget to run. The row this call just re-stamped is excluded by key: the
        // database still holds its old timestamp, so an old one would sweep away the write.
        var cutoff = now - DismissalRetention;
        var stale = await ctx.InboxTaskDismissals
            .Where(d => d.UserId == userId
                        && d.DismissedAt < cutoff
                        && !(d.Kind == name && d.GuildId == guildId && d.TargetId == targetId))
            .ToListAsync();

        if (stale.Count > 0) ctx.InboxTaskDismissals.RemoveRange(stale);

        await ctx.SaveChangesAsync();

        return true;
    }

    /// <summary>
    /// Drops guild-scoped rows whose module has since been switched off, and rows addressed to a
    /// permission the caller has since lost. Both are resolved once per guild rather than once per
    /// row.
    /// </summary>
    private async Task<List<TaskRow>> KeepPermittedAsync(
        string userId, List<TaskRow> rows, GuildFeatures feature, ModulePermissions? required = null)
    {
        if (rows.Count == 0) return rows;

        var kept = new List<TaskRow>(rows.Count);

        foreach (var group in rows.GroupBy(r => r.GuildId, StringComparer.Ordinal))
        {
            if (!await permissions.IsFeatureEnabledAsync(group.Key, feature)) continue;

            if (required is { } module
                && !await permissions.CanUserPerformActionOnGuildAsync(userId, group.Key, module))
            {
                continue;
            }

            kept.AddRange(group);
        }

        return kept;
    }

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

            var channelIds = group
                .Select(r => r.ChannelId)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var visible = await permissions.FilterChannelsWithPermissionAsync(
                userId, group.Key, channelIds, Permissions.ViewChannel);

            kept.AddRange(group.Where(r => r.ChannelId is not null && visible.Contains(r.ChannelId)));
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
            ChannelType = (int?)row.ChannelType,
        },
        Title = row.Title,
        Subtitle = row.Subtitle,
        DueAt = row.DueAt,
        // Grace is a per-kind allowance and zero for most of them, so one expression covers every
        // kind: a decision or a bill is overdue the moment its date passes, a chore not until its
        // own grace period runs out, a meal not until the day it was planned for is over.
        IsOverdue = row.DueAt is not null
                    && row.DueAt.Value.AddHours(row.GraceHours) < DateTimeOffset.UtcNow,
    };
}
