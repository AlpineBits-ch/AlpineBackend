using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>
/// Recurring and due-dated expenses on a <see cref="ChannelType.Ledger"/> channel.
/// </summary>
[Authorize]
public class BillEndpoint
{
    private const int MaxDescriptionLength = 200;
    private const int MaxReasonLength = 200;

    private static RecurringExpenseDto ToDto(RecurringExpense template, string currency) => new()
    {
        Id = template.Id,
        ChannelId = template.ChannelId,
        Description = template.Description,
        AmountMinor = template.AmountMinor,
        Currency = currency,
        PayerUserId = template.PayerUserId,
        SplitKind = template.SplitKind,
        Category = template.Category,
        RecurrenceUnit = template.RecurrenceUnit,
        RecurrenceInterval = template.RecurrenceInterval,
        AnchorAt = template.AnchorAt,
        NextDueAt = template.NextDueAt,
        LeadDays = template.LeadDays,
        AutoPost = template.AutoPost,
        IsPaused = template.IsPaused,
        CreatedByUserId = template.CreatedByUserId,
        Shares = template.Shares
            .OrderBy(s => s.UserId, StringComparer.Ordinal)
            .Select(s => new RecurringExpenseShareEntryDto { UserId = s.UserId, ShareValue = s.ShareValue })
            .ToList(),
    };

    private static BillOccurrenceDto ToDto(BillOccurrence occurrence, string currency) => new()
    {
        Id = occurrence.Id,
        RecurringExpenseId = occurrence.RecurringExpenseId,
        ChannelId = occurrence.ChannelId,
        Description = occurrence.Description,
        DueAt = occurrence.DueAt,
        AmountMinor = occurrence.AmountMinor,
        Currency = currency,
        Status = occurrence.Status,
        ExpenseId = occurrence.ExpenseId,
        PostedByUserId = occurrence.PostedByUserId,
        SkippedByUserId = occurrence.SkippedByUserId,
        SkipReason = occurrence.SkipReason,
        NeedsAmount = occurrence.NeedsAmount(),
        IsOverdue = occurrence.Status == BillStatus.Pending && occurrence.DueAt < DateTimeOffset.UtcNow,
    };

    // ── Recurring expenses ───────────────────────────────────────────────────

    [WolverineGet("/api/v1/channels/{channelId}/recurring-expenses")]
    public async Task<IResult> ListTemplatesAsync(string channelId,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var templates = await ctx.Set<RecurringExpense>().AsNoTracking()
            .Include(t => t.Shares)
            .Where(t => t.ChannelId == channelId)
            .OrderBy(t => t.NextDueAt)
            .ToListAsync();

        var currency = await ledger.GetCurrencyAsync(channelId);

        return Results.Ok(templates.Select(t => ToDto(t, currency)));
    }

