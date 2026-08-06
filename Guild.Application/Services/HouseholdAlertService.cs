using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Who gets told about what, for every household event worth a phone buzzing.</summary>
public class HouseholdAlertService(
    MicroserviceContext ctx,
    HouseholdNotifier notifier,
    GuildPermissionService permissions,
    HomeStatusService homeStatus,
    ILogger<HouseholdAlertService> logger)
{
    /// <summary>Ceiling on how many members one alert resolves permissions for.</summary>
    private const int MaxRecipients = 200;

    /// <summary>Longest block reason carried into a push body.</summary>
    private const int MaxReasonLength = 120;

    public const string KindChoreDue = "chore.due";
    public const string KindExpense = "ledger.expense";
    public const string KindSettlement = "ledger.settlement";
    public const string KindDecisionOpened = "decision.opened";
    public const string KindDecisionBlocked = "decision.blocked";
    public const string KindRestock = "pantry.restock";
    public const string KindPantryExpiring = "pantry.expiring";

    // ── Ledger ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tells the people an expense was split across that it happened, and what it cost them.
    /// </summary>
    public async Task ExpenseAddedAsync(Expense expense, string currency, string actorUserId)
    {
        await SafelyAsync(nameof(ExpenseAddedAsync), async () =>
        {
            var shares = expense.Shares
                .Where(s => s.UserId != actorUserId && s.AmountMinor != 0)
                .ToList();

            foreach (var group in shares.GroupBy(s => s.AmountMinor))
            {
                var recipients = await ViewersOfAsync(
                    expense.ChannelId, group.Select(s => s.UserId).ToList());

                if (recipients.Count == 0) continue;

                await notifier.AlertAsync(
                    expense.GuildId, expense.ChannelId, recipients,
                    KindExpense,
                    expense.Description,
                    $"Your share is {MoneyFormat.Format(group.Key, currency)}.",
                    expense.Id,
                    new { expense.AmountMinor, Currency = currency, ShareMinor = group.Key, expense.PayerUserId });
            }

            if (expense.PayerUserId == actorUserId) return;

            var payer = await ViewersOfAsync(expense.ChannelId, [expense.PayerUserId]);
            if (payer.Count == 0) return;

            await notifier.AlertAsync(
                expense.GuildId, expense.ChannelId, payer,
                KindExpense,
                expense.Description,
                $"Recorded as paid by you: {MoneyFormat.Format(expense.AmountMinor, currency)}.",
                expense.Id,
                new { expense.AmountMinor, Currency = currency, RecordedBy = actorUserId });
        });
    }

    /// <summary>Tells the counterparty a payment was recorded against them.</summary>
    public async Task SettlementRecordedAsync(Settlement settlement, string currency, string actorUserId)
    {
        await SafelyAsync(nameof(SettlementRecordedAsync), async () =>
        {
            var money = MoneyFormat.Format(settlement.AmountMinor, currency);

            if (settlement.ToUserId != actorUserId)
            {
                await SendSettlementAsync(settlement, settlement.ToUserId,
                    "Payment received", $"{money} was recorded as paid to you.", currency);
            }

            if (settlement.FromUserId != actorUserId)
            {
                await SendSettlementAsync(settlement, settlement.FromUserId,
                    "Payment recorded", $"{money} was recorded as paid by you.", currency);
            }
        });
    }

    private async Task SendSettlementAsync(
        Settlement settlement, string userId, string title, string body, string currency)
    {
        var recipients = await ViewersOfAsync(settlement.ChannelId, [userId]);
        if (recipients.Count == 0) return;

        await notifier.AlertAsync(
            settlement.GuildId, settlement.ChannelId, recipients,
            KindSettlement, title, body, settlement.Id,
            new
            {
                settlement.FromUserId,
                settlement.ToUserId,
                settlement.AmountMinor,
                Currency = currency,
            });
    }

    // ── Decisions ────────────────────────────────────────────────────────────

    /// <summary>Tells the house a question is open.</summary>
    public async Task DecisionOpenedAsync(Decision decision, string actorUserId)
    {
        await SafelyAsync(nameof(DecisionOpenedAsync), async () =>
        {
            var members = await MemberUserIdsAsync(decision.GuildId, except: actorUserId);
            var recipients = await ViewersOfAsync(decision.ChannelId, members);
            if (recipients.Count == 0) return;

            await notifier.AlertAsync(
                decision.GuildId, decision.ChannelId, recipients,
                KindDecisionOpened,
                decision.Title,
                decision.ClosesAt is null
                    ? "A house decision needs your vote."
                    : "A house decision needs your vote before it closes.",
                decision.Id,
                new { decision.ClosesAt, decision.Quorum });
        });
    }

    /// <summary>
    /// Tells the people invested in a decision that it has been blocked, and why.
    /// </summary>
    public async Task DecisionBlockedAsync(Decision decision, string blockerUserId, string reason)
    {
        await SafelyAsync(nameof(DecisionBlockedAsync), async () =>
        {
            var interested = decision.Votes.Select(v => v.UserId)
                .Append(decision.CreatedByUserId)
                .Where(id => id != blockerUserId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var recipients = await ViewersOfAsync(decision.ChannelId, interested);
            if (recipients.Count == 0) return;

            var trimmed = reason.Trim();
            if (trimmed.Length > MaxReasonLength) trimmed = trimmed[..MaxReasonLength].TrimEnd() + "...";

            await notifier.AlertAsync(
                decision.GuildId, decision.ChannelId, recipients,
                KindDecisionBlocked,
                decision.Title,
                $"Blocked: {trimmed}",
                decision.Id,
                new { BlockedBy = blockerUserId });
        });
    }

    // ── Pantry ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tells whoever is out of the house that something just went on the shopping list.
    /// </summary>
    public async Task RestockAddedAsync(ListItem listItem, string actorUserId)
    {
        await SafelyAsync(nameof(RestockAddedAsync), async () =>
        {
            if (!await permissions.IsFeatureEnabledAsync(listItem.GuildId, GuildFeatures.Presence)) return;

            var statuses = await homeStatus.GetAsync(listItem.GuildId);

            var away = statuses
                .Where(s => s.Kind is HomeStatusKind.Out or HomeStatusKind.OnMyWay)
                .Select(s => s.UserId)
                .Where(id => id != actorUserId)
                .ToList();

            if (away.Count == 0) return;

            var recipients = await ViewersOfAsync(listItem.ChannelId, away);
            if (recipients.Count == 0) return;

            await notifier.AlertAsync(
                listItem.GuildId, listItem.ChannelId, recipients,
                KindRestock,
                listItem.Text,
                "Ran low at home and went on the list - could you grab it while you're out?",
                listItem.Id,
                new { listItem.Quantity, listItem.SourcePantryItemId });
        });
    }

    /// <summary>Tells a pantry's viewers what is about to go off, as one alert per pantry rather
    /// than one per item - see <see cref="PantryExpiryService"/> for why the sweep batches.</summary>
    public async Task<int> PantryExpiringAsync(
        string guildId, string channelId, IReadOnlyList<PantryItem> items)
    {
        if (items.Count == 0) return 0;

        var members = await MemberUserIdsAsync(guildId, except: null);
        var recipients = await ViewersOfAsync(channelId, members);
        if (recipients.Count == 0) return 0;

        await notifier.AlertAsync(
            guildId, channelId, recipients,
            KindPantryExpiring,
            "Use it or lose it",
            DescribeExpiring(items),
            channelId,
            new
            {
                Items = items.Select(i => new { i.Id, i.Name, i.ExpiresAt }).ToList(),
            });

        return recipients.Count;
    }

    /// <summary>"Milk expires tomorrow." / "Milk, yoghurt and 2 more are about to go off."</summary>
    internal static string DescribeExpiring(IReadOnlyList<PantryItem> items)
    {
        if (items.Count == 1) return $"{items[0].Name} is about to go off.";
        if (items.Count == 2) return $"{items[0].Name} and {items[1].Name} are about to go off.";

        return $"{items[0].Name}, {items[1].Name} and {items.Count - 2} more are about to go off.";
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    private async Task<List<string>> ViewersOfAsync(string channelId, IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0) return [];

        return await permissions.FilterUsersWithChannelPermissionAsync(
            channelId, userIds, Permissions.ViewChannel);
    }

    private async Task<List<string>> MemberUserIdsAsync(string guildId, string? except)
    {
        var query = ctx.GuildMembers.AsNoTracking().Where(m => m.GuildId == guildId);
        if (except is not null) query = query.Where(m => m.UserId != except);

        return await query
            .OrderBy(m => m.JoinedAt)
            .Select(m => m.UserId)
            .Take(MaxRecipients)
            .ToListAsync();
    }

    private async Task SafelyAsync(string operation, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Household alert {Operation} could not be delivered", operation);
        }
    }
}
