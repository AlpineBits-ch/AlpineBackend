using Guild.Contracts;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Who gets told a bill is coming, and who gets told one landed.</summary>
public class BillAlertService(
    MicroserviceContext ctx,
    HouseholdNotifier notifier,
    LedgerService ledger,
    GuildPermissionService permissions,
    ILogger<BillAlertService> logger)
{
    /// <summary>A bill is due, or is about to be.</summary>
    public const string KindBillDue = "ledger.bill_due";

    /// <summary>A bill became a real expense and is now in the balances.</summary>
    public const string KindBillPosted = "ledger.bill_posted";

    /// <summary>A variable bill has come due with nobody having said what it was.</summary>
    public const string KindBillNeedsAmount = "ledger.bill_needs_amount";

    /// <summary>Ceiling on how many members one alert resolves permissions for, matching
    /// <see cref="HouseholdAlertService"/>'s.</summary>
    private const int MaxRecipients = 200;

    /// <summary>How far past its due date a bill is still worth announcing.</summary>
    private static readonly TimeSpan MaxLateness = TimeSpan.FromDays(7);

    /// <summary>Inside this much of the due date a bill counts as due today.</summary>
    private static readonly TimeSpan DueTodayWindow = TimeSpan.FromHours(24);

    // ── Due and due soon ─────────────────────────────────────────────────────

    /// <summary>
    /// Announces the bills in <paramref name="candidates"/> that are worth announcing, and stamps
    /// every one it dealt with.
    /// </summary>
    public async Task<int> SendDueAlertsAsync(
        IReadOnlyList<BillOccurrence> candidates, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;

        var guildIds = candidates.Select(o => o.GuildId).Distinct().ToList();

        var quietHours = await ctx.GuildQuietHoursConfigs.AsNoTracking()
            .Where(c => guildIds.Contains(c.GuildId))
            .ToDictionaryAsync(c => c.GuildId, ct);

        var templateIds = candidates.Select(o => o.RecurringExpenseId).Distinct().ToList();

        var templates = await ctx.Set<RecurringExpense>()
            .AsNoTracking()
            .Include(t => t.Shares)
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        var announced = 0;

        foreach (var occurrence in candidates)
        {
            if (!templates.TryGetValue(occurrence.RecurringExpenseId, out var template))
            {
                occurrence.RemindedAt = now;
                continue;
            }

            // The module can be switched off between a bill being generated and it coming due, and
            // a guild with the ledger disabled must not be told what it owes.
            if (!await permissions.IsFeatureEnabledAsync(occurrence.GuildId, GuildFeatures.Ledger)) continue;

            // The whole point of the deferral: an announcement that would land inside the quiet
            // window is simply not sent yet, and stays unstamped so the next sweep after the window
            // closes picks it up.
            if (quietHours.TryGetValue(occurrence.GuildId, out var quiet) && quiet.DeferPast(now) > now)
                continue;

            if (now - occurrence.DueAt > MaxLateness)
            {
                // Too stale to be useful, but stamped anyway - otherwise it sits at the head of the
                // candidate ordering forever and the sweep re-examines it every pass for the rest of
                // time.
                occurrence.RemindedAt = now;
                continue;
            }

            var sent = occurrence.NeedsAmount()
                ? await SendNeedsAmountAsync(occurrence)
                : await SendDueAsync(occurrence, template, now);

            occurrence.RemindedAt = now;
            if (sent) announced++;
        }

        await ctx.SaveChangesAsync(ct);

        return announced;
    }

    /// <summary>Tells everyone with a share what this bill will cost them, and when.</summary>
    private async Task<bool> SendDueAsync(BillOccurrence occurrence, RecurringExpense template, DateTimeOffset now)
    {
        return await SafelyAsync(nameof(SendDueAsync), async () =>
        {
            var amount = occurrence.AmountMinor;
            if (amount is null or <= 0) return false;

            var members = await MemberUserIdsAsync(occurrence.GuildId);
            var participants = BillService.Participants(template, members);
            if (participants.Count == 0) return false;

            IReadOnlyList<SplitResult> split;
            try
            {
                split = ExpenseSplitter.Split(amount.Value, template.SplitKind, participants);
            }
            catch (ArgumentException e)
            {
                // A split the template can no longer satisfy - exact amounts that stopped summing
                // after the total was edited, say.
                logger.LogWarning(e, "Bill {BillId} has no resolvable split to announce", occurrence.Id);
                return false;
            }

            var currency = await ledger.GetCurrencyAsync(occurrence.ChannelId);
            var remaining = occurrence.DueAt - now;
            var dueToday = remaining < DueTodayWindow;

            var anySent = false;

            // Grouped by share amount rather than sent per person, exactly as
            // HouseholdAlertService.ExpenseAddedAsync does: an equal split - the overwhelmingly
            // common case for rent - collapses to a single fan-out.
            foreach (var group in split.Where(s => s.AmountMinor != 0).GroupBy(s => s.AmountMinor))
            {
                var recipients = await ViewersOfAsync(
                    occurrence.ChannelId, group.Select(s => s.UserId).ToList());

                if (recipients.Count == 0) continue;

                var share = MoneyFormat.Format(group.Key, currency);

                var body = dueToday
                    ? AlertText.Loc(HouseholdLocKeys.BillDueBody,
                        $"Due today. Your share is {share}.", share)
                    : AlertText.Loc(HouseholdLocKeys.BillDueSoonBody,
                        $"Due in {DescribeDuration(remaining)}. Your share is {share}.",
                        DescribeDuration(remaining), share);

                await notifier.AlertAsync(
                    occurrence.GuildId, occurrence.ChannelId, recipients,
                    KindBillDue,
                    AlertText.Raw(occurrence.Description),
                    body,
                    occurrence.Id,
                    new
                    {
                        occurrence.RecurringExpenseId,
                        occurrence.DueAt,
                        AmountMinor = amount.Value,
                        Currency = currency,
                        ShareMinor = group.Key,
                        template.PayerUserId,
                    });

                anySent = true;
            }

            return anySent;
        });
    }

    /// <summary>
    /// Tells whoever can act on it that a variable bill has come due with no figure.
    /// </summary>
    private async Task<bool> SendNeedsAmountAsync(BillOccurrence occurrence)
    {
        return await SafelyAsync(nameof(SendNeedsAmountAsync), async () =>
        {
            var members = await MemberUserIdsAsync(occurrence.GuildId);
            if (members.Count == 0) return false;

            var recipients = await permissions.FilterUsersWithChannelPermissionAsync(
                occurrence.ChannelId, members, ModulePermissions.ManageLedger);

            if (recipients.Count == 0) return false;

            await notifier.AlertAsync(
                occurrence.GuildId, occurrence.ChannelId, recipients,
                KindBillNeedsAmount,
                AlertText.Raw(occurrence.Description),
                AlertText.Loc(HouseholdLocKeys.BillNeedsAmountBody,
                    "A bill needs an amount before it can be split."),
                occurrence.Id,
                new { occurrence.RecurringExpenseId, occurrence.DueAt });

            return true;
        });
    }

    // ── Posted ───────────────────────────────────────────────────────────────

    /// <summary>Tells the people a bill was split across that it is now in the balances.</summary>
    public async Task BillPostedAsync(
        BillOccurrence occurrence, Expense expense, string currency, string? actorUserId)
    {
        await SafelyAsync(nameof(BillPostedAsync), async () =>
        {
            var shares = expense.Shares
                .Where(s => s.UserId != actorUserId && s.AmountMinor != 0)
                .ToList();

            foreach (var group in shares.GroupBy(s => s.AmountMinor))
            {
                var recipients = await ViewersOfAsync(
                    expense.ChannelId, group.Select(s => s.UserId).ToList());

                if (recipients.Count == 0) continue;

                var share = MoneyFormat.Format(group.Key, currency);

                await notifier.AlertAsync(
                    expense.GuildId, expense.ChannelId, recipients,
                    KindBillPosted,
                    AlertText.Raw(expense.Description),
                    AlertText.Loc(HouseholdLocKeys.BillPostedBody,
                        $"Added to the ledger. Your share is {share}.", share),
                    // The expense, not the bill: what the recipient wants to open is the row that
                    // now moves their balance.
                    expense.Id,
                    new
                    {
                        BillId = occurrence.Id,
                        expense.AmountMinor,
                        Currency = currency,
                        ShareMinor = group.Key,
                        expense.PayerUserId,
                    });
            }

            return true;
        });
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>"3 days", "1 day".</summary>
    internal static string DescribeDuration(TimeSpan remaining)
    {
        var days = Math.Max(1, (int)Math.Round(remaining.TotalDays, MidpointRounding.AwayFromZero));
        return days == 1 ? "1 day" : $"{days} days";
    }

    private async Task<List<string>> ViewersOfAsync(string channelId, IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0) return [];

        return await permissions.FilterUsersWithChannelPermissionAsync(
            channelId, userIds, Permissions.ViewChannel);
    }

    private async Task<List<string>> MemberUserIdsAsync(string guildId) =>
        await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => m.UserId)
            .Take(MaxRecipients)
            .ToListAsync();

    private async Task<bool> SafelyAsync(string operation, Func<Task<bool>> body)
    {
        try
        {
            return await body();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Bill alert {Operation} could not be delivered", operation);
            return false;
        }
    }
}
