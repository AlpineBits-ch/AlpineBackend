using Guild.Contracts;
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

    /// <summary>Something went on a shopping list.</summary>
    public const string KindListItemAdded = "list.item_added";

    /// <summary>The last unchecked line on a list was ticked off.</summary>
    public const string KindListCompleted = "list.completed";

    /// <summary>A pantry item crossed its low-stock threshold.</summary>
    public const string KindPantryLow = "pantry.low";

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

                var share = MoneyFormat.Format(group.Key, currency);

                await notifier.AlertAsync(
                    expense.GuildId, expense.ChannelId, recipients,
                    KindExpense,
                    AlertText.Raw(expense.Description),
                    AlertText.Loc(HouseholdLocKeys.ExpenseShareBody, $"Your share is {share}.", share),
                    expense.Id,
                    new { expense.AmountMinor, Currency = currency, ShareMinor = group.Key, expense.PayerUserId });
            }

            if (expense.PayerUserId == actorUserId) return;

            var payer = await ViewersOfAsync(expense.ChannelId, [expense.PayerUserId]);
            if (payer.Count == 0) return;

            var total = MoneyFormat.Format(expense.AmountMinor, currency);

            await notifier.AlertAsync(
                expense.GuildId, expense.ChannelId, payer,
                KindExpense,
                AlertText.Raw(expense.Description),
                AlertText.Loc(HouseholdLocKeys.ExpensePaidBody, $"Recorded as paid by you: {total}.", total),
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
                    AlertText.Loc(HouseholdLocKeys.SettlementReceivedTitle, "Payment received"),
                    AlertText.Loc(HouseholdLocKeys.SettlementReceivedBody,
                        $"{money} was recorded as paid to you.", money),
                    currency);
            }

            if (settlement.FromUserId != actorUserId)
            {
                await SendSettlementAsync(settlement, settlement.FromUserId,
                    AlertText.Loc(HouseholdLocKeys.SettlementRecordedTitle, "Payment recorded"),
                    AlertText.Loc(HouseholdLocKeys.SettlementRecordedBody,
                        $"{money} was recorded as paid by you.", money),
                    currency);
            }
        });
    }

    private async Task SendSettlementAsync(
        Settlement settlement, string userId, AlertText title, AlertText body, string currency)
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
                AlertText.Raw(decision.Title),
                decision.ClosesAt is null
                    ? AlertText.Loc(HouseholdLocKeys.DecisionOpenedBody,
                        "A house decision needs your vote.")
                    : AlertText.Loc(HouseholdLocKeys.DecisionOpenedClosingBody,
                        "A house decision needs your vote before it closes."),
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
                AlertText.Raw(decision.Title),
                AlertText.Loc(HouseholdLocKeys.DecisionBlockedBody, $"Blocked: {trimmed}", trimmed),
                decision.Id,
                new { BlockedBy = blockerUserId });
        });
    }

    // ── Shopping lists ───────────────────────────────────────────────────────

    /// <summary>
    /// Tells the people who can still act on it that something went on a shopping list.
    /// </summary>
    public async Task ListItemAddedAsync(ListItem item, string listName, string actorUserId)
    {
        await SafelyAsync(nameof(ListItemAddedAsync), async () =>
        {
            // The assignee first, so that somebody who is both assigned and out gets the sentence
            // that names them rather than the generic one.
            List<string> assignee = item.AssigneeUserId is null || item.AssigneeUserId == actorUserId
                ? []
                : await ViewersOfAsync(item.ChannelId, [item.AssigneeUserId]);

            if (assignee.Count > 0)
            {
                await notifier.AlertAsync(
                    item.GuildId, item.ChannelId, assignee,
                    KindListItemAdded,
                    AlertText.Raw(item.Text),
                    AlertText.Loc(HouseholdLocKeys.ListItemAssignedBody,
                        $"You've been asked to pick this up from {listName}.", listName),
                    item.Id,
                    new { item.Quantity, item.Section, item.AssigneeUserId, AddedBy = actorUserId });
            }

            var away = await AwayFromHomeAsync(item.GuildId, except: actorUserId);
            away.RemoveAll(assignee.Contains);
            if (away.Count == 0) return;

            var recipients = await ViewersOfAsync(item.ChannelId, away);
            if (recipients.Count == 0) return;

            await notifier.AlertAsync(
                item.GuildId, item.ChannelId, recipients,
                KindListItemAdded,
                AlertText.Raw(item.Text),
                AlertText.Loc(HouseholdLocKeys.ListItemAddedBody,
                    $"Just went on {listName} - could you grab it while you're out?", listName),
                item.Id,
                new { item.Quantity, item.Section, AddedBy = actorUserId });
        });
    }

    /// <summary>
    /// Tells the people who put something on a list that all of it has now been bought.
    /// </summary>
    public async Task ListCompletedAsync(
        string guildId, string channelId, string listName, string actorUserId)
    {
        await SafelyAsync(nameof(ListCompletedAsync), async () =>
        {
            var contributors = await ctx.ListItems.AsNoTracking()
                .Where(i => i.ChannelId == channelId && i.AddedByUserId != actorUserId)
                .Select(i => i.AddedByUserId)
                .Distinct()
                .Take(MaxRecipients)
                .ToListAsync();

            var recipients = await ViewersOfAsync(channelId, contributors);
            if (recipients.Count == 0) return;

            await notifier.AlertAsync(
                guildId, channelId, recipients,
                KindListCompleted,
                AlertText.Raw(listName),
                AlertText.Loc(HouseholdLocKeys.ListCompletedBody, "Everything on it is ticked off."),
                channelId,
                new { CompletedBy = actorUserId });
        });
    }

    // ── Pantry ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tells the house that a pantry item has run low, and tells whoever is out about it
    /// differently.
    /// </summary>
    public async Task PantryLowAsync(PantryItem item, ListItem? restocked, string actorUserId)
    {
        await SafelyAsync(nameof(PantryLowAsync), async () =>
        {
            List<string> away = restocked is null
                ? []
                : await AwayFromHomeAsync(item.GuildId, except: actorUserId);

            List<string> restockRecipients = away.Count == 0
                ? []
                : await ViewersOfAsync(restocked!.ChannelId, away);

            var listName = restocked is null ? null : await ChannelNameAsync(restocked.ChannelId);

            if (restockRecipients.Count > 0)
            {
                await notifier.AlertAsync(
                    restocked!.GuildId, restocked.ChannelId, restockRecipients,
                    KindRestock,
                    AlertText.Raw(restocked.Text),
                    AlertText.Loc(HouseholdLocKeys.PantryRestockBody,
                        "Ran low at home and went on the list - could you grab it while you're out?"),
                    restocked.Id,
                    new { restocked.Quantity, restocked.SourcePantryItemId });
            }

            // Everyone else, on the pantry channel rather than the list - that is where the item
            // they are being told about lives, and it is the board that answers "what else is
            // nearly out".
            var members = await MemberUserIdsAsync(item.GuildId, except: actorUserId);
            members.RemoveAll(restockRecipients.Contains);

            var recipients = await ViewersOfAsync(item.ChannelId, members);
            if (recipients.Count == 0) return;

            await notifier.AlertAsync(
                item.GuildId, item.ChannelId, recipients,
                KindPantryLow,
                AlertText.Raw(item.Name),
                listName is null
                    ? AlertText.Loc(HouseholdLocKeys.PantryLowBody, "Running low at home.")
                    : AlertText.Loc(HouseholdLocKeys.PantryLowListedBody,
                        $"Running low, so it's gone on {listName}.", listName),
                item.Id,
                new
                {
                    item.Quantity,
                    item.Unit,
                    item.LowThreshold,
                    ListedOnChannelId = restocked?.ChannelId,
                    ListItemId = restocked?.Id,
                });
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
            AlertText.Loc(HouseholdLocKeys.PantryExpiringTitle, "Use it or lose it"),
            DescribeExpiring(items),
            channelId,
            new
            {
                Items = items.Select(i => new { i.Id, i.Name, i.ExpiresAt }).ToList(),
            });

        return recipients.Count;
    }

    /// <summary>
    /// "Milk is about to go off." / "Milk, yoghurt and 2 more are about to go off."
    /// </summary>
    internal static AlertText DescribeExpiring(IReadOnlyList<PantryItem> items)
    {
        if (items.Count == 1)
        {
            return AlertText.Loc(HouseholdLocKeys.PantryExpiringOneBody,
                $"{items[0].Name} is about to go off.", items[0].Name);
        }

        if (items.Count == 2)
        {
            return AlertText.Loc(HouseholdLocKeys.PantryExpiringTwoBody,
                $"{items[0].Name} and {items[1].Name} are about to go off.",
                items[0].Name, items[1].Name);
        }

        var rest = (items.Count - 2).ToString();

        return AlertText.Loc(HouseholdLocKeys.PantryExpiringManyBody,
            $"{items[0].Name}, {items[1].Name} and {rest} more are about to go off.",
            items[0].Name, items[1].Name, rest);
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>Who is out of the house right now, as the home-status board sees it.</summary>
    private async Task<List<string>> AwayFromHomeAsync(string guildId, string except)
    {
        if (!await permissions.IsFeatureEnabledAsync(guildId, GuildFeatures.Presence)) return [];

        var statuses = await homeStatus.GetAsync(guildId);

        return statuses
            .Where(s => s.Kind is HomeStatusKind.Out or HomeStatusKind.OnMyWay)
            .Select(s => s.UserId)
            .Where(id => id != except)
            .ToList();
    }

    private async Task<string?> ChannelNameAsync(string channelId) =>
        await ctx.Channels.AsNoTracking()
            .Where(c => c.Id == channelId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync();

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