    /// <summary>Creates a schedule.</summary>
    [WolverinePost("/api/v1/channels/{channelId}/recurring-expenses")]
    public async Task<IResult> CreateTemplateAsync(string channelId, CreateRecurringExpenseDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] BillService bills, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ManageLedger);
        if (access.ToFailure() is { } failure) return failure;

        if (string.IsNullOrWhiteSpace(dto.Description)) return Results.BadRequest("Description is required");
        if (dto.Description.Length > MaxDescriptionLength)
            return Results.BadRequest($"Description must be {MaxDescriptionLength} characters or fewer");

        if (dto.AmountMinor is <= 0) return Results.BadRequest("AmountMinor must be greater than zero");

        if (Validate(dto.RecurrenceUnit, dto.RecurrenceInterval, dto.LeadDays, dto.AmountMinor,
                dto.AutoPost, dto.SplitKind, dto.Shares.Count) is { } invalid)
            return Results.BadRequest(invalid);

        var payerUserId = dto.PayerUserId ?? userId;

        // The payer, and everyone the split names.
        if (!await ledger.AreMembersAsync(access.Channel!.GuildId, payerUserId))
            return Results.BadRequest("The payer must be a member of this guild");

        if (dto.Shares.Count > 0)
        {
            if (dto.Shares.Select(s => s.UserId).Distinct(StringComparer.Ordinal).Count() != dto.Shares.Count)
                return Results.BadRequest("A participant may only appear once");

            if (!await ledger.AreMembersAsync(access.Channel.GuildId, dto.Shares.Select(s => s.UserId).ToArray()))
                return Results.BadRequest("Every participant must be a member of this guild");
        }

        var template = RecurringExpense.Create(new CreateRecurringExpenseParams
        {
            ChannelId = channelId,
            GuildId = access.Channel.GuildId,
            Description = dto.Description.Trim(),
            AmountMinor = dto.AmountMinor,
            PayerUserId = payerUserId,
            SplitKind = dto.SplitKind,
            Category = dto.Category,
            RecurrenceUnit = dto.RecurrenceUnit,
            RecurrenceInterval = dto.RecurrenceInterval,
            AnchorAt = dto.AnchorAt ?? DateTimeOffset.UtcNow,
            LeadDays = dto.LeadDays,
            AutoPost = dto.AutoPost,
            CreatedByUserId = userId,
        });

        foreach (var share in dto.Shares)
        {
            template.Shares.Add(new RecurringExpenseShare
            {
                RecurringExpenseId = template.Id,
                UserId = share.UserId,
                ShareValue = share.ShareValue,
            });
        }

        ctx.Set<RecurringExpense>().Add(template);

        // Generate the first bill immediately when it is already inside the lead window, so a
        // schedule entered the day before rent is due appears on the board now rather than after
        // the next sweep.
        var occurrence = await bills.StageNextOccurrenceAsync(template, DateTimeOffset.UtcNow);

        auditLog.Log(template.GuildId, userId, AuditActionType.RecurringExpenseCreated, template.Id,
            new { template.Description, template.AmountMinor, template.RecurrenceUnit, template.RecurrenceInterval, template.AutoPost });

        await ctx.SaveChangesAsync();

        var currency = await ledger.GetCurrencyAsync(channelId);

        await household.BroadcastAsync(template.GuildId, channelId, "guild.RecurringExpenseCreated",
            new { GuildId = template.GuildId, ChannelId = channelId, RecurringExpense = ToDto(template, currency) });

        if (occurrence is not null)
        {
            await household.BroadcastAsync(template.GuildId, channelId, "guild.BillOccurrenceCreated",
                new { GuildId = template.GuildId, ChannelId = channelId, Bill = ToDto(occurrence, currency) });
        }

        return Results.Ok(ToDto(template, currency));
    }

    [WolverinePatch("/api/v1/recurring-expenses/{templateId}")]
    public async Task<IResult> UpdateTemplateAsync(string templateId, UpdateRecurringExpenseDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var template = await ctx.Set<RecurringExpense>().Include(t => t.Shares)
            .FirstOrDefaultAsync(t => t.Id == templateId);
        if (template is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            template.ChannelId, ChannelType.Ledger, userId, Permissions.ManageLedger);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.Description is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Description)) return Results.BadRequest("Description cannot be empty");
            if (dto.Description.Length > MaxDescriptionLength)
                return Results.BadRequest($"Description must be {MaxDescriptionLength} characters or fewer");
        }

        if (dto.AmountMinor is <= 0) return Results.BadRequest("AmountMinor must be greater than zero");

        var amountMinor = dto.ClearAmount == true ? null : dto.AmountMinor ?? template.AmountMinor;
        var unit = dto.RecurrenceUnit ?? template.RecurrenceUnit;
        var interval = dto.RecurrenceInterval ?? template.RecurrenceInterval;
        var leadDays = dto.LeadDays ?? template.LeadDays;
        var autoPost = dto.AutoPost ?? template.AutoPost;
        var splitKind = dto.SplitKind ?? template.SplitKind;
        var shareCount = dto.Shares?.Count ?? template.Shares.Count;

        if (Validate(unit, interval, leadDays, amountMinor, autoPost, splitKind, shareCount) is { } invalid)
            return Results.BadRequest(invalid);

        if (dto.PayerUserId is not null && !await ledger.AreMembersAsync(template.GuildId, dto.PayerUserId))
            return Results.BadRequest("The payer must be a member of this guild");

        if (dto.Shares is { Count: > 0 })
        {
            if (dto.Shares.Select(s => s.UserId).Distinct(StringComparer.Ordinal).Count() != dto.Shares.Count)
                return Results.BadRequest("A participant may only appear once");

            if (!await ledger.AreMembersAsync(template.GuildId, dto.Shares.Select(s => s.UserId).ToArray()))
                return Results.BadRequest("Every participant must be a member of this guild");
        }

        if (dto.Description is not null) template.Description = dto.Description.Trim();
        template.AmountMinor = amountMinor;
        if (dto.PayerUserId is not null) template.PayerUserId = dto.PayerUserId;
        template.SplitKind = splitKind;
        if (dto.Category is not null) template.Category = dto.Category.Value;
        template.LeadDays = leadDays;
        template.AutoPost = autoPost;
        if (dto.IsPaused is not null) template.IsPaused = dto.IsPaused.Value;

        if (dto.Shares is not null)
        {
            template.Shares.Clear();
            foreach (var share in dto.Shares)
            {
                template.Shares.Add(new RecurringExpenseShare
                {
                    RecurringExpenseId = template.Id,
                    UserId = share.UserId,
                    ShareValue = share.ShareValue,
                });
            }
        }

        var rescheduled = dto.AnchorAt is not null
                          || unit != template.RecurrenceUnit
                          || interval != template.RecurrenceInterval;

        template.RecurrenceUnit = unit;
        template.RecurrenceInterval = interval;

        if (rescheduled)
        {
            // A new cadence has to be re-derived from the anchor rather than nudged, because every
            // slot is anchor + n units - see RecurringExpense.SlotAt.
            template.AnchorAt = dto.AnchorAt ?? template.AnchorAt;
            template.NextDueAt = template.AnchorAt;
            template.FastForwardTo(DateTimeOffset.UtcNow);

            await ReschedulePendingAsync(ctx, template);
        }

        template.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(template.GuildId, userId, AuditActionType.RecurringExpenseUpdated, template.Id,
            new { template.Description, template.AmountMinor, template.RecurrenceUnit, template.RecurrenceInterval, template.AutoPost, template.IsPaused });

        await ctx.SaveChangesAsync();

        var currency = await ledger.GetCurrencyAsync(template.ChannelId);

        await household.BroadcastAsync(template.GuildId, template.ChannelId, "guild.RecurringExpenseUpdated",
            new { GuildId = template.GuildId, ChannelId = template.ChannelId, RecurringExpense = ToDto(template, currency) });

        return Results.Ok(ToDto(template, currency));
    }

    /// <summary>
    /// Moves this template's un-posted bills onto the new cadence, and releases their announcement
    /// stamps so the new dates are announced.
    /// </summary>
    private static async Task ReschedulePendingAsync(MicroserviceContext ctx, RecurringExpense template)
    {
        var pending = await ctx.Set<BillOccurrence>()
            .Where(o => o.RecurringExpenseId == template.Id && o.Status == BillStatus.Pending)
            .OrderBy(o => o.DueAt)
            .ToListAsync();

        if (pending.Count == 0) return;

        var taken = await ctx.Set<BillOccurrence>()
            .Where(o => o.RecurringExpenseId == template.Id && o.Status != BillStatus.Pending)
            .Select(o => o.DueAt)
            .ToListAsync();

        var used = taken.ToHashSet();

        foreach (var occurrence in pending)
        {
            // The first slot of the new schedule at or after where this bill used to sit, so the
            // order of a house's upcoming bills survives the edit.
            var moved = template.AdvanceFrom(occurrence.DueAt.AddTicks(-1));

            // A slot the new cadence maps two old bills onto - a daily schedule turned monthly,
            // say.
            if (!used.Add(moved))
            {
                ctx.Set<BillOccurrence>().Remove(occurrence);
                continue;
            }

            occurrence.Reschedule(moved);
        }
    }

    /// <summary>Deletes a schedule and every bill it has not yet produced an expense for.</summary>
    [WolverineDelete("/api/v1/recurring-expenses/{templateId}")]
    public async Task<IResult> DeleteTemplateAsync(string templateId,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] AuditLogService auditLog, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var template = await ctx.Set<RecurringExpense>().FirstOrDefaultAsync(t => t.Id == templateId);
        if (template is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            template.ChannelId, ChannelType.Ledger, userId, Permissions.ManageLedger);
        if (access.ToFailure() is { } failure) return failure;

        // Logged before the remove, so the entry still says what was cancelled rather than the id of
        // a row nobody can look up any more.
        auditLog.Log(template.GuildId, userId, AuditActionType.RecurringExpenseDeleted, template.Id,
            new { template.Description, template.AmountMinor, template.RecurrenceUnit, template.RecurrenceInterval });

        ctx.Set<RecurringExpense>().Remove(template);   // shares and occurrences cascade
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(template.GuildId, template.ChannelId, "guild.RecurringExpenseDeleted",
            new { GuildId = template.GuildId, ChannelId = template.ChannelId, RecurringExpenseId = templateId });

        return Results.NoContent();
    }

    // ── Bills ────────────────────────────────────────────────────────────────

    [WolverineGet("/api/v1/channels/{channelId}/bills")]
    public async Task<IResult> ListBillsAsync(string channelId, string? status,
        DateTimeOffset? from, DateTimeOffset? to,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Ledger, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        BillStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<BillStatus>(status, ignoreCase: true, out var value))
                return Results.BadRequest("status must be one of Pending, Posted or Skipped");

            parsed = value;
        }

        var query = ctx.Set<BillOccurrence>().AsNoTracking().Where(o => o.ChannelId == channelId);

        if (parsed is not null) query = query.Where(o => o.Status == parsed);
        if (from is not null) query = query.Where(o => o.DueAt >= from);
        if (to is not null) query = query.Where(o => o.DueAt <= to);

        // Soonest first: the board answers "what is coming", and a bill history nobody paged past
        // the top of would be the wrong end of the list.
        var bills = await query
            .OrderBy(o => o.DueAt)
            .ThenBy(o => o.Id)
            .Take(MaxBoardSize)
            .ToListAsync();

        var currency = await ledger.GetCurrencyAsync(channelId);

        return Results.Ok(bills.Select(b => ToDto(b, currency)));
    }

    /// <summary>Bounded rather than paged.</summary>
    private const int MaxBoardSize = 500;

    /// <summary>Turns a bill into a real expense.</summary>
    [WolverinePost("/api/v1/bills/{billId}/post")]
    public async Task<IResult> PostBillAsync(string billId, PostBillDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] BillService bills, [NotBody] BillAlertService alerts,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var occurrence = await ctx.Set<BillOccurrence>().FirstOrDefaultAsync(o => o.Id == billId);
        if (occurrence is null) return Results.NotFound();

        var template = await ctx.Set<RecurringExpense>().Include(t => t.Shares)
            .FirstOrDefaultAsync(t => t.Id == occurrence.RecurringExpenseId);
        if (template is null) return Results.NotFound();

        var required = template.PayerUserId == userId ? Permissions.AddExpenses : Permissions.ManageLedger;

        var access = await household.ResolveAsync(
            occurrence.ChannelId, ChannelType.Ledger, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.AmountMinor is <= 0) return Results.BadRequest("AmountMinor must be greater than zero");

        var alreadyPosted = occurrence.Status == BillStatus.Posted;

        var result = await bills.PostAsync(occurrence, template, dto.AmountMinor, dto.OccurredAt, userId);
        if (result.Error is not null) return Results.BadRequest(result.Error);
        if (result.Expense is null) return Results.BadRequest("This bill could not be posted");

        var currency = await ledger.GetCurrencyAsync(occurrence.ChannelId);

        // A repeat post is answered with the expense the first one produced and nothing else
        // happens: no second audit entry, no second broadcast and above all no second notification
        // telling the house it owes rent again.
        if (alreadyPosted) return Results.Ok(ToDto(occurrence, currency));

        auditLog.Log(occurrence.GuildId, userId, AuditActionType.BillPosted, occurrence.Id,
            new { ExpenseId = result.Expense.Id, result.Expense.AmountMinor, result.Expense.PayerUserId });

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(occurrence.GuildId, occurrence.ChannelId, "guild.BillOccurrenceUpdated",
            new { GuildId = occurrence.GuildId, ChannelId = occurrence.ChannelId, Bill = ToDto(occurrence, currency) });

        await alerts.BillPostedAsync(occurrence, result.Expense, currency, userId);

        return Results.Ok(ToDto(occurrence, currency));
    }

    /// <summary>Marks a period as deliberately not charged.</summary>
    [WolverinePost("/api/v1/bills/{billId}/skip")]
    public async Task<IResult> SkipBillAsync(string billId, SkipBillDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] LedgerService ledger,
        [NotBody] MicroserviceContext ctx, [NotBody] AuditLogService auditLog,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var occurrence = await ctx.Set<BillOccurrence>().FirstOrDefaultAsync(o => o.Id == billId);
        if (occurrence is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            occurrence.ChannelId, ChannelType.Ledger, userId, Permissions.ManageLedger);
        if (access.ToFailure() is { } failure) return failure;

        if (occurrence.Status == BillStatus.Posted)
            return Results.BadRequest("This bill is already in the ledger - delete the expense instead");

        if (dto.Reason is { Length: > MaxReasonLength })
            return Results.BadRequest($"Reason must be {MaxReasonLength} characters or fewer");

        occurrence.Status = BillStatus.Skipped;
        occurrence.SkippedByUserId = userId;
        occurrence.SkipReason = string.IsNullOrWhiteSpace(dto.Reason) ? null : dto.Reason.Trim();
        occurrence.UpdatedAt = DateTimeOffset.UtcNow;

        auditLog.Log(occurrence.GuildId, userId, AuditActionType.BillSkipped, occurrence.Id,
            new { occurrence.Description, occurrence.DueAt, occurrence.SkipReason });

        await ctx.SaveChangesAsync();

        var currency = await ledger.GetCurrencyAsync(occurrence.ChannelId);

        await household.BroadcastAsync(occurrence.GuildId, occurrence.ChannelId, "guild.BillOccurrenceUpdated",
            new { GuildId = occurrence.GuildId, ChannelId = occurrence.ChannelId, Bill = ToDto(occurrence, currency) });

        return Results.NoContent();
    }

    // ── Shared validation ────────────────────────────────────────────────────

    /// <summary>The rules a schedule has to satisfy however it is being written.</summary>
    private static string? Validate(
        RecurrenceUnit unit, int interval, int leadDays, long? amountMinor,
        bool autoPost, ExpenseSplitKind splitKind, int shareCount)
    {
        if (!RecurringExpense.IsIntervalValid(unit, interval))
            return $"RecurrenceInterval must be between 1 and {RecurringExpense.MaxIntervalFor(unit)} for {unit}";

        if (leadDays is < 0 or > RecurringExpense.MaxLeadDays)
            return $"LeadDays must be between 0 and {RecurringExpense.MaxLeadDays}";

        // Auto-posting a bill nobody has priced would have to either invent a figure or post a zero,
        // and both of those silently corrupt balances people settle in their bank accounts.
        if (autoPost && amountMinor is null)
            return "AutoPost needs a fixed AmountMinor - a bill whose amount varies has to be confirmed each period";

        // Only an Equal split can leave its participants out, because only an Equal split can be
        // resolved from the member list alone.
        if (shareCount == 0 && splitKind != ExpenseSplitKind.Equal)
            return "A Shares or Exact split has to name its participants";

        return null;
    }
}
