using Guild.Application.Dtos.Response;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
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

    private const int MaxChores = 10;
    private const int MaxListChannels = 5;
    private const int MaxListPreview = 5;
    private const int MaxPantryItems = 5;
    private const int MaxLedgerChannels = 3;
    private const int MaxDecisions = 5;
    private const int DefaultExpiryWarningDays = 3;
    private const int DefaultGraceHours = 24;

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
}
