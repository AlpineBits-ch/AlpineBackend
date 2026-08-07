using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>Assembles the whole household into one glance.</summary>
public class HouseholdDigestService(
    MicroserviceContext ctx,
    GuildPermissionService permissions,
    LedgerService ledger,
    HomeStatusService homeStatus)
{
    /// <summary>Chores due inside this window count as "coming up".</summary>
    private static readonly TimeSpan ChoreLookahead = TimeSpan.FromDays(1);

    /// <summary>Bills due inside this window count as "coming up".</summary>
    private static readonly TimeSpan BillLookahead = TimeSpan.FromDays(14);

    private const int MaxChores = 10;
    private const int MaxListChannels = 5;
    private const int MaxListPreview = 5;
    private const int MaxPantryItems = 5;
    private const int MaxLedgerChannels = 3;
    private const int MaxDecisions = 5;
    private const int MaxBills = 5;
    private const int MaxMeals = 5;
    private const int MaxAssets = 5;
    private const int MaxAbsences = 10;
    private const int DefaultExpiryWarningDays = 3;
    private const int DefaultGraceHours = 24;

    /// <summary>Ceiling on the members one empty-shares split is resolved across, matching
    /// <see cref="BillAlertService"/>'s recipient cap. The two must agree: a share the digest
    /// computes across a different set from the one the alert announced is a number that changes
    /// depending on which surface you read it from.</summary>
    private const int MaxSplitMembers = 200;

    public async Task<HouseholdDigestDto> BuildAsync(string guildId, string userId)
    {
        var now = DateTimeOffset.UtcNow;

        var features = await permissions.GetGuildFeaturesAsync(guildId);

        // One pass over the guild's channels; each section filters the subset it cares about, so
        // the permission resolution below happens once per type rather than once per row.
        var channels = await ctx.Channels.AsNoTracking()
            .Where(c => c.GuildId == guildId)
            .Select(c => new ChannelRef(c.Id, c.Name, c.Type))
            .ToListAsync();

        return new HouseholdDigestDto
        {
            GuildId = guildId,
            Chores = features.HasFlag(GuildFeatures.Chores)
                ? await BuildChoresAsync(guildId, userId, channels, now)
                : null,
            Lists = features.HasFlag(GuildFeatures.Lists)
                ? await BuildListsAsync(guildId, userId, channels)
                : null,
            Pantry = features.HasFlag(GuildFeatures.Pantry)
                ? await BuildPantryAsync(guildId, userId, channels, now)
                : null,
            Ledger = features.HasFlag(GuildFeatures.Ledger)
                ? await BuildLedgerAsync(guildId, userId, channels)
                : null,
            Decisions = features.HasFlag(GuildFeatures.Decisions)
                ? await BuildDecisionsAsync(guildId, userId, channels, now)
                : null,
            HomeStatus = features.HasFlag(GuildFeatures.Presence)
                ? await homeStatus.GetAsync(guildId)
                : null,
            // Bills live on ledger channels and are gated on the ledger module, because a bill is
            // an expense before it is one - switching the ledger off must take what it owes with it.
            Bills = features.HasFlag(GuildFeatures.Ledger)
                ? await BuildBillsAsync(guildId, userId, channels, now)
                : null,
            Meals = features.HasFlag(GuildFeatures.Meals)
                ? await BuildMealsAsync(guildId, userId, channels, now)
                : null,
            Maintenance = features.HasFlag(GuildFeatures.Maintenance)
                ? await BuildMaintenanceAsync(guildId, userId, channels, now)
                : null,
            Away = features.HasFlag(GuildFeatures.Presence)
                ? await BuildAwayAsync(guildId, now)
                : null,
        };
    }

    private sealed record ChannelRef(string Id, string Name, ChannelType Type);

    private async Task<List<ChannelRef>> VisibleAsync(
        string guildId, string userId, List<ChannelRef> channels, ChannelType type)
    {
        var ofType = channels.Where(c => c.Type == type).ToList();
        if (ofType.Count == 0) return [];

        var allowed = await permissions.FilterChannelsWithPermissionAsync(
            userId, guildId, ofType.Select(c => c.Id).ToList(), Permissions.ViewChannel);

        return ofType.Where(c => allowed.Contains(c.Id)).ToList();
    }

    private async Task<HouseholdDigestChoresDto?> BuildChoresAsync(
        string guildId, string userId, List<ChannelRef> channels, DateTimeOffset now)
    {
        var visible = await VisibleAsync(guildId, userId, channels, ChannelType.Chores);
        if (visible.Count == 0) return null;

        var channelIds = visible.Select(c => c.Id).ToList();
        var horizon = now + ChoreLookahead;

        var outstanding = await ctx.ChoreOccurrences.AsNoTracking()
            .Where(o => channelIds.Contains(o.ChannelId)
                        && o.CompletedAt == null
                        && o.SkippedAt == null
                        && o.DueAt <= horizon)
            .OrderBy(o => o.DueAt)
            .ToListAsync();

        if (outstanding.Count == 0)
            return new HouseholdDigestChoresDto { Mine = [], MineOverdueCount = 0, HouseOverdueCount = 0 };

        // Grace is per chore, so overdue cannot be decided in SQL without a join the board query
        // does not do either. The candidate set is bounded by one day of the house's rota.
        var choreIds = outstanding.Select(o => o.ChoreId).Distinct().ToList();
        var chores = await ctx.Chores.AsNoTracking()
            .Where(c => choreIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title, c.GraceHours })
            .ToDictionaryAsync(c => c.Id, c => c);

        bool IsOverdue(ChoreOccurrence o) =>
            o.DueAt.AddHours(chores.TryGetValue(o.ChoreId, out var c) ? c.GraceHours : DefaultGraceHours) < now;

        var mine = outstanding.Where(o => o.AssignedUserId == userId).ToList();

        return new HouseholdDigestChoresDto
        {
            Mine = mine.Take(MaxChores).Select(o => new ChoreOccurrenceDto
            {
                Id = o.Id,
                ChoreId = o.ChoreId,
                ChannelId = o.ChannelId,
                Title = chores.TryGetValue(o.ChoreId, out var c) ? c.Title : "",
                DueAt = o.DueAt,
                AssignedUserId = o.AssignedUserId,
                EffortMinutes = o.EffortMinutes,
                CompletedAt = o.CompletedAt,
                CompletedByUserId = o.CompletedByUserId,
                SkippedAt = o.SkippedAt,
                IsOverdue = IsOverdue(o),
            }).ToList(),
            MineOverdueCount = mine.Count(IsOverdue),
            HouseOverdueCount = outstanding.Count(IsOverdue),
        };
    }

    private async Task<List<HouseholdDigestListDto>?> BuildListsAsync(
        string guildId, string userId, List<ChannelRef> channels)
    {
        var visible = await VisibleAsync(guildId, userId, channels, ChannelType.List);
        if (visible.Count == 0) return null;

        var channelIds = visible.Take(MaxListChannels).Select(c => c.Id).ToList();

        var open = await ctx.ListItems.AsNoTracking()
            .Where(i => channelIds.Contains(i.ChannelId) && !i.IsChecked)
            .OrderBy(i => i.Position)
            .ToListAsync();

        var byChannel = open.GroupBy(i => i.ChannelId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return visible.Take(MaxListChannels).Select(c =>
        {
            var items = byChannel.GetValueOrDefault(c.Id, []);

            return new HouseholdDigestListDto
            {
                ChannelId = c.Id,
                ChannelName = c.Name,
                OpenCount = items.Count,
                Preview = items.Take(MaxListPreview).Select(i => new ListItemDto
                {
                    Id = i.Id,
                    ChannelId = i.ChannelId,
                    Text = i.Text,
                    Quantity = i.Quantity,
                    Note = i.Note,
                    Section = i.Section,
                    AssigneeUserId = i.AssigneeUserId,
                    AddedByUserId = i.AddedByUserId,
                    IsChecked = i.IsChecked,
                    CheckedAt = i.CheckedAt,
                    CheckedByUserId = i.CheckedByUserId,
                    Position = i.Position,
                    SourcePantryItemId = i.SourcePantryItemId,
                    CreatedAt = i.CreatedAt,
                }).ToList(),
            };
        }).ToList();
    }

    private async Task<HouseholdDigestPantryDto?> BuildPantryAsync(
        string guildId, string userId, List<ChannelRef> channels, DateTimeOffset now)
    {
        var visible = await VisibleAsync(guildId, userId, channels, ChannelType.Pantry);
        if (visible.Count == 0) return null;

        var channelIds = visible.Select(c => c.Id).ToList();

        var horizons = await ctx.PantryConfigs.AsNoTracking()
            .Where(c => channelIds.Contains(c.ChannelId))
            .ToDictionaryAsync(c => c.ChannelId, c => c.ExpiryWarningDays);

        // Widest configured horizon in one query, then each channel's own applied in memory - the
        // same shape as the expiring board, and for the same reason.
        var widest = horizons.Count == 0 ? DefaultExpiryWarningDays : horizons.Values.Max();

        var candidates = await ctx.PantryItems.AsNoTracking()
            .Where(i => channelIds.Contains(i.ChannelId)
                        && i.ExpiresAt != null
                        && i.ExpiresAt <= now.AddDays(Math.Max(widest, DefaultExpiryWarningDays)))
            .OrderBy(i => i.ExpiresAt)
            .ToListAsync();

        var expiring = candidates
            .Where(i => i.ExpiresAt <= now.AddDays(
                horizons.GetValueOrDefault(i.ChannelId, DefaultExpiryWarningDays)))
            .ToList();

        return new HouseholdDigestPantryDto
        {
            ExpiringCount = expiring.Count,
            Soonest = expiring.Take(MaxPantryItems).Select(i => new PantryItemDto
            {
                Id = i.Id,
                ChannelId = i.ChannelId,
                Name = i.Name,
                Quantity = i.Quantity,
                Unit = i.Unit,
                LowThreshold = i.LowThreshold,
                ExpiresAt = i.ExpiresAt,
                IsLow = i.LowThreshold is not null && i.Quantity <= i.LowThreshold,
                RestockedAt = i.RestockedAt,
                AddedByUserId = i.AddedByUserId,
            }).ToList(),
        };
    }

    private async Task<List<HouseholdDigestLedgerDto>?> BuildLedgerAsync(
        string guildId, string userId, List<ChannelRef> channels)
    {
        var visible = await VisibleAsync(guildId, userId, channels, ChannelType.Ledger);
        if (visible.Count == 0) return null;

        var result = new List<HouseholdDigestLedgerDto>();

        // Capped rather than paged: a house has one ledger.
        foreach (var channel in visible.Take(MaxLedgerChannels))
        {
            var balances = await ledger.GetBalancesAsync(channel.Id);

            result.Add(new HouseholdDigestLedgerDto
            {
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                Currency = await ledger.GetCurrencyAsync(channel.Id),
                // Absent from the balance list means exactly square - GetBalancesAsync drops zeroes.
                MyNetMinor = balances.FirstOrDefault(b => b.UserId == userId)?.NetMinor ?? 0,
            });
        }

        return result;
    }

    private async Task<HouseholdDigestDecisionsDto?> BuildDecisionsAsync(
        string guildId, string userId, List<ChannelRef> channels, DateTimeOffset now)
    {
        var visible = await VisibleAsync(guildId, userId, channels, ChannelType.Decisions);
        if (visible.Count == 0) return null;

        var channelIds = visible.Select(c => c.Id).ToList();

        var open = await ctx.Decisions.AsNoTracking()
            .Where(d => channelIds.Contains(d.ChannelId)
                        && d.Status == DecisionStatus.Open
                        && (d.ClosesAt == null || d.ClosesAt > now))
            .OrderBy(d => d.ClosesAt == null)
            .ThenBy(d => d.ClosesAt)
            .Select(d => new
            {
                d.Id,
                d.ChannelId,
                d.Title,
                d.ClosesAt,
                IVoted = d.Votes.Any(v => v.UserId == userId),
            })
            .ToListAsync();

        return new HouseholdDigestDecisionsDto
        {
            OpenCount = open.Count,
            AwaitingMyVote = open
                .Where(d => !d.IVoted)
                .Take(MaxDecisions)
                .Select(d => new HouseholdDigestDecisionDto
                {
                    Id = d.Id,
                    ChannelId = d.ChannelId,
                    Title = d.Title,
                    ClosesAt = d.ClosesAt,
                })
                .ToList(),
        };
    }

    private async Task<HouseholdDigestBillsDto?> BuildBillsAsync(
        string guildId, string userId, List<ChannelRef> channels, DateTimeOffset now)
    {
        var visible = await VisibleAsync(guildId, userId, channels, ChannelType.Ledger);
        if (visible.Count == 0) return null;

        var channelIds = visible.Select(c => c.Id).ToList();
        var horizon = now + BillLookahead;

        // No lower bound on the due date, for the same reason the chores section has none: a bill
        // nobody ever posted is exactly what the overdue count is for, and clipping the candidate
        // set at some arbitrary age would make that count quietly wrong rather than merely short.
        var pending = await ctx.Set<BillOccurrence>().AsNoTracking()
            .Where(o => channelIds.Contains(o.ChannelId)
                        && o.Status == BillStatus.Pending
                        && o.DueAt <= horizon)
            .OrderBy(o => o.DueAt)
            .ToListAsync();

        if (pending.Count == 0)
            return new HouseholdDigestBillsDto { DueSoon = [], OverdueCount = 0, NeedsAmountCount = 0 };

        var shown = pending.Take(MaxBills).ToList();

        var shares = await ResolveMySharesAsync(guildId, userId, shown);

        // One currency lookup per channel rather than per bill: a house has one ledger, and the
        // capped list can easily be five bills off the same one.
        var currencies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var channelId in shown.Select(o => o.ChannelId).Distinct(StringComparer.Ordinal))
            currencies[channelId] = await ledger.GetCurrencyAsync(channelId);

        long? MyShare(string occurrenceId) =>
            shares.TryGetValue(occurrenceId, out var value) ? value : null;

        return new HouseholdDigestBillsDto
        {
            DueSoon = shown.Select(o => new HouseholdDigestBillDto
            {
                Id = o.Id,
                ChannelId = o.ChannelId,
                Description = o.Description,
                DueAt = o.DueAt,
                AmountMinor = o.AmountMinor,
                Currency = currencies.GetValueOrDefault(o.ChannelId, "CHF"),
                MyShareMinor = MyShare(o.Id),
                Status = o.Status,
            }).ToList(),
            OverdueCount = pending.Count(o => o.DueAt < now),
            NeedsAmountCount = pending.Count(o => o.NeedsAmount() && o.DueAt <= now),
        };
    }

    /// <summary>
    /// What each of these bills costs the caller, keyed by occurrence id, skipping the ones with no
    /// answer.
    /// </summary>
    private async Task<Dictionary<string, long>> ResolveMySharesAsync(
        string guildId, string userId, List<BillOccurrence> occurrences)
    {
        var resolved = new Dictionary<string, long>(StringComparer.Ordinal);

        var withAmount = occurrences.Where(o => o.AmountMinor is > 0).ToList();
        if (withAmount.Count == 0) return resolved;

        var templateIds = withAmount.Select(o => o.RecurringExpenseId).Distinct().ToList();

        var templates = await ctx.Set<RecurringExpense>().AsNoTracking()
            .Include(t => t.Shares)
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id);

        var members = await ctx.GuildMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => m.UserId)
            .Take(MaxSplitMembers)
            .ToListAsync();

        foreach (var occurrence in withAmount)
        {
            if (!templates.TryGetValue(occurrence.RecurringExpenseId, out var template)) continue;

            var participants = BillService.Participants(template, members);
            if (participants.Count == 0) continue;

            try
            {
                var split = ExpenseSplitter.Split(
                    occurrence.AmountMinor!.Value, template.SplitKind, participants);

                if (split.FirstOrDefault(s => s.UserId == userId) is { } mine)
                    resolved[occurrence.Id] = mine.AmountMinor;
            }
            catch (ArgumentException)
            {
                // A split the template can no longer satisfy.
            }
        }

        return resolved;
    }

    private async Task<HouseholdDigestMealsDto?> BuildMealsAsync(
        string guildId, string userId, List<ChannelRef> channels, DateTimeOffset now)
    {
        var visible = await VisibleAsync(guildId, userId, channels, ChannelType.Meals);
        if (visible.Count == 0) return null;

        var channelIds = visible.Select(c => c.Id).ToList();

        // UTC, the same calendar day the cooking-today sweep resolves.
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        var entries = await ctx.Set<MealPlanEntry>().AsNoTracking()
            .Where(e => channelIds.Contains(e.ChannelId) && e.Date == today)
            .OrderBy(e => e.Slot)
            .ThenBy(e => e.Position)
            .ToListAsync();

        if (entries.Count == 0)
            return new HouseholdDigestMealsDto { Today = [], ImCookingToday = false };

        var shown = entries.Take(MaxMeals).ToList();

        var recipeIds = shown.Where(e => e.RecipeId is not null).Select(e => e.RecipeId!).Distinct().ToList();

        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        if (recipeIds.Count > 0)
        {
            titles = await ctx.Set<Recipe>().AsNoTracking()
                .Where(r => recipeIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Title, StringComparer.Ordinal);
        }

        return new HouseholdDigestMealsDto
        {
            Today = shown.Select(e => new HouseholdDigestMealDto
            {
                Id = e.Id,
                ChannelId = e.ChannelId,
                Slot = e.Slot,
                Title = e.RecipeId is not null && titles.TryGetValue(e.RecipeId, out var title)
                    ? title
                    : e.FreeText ?? "",
                CookUserId = e.CookUserId,
            }).ToList(),
            ImCookingToday = entries.Any(e => e.CookUserId == userId),
        };
    }

    private async Task<HouseholdDigestMaintenanceDto?> BuildMaintenanceAsync(
        string guildId, string userId, List<ChannelRef> channels, DateTimeOffset now)
    {
        var visible = await VisibleAsync(guildId, userId, channels, ChannelType.Maintenance);
        if (visible.Count == 0) return null;

        var channelIds = visible.Select(c => c.Id).ToList();
        var warrantyHorizon = now.AddDays(MaintenanceAsset.WarrantyWarningDays);

        // The same predicate the attention board runs, so the digest and the board never disagree
        // about what is asking for a human.
        var candidates = await ctx.Set<MaintenanceAsset>().AsNoTracking()
            .Where(a => channelIds.Contains(a.ChannelId)
                        && (a.Status == AssetStatus.Broken
                            || a.Status == AssetStatus.NeedsAttention
                            || (a.NextServiceAt != null && a.NextServiceAt <= now)
                            || (a.WarrantyUntil != null
                                && a.WarrantyUntil > now
                                && a.WarrantyUntil <= warrantyHorizon)))
            .ToListAsync();

        return new HouseholdDigestMaintenanceDto
        {
            BrokenCount = candidates.Count(a => a.Status == AssetStatus.Broken),
            ServiceOverdueCount = candidates.Count(a => a.IsServiceOverdue(now)),
            WarrantyExpiringCount = candidates.Count(a => a.IsWarrantyExpiring(now)),
            // Ranked by urgency where the board sorts by name, and the cap is the reason: the board
            // shows everything, so alphabetical is a way to find a machine you already know about,
            // while five rows sorted alphabetically can push the broken boiler off the widget
            // entirely behind four warranty warnings.
            Attention = candidates
                .OrderBy(a => Urgency(a, now))
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .Take(MaxAssets)
                .Select(a => new HouseholdDigestAssetDto
                {
                    Id = a.Id,
                    ChannelId = a.ChannelId,
                    Name = a.Name,
                    Status = a.Status,
                    Reason = ReasonFor(a, now),
                })
                .ToList(),
        };
    }

    /// <summary>
    /// The most urgent thing wrong with an asset, as the attention board's own token.
    /// </summary>
    private static string ReasonFor(MaintenanceAsset asset, DateTimeOffset now) => Urgency(asset, now) switch
    {
        0 => "broken",
        1 => "service_overdue",
        2 => "needs_attention",
        _ => "warranty_expiring",
    };

    private static int Urgency(MaintenanceAsset asset, DateTimeOffset now)
    {
        if (asset.Status == AssetStatus.Broken) return 0;
        if (asset.IsServiceOverdue(now)) return 1;
        return asset.Status == AssetStatus.NeedsAttention ? 2 : 3;
    }

    /// <summary>Who is away at this instant.</summary>
    private async Task<IReadOnlyList<HouseholdDigestAbsenceDto>> BuildAwayAsync(
        string guildId, DateTimeOffset now)
    {
        // A window one tick wide, so the rota's own overlap predicate selects exactly what
        // MemberAbsence.Covers(now) accepts.
        var live = await AbsenceService.InWindowAsync(ctx, permissions, guildId, now, now.AddTicks(1));

        return live
            .Where(a => a.Covers(now))
            .OrderBy(a => a.EndAt)
            .ThenBy(a => a.UserId, StringComparer.Ordinal)
            .Take(MaxAbsences)
            .Select(a => new HouseholdDigestAbsenceDto
            {
                UserId = a.UserId,
                StartAt = a.StartAt,
                EndAt = a.EndAt,
                Note = a.Note,
            })
            .ToList();
    }
}
