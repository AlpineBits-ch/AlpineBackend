using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>The outcome of turning a bill into a real expense.</summary>
public record BillPostResult(Expense? Expense, string? Error);

/// <summary>
/// Generates the bills a recurring expense owes, and turns them into ledger expenses.
/// </summary>
public class BillService(
    MicroserviceContext ctx,
    LedgerService ledger,
    BillAlertService alerts,
    GuildPermissionService permissions,
    ILogger<BillService> logger)
{
    /// <summary>Ceiling on the rows one sweep pass touches, matching
    /// <c>HouseholdReconcileService.GenerateDueChoresAsync</c>. Combined with oldest-first ordering
    /// it means a guild past the cap makes progress on its most overdue schedule rather than on
    /// whatever the planner happened to return.</summary>
    private const int BatchSize = 200;

    // ── Splits ───────────────────────────────────────────────────────────────

    /// <summary>Who a template's cost divides across, and in what proportion.</summary>
    public static IReadOnlyList<SplitParticipant> Participants(
        RecurringExpense template, IReadOnlyCollection<string> memberUserIds)
    {
        if (template.Shares.Count > 0)
        {
            return template.Shares
                .Select(s => new SplitParticipant(s.UserId, s.ShareValue))
                .ToList();
        }

        if (template.SplitKind != ExpenseSplitKind.Equal) return [];

        return memberUserIds
            .Order(StringComparer.Ordinal)
            .Select(id => new SplitParticipant(id, 1))
            .ToList();
    }

    public async Task<List<string>> MemberUserIdsAsync(string guildId) =>
        await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .Select(m => m.UserId)
            .ToListAsync();

    // ── Generation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the occurrence for a template's current <c>NextDueAt</c> if it is inside the lead
    /// window, and advances the schedule.
    /// </summary>
    public async Task<BillOccurrence?> StageNextOccurrenceAsync(RecurringExpense template, DateTimeOffset now)
    {
        if (template.IsPaused) return null;

        // Collapse the backlog before generating anything. See RecurringExpense.FastForwardTo.
        template.FastForwardTo(now);

        var dueAt = template.NextDueAt;

        // Not yet worth telling anybody about.
        if (dueAt > now.AddDays(Math.Clamp(template.LeadDays, 0, RecurringExpense.MaxLeadDays)))
            return null;

        var exists = await ctx.Set<BillOccurrence>()
            .AnyAsync(o => o.RecurringExpenseId == template.Id && o.DueAt == dueAt);

        if (exists)
        {
            template.NextDueAt = template.AdvanceFrom(dueAt);
            return null;
        }

        var occurrence = BillOccurrence.Create(template, dueAt);
        ctx.Set<BillOccurrence>().Add(occurrence);

        template.NextDueAt = template.AdvanceFrom(dueAt);
        template.UpdatedAt = now;

        return occurrence;
    }

    // ── Posting ──────────────────────────────────────────────────────────────

    /// <summary>Turns a bill into the <see cref="Expense"/> it always meant to be.</summary>
    public async Task<BillPostResult> PostAsync(
        BillOccurrence occurrence,
        RecurringExpense template,
        long? amountMinor,
        DateTimeOffset? occurredAt,
        string? actorUserId)
    {
        if (occurrence.Status == BillStatus.Skipped)
            return new BillPostResult(null, "This bill was skipped for this period");

        if (occurrence.Status == BillStatus.Posted)
        {
            var already = occurrence.ExpenseId is null
                ? null
                : await ctx.Expenses.Include(e => e.Shares)
                    .FirstOrDefaultAsync(e => e.Id == occurrence.ExpenseId);

            return new BillPostResult(already, null);
        }

        var total = amountMinor ?? occurrence.AmountMinor;
        if (total is null)
            return new BillPostResult(null, "This bill needs an amount before it can be posted");

        if (total <= 0)
            return new BillPostResult(null, "AmountMinor must be greater than zero");

        // Re-checked at post time rather than trusted from template creation: the payer may have
        // moved out between the schedule being written and this period coming due, and a bill
        // credited to somebody who is gone leaves a balance nobody can clear.
        if (!await ledger.AreMembersAsync(template.GuildId, template.PayerUserId))
            return new BillPostResult(null, "The payer must be a member of this guild");

        var expense = Expense.Create(new CreateExpenseParams
        {
            ChannelId = template.ChannelId,
            GuildId = template.GuildId,
            PayerUserId = template.PayerUserId,
            Description = template.Description,
            AmountMinor = total.Value,
            OccurredAt = occurredAt ?? occurrence.DueAt,
            SplitKind = template.SplitKind,
            CreatedByUserId = actorUserId ?? template.CreatedByUserId,
            Category = template.Category,
            BillOccurrenceId = occurrence.Id,
        });

        var participants = Participants(template, await MemberUserIdsAsync(template.GuildId));
        if (participants.Count == 0)
            return new BillPostResult(null, "This bill has nobody to split across");

        if (await ledger.ApplySharesAsync(expense, template.SplitKind, participants, template.GuildId) is { } error)
            return new BillPostResult(null, error);

        ctx.Expenses.Add(expense);

        occurrence.Status = BillStatus.Posted;
        occurrence.ExpenseId = expense.Id;
        occurrence.PostedByUserId = actorUserId;
        occurrence.AmountMinor = total;
        occurrence.UpdatedAt = DateTimeOffset.UtcNow;

        return new BillPostResult(expense, null);
    }

    // ── Sweep ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One pass of the bill sweep: generate what is coming due, auto-post what may post itself, and
    /// announce the rest.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var generated = await GenerateDueBillsAsync(now, ct);
        var posted = await AutoPostDueBillsAsync(now, ct);

        var candidates = await ctx.Set<BillOccurrence>()
            .Where(o => o.RemindedAt == null && o.Status == BillStatus.Pending)
            // Oldest first, so a backlog of abandoned bills is stamped out of the filtered index in
            // batches rather than being re-examined every few minutes forever.
            .OrderBy(o => o.DueAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        var announced = await alerts.SendDueAlertsAsync(candidates, ct);

        if (generated > 0 || posted > 0)
            logger.LogDebug("Bill sweep generated {Generated} and auto-posted {Posted}", generated, posted);

        return generated + posted + announced;
    }

    private async Task<int> GenerateDueBillsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // LeadDays is a per-row column, so "NextDueAt <= now + LeadDays" has no translation. The
        // query asks for the widest lead any template may configure and each row's own lead is
        // applied in memory, which is the shape PantryExpiryService already uses for
        // ExpiryWarningDays.
        var horizon = now.AddDays(RecurringExpense.MaxLeadDays);

        var due = await ctx.Set<RecurringExpense>()
            .Where(t => !t.IsPaused && t.NextDueAt <= horizon)
            .OrderBy(t => t.NextDueAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        var generated = 0;

        foreach (var template in due)
        {
            // The module can be switched off between a schedule being written and a period coming
            // due, and a guild with the ledger disabled must not keep charging itself.
            if (!await permissions.IsFeatureEnabledAsync(template.GuildId, GuildFeatures.Ledger)) continue;

            if (await StageNextOccurrenceAsync(template, now) is not null) generated++;
        }

        await ctx.SaveChangesAsync(ct);

        return generated;
    }

    /// <summary>Posts the bills whose template said to, once they are actually due.</summary>
    private async Task<int> AutoPostDueBillsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var candidates = await ctx.Set<BillOccurrence>()
            .Where(o => o.Status == BillStatus.Pending && o.AmountMinor != null && o.DueAt <= now)
            .OrderBy(o => o.DueAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var templateIds = candidates.Select(o => o.RecurringExpenseId).Distinct().ToList();

        var templates = await ctx.Set<RecurringExpense>()
            .Include(t => t.Shares)
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        var postedNow = new List<(BillOccurrence Occurrence, Expense Expense)>();

        foreach (var occurrence in candidates)
        {
            if (!templates.TryGetValue(occurrence.RecurringExpenseId, out var template)) continue;
            if (!template.AutoPost) continue;

            if (!await permissions.IsFeatureEnabledAsync(template.GuildId, GuildFeatures.Ledger)) continue;

            var result = await PostAsync(occurrence, template, null, null, actorUserId: null);

            if (result.Error is not null)
            {
                // Not fatal and not retried differently: the bill stays Pending and a person can
                // post it by hand, which is the right escalation for "the payer moved out" or "the
                // split no longer resolves".
                logger.LogWarning("Bill {BillId} could not auto-post: {Error}", occurrence.Id, result.Error);
                continue;
            }

            if (result.Expense is not null) postedNow.Add((occurrence, result.Expense));
        }

        if (postedNow.Count == 0) return 0;

        await ctx.SaveChangesAsync(ct);

        // After the commit, for the reason every household alert is: dispatch is best-effort and
        // must never fail a write whose money is already recorded.
        foreach (var (occurrence, expense) in postedNow)
        {
            var currency = await ledger.GetCurrencyAsync(occurrence.ChannelId);
            await alerts.BillPostedAsync(occurrence, expense, currency, actorUserId: null);
        }

        return postedNow.Count;
    }
}
