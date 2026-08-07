using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The loop that keeps a shopping list honest: when a pantry item falls to its low-stock threshold,
/// it appends itself to the linked list channel.
/// </summary>
public class PantryRestockService(
    MicroserviceContext ctx, HouseholdChannelService household, HouseholdAlertService alerts)
{
    /// <summary>What one quantity change staged: the list line it produced, if any, and whether
    /// the house still has to be told the item ran low.</summary>
    public readonly record struct StockChange(ListItem? Restocked, bool WasLow);

    /// <summary>
    /// The whole low-stock loop for an item whose quantity has just changed, staged into the
    /// caller's unit of work.
    /// </summary>
    public async Task<StockChange> ApplyStockChangeAsync(PantryItem item)
    {
        // Back above the threshold: release both stamps so the next dip re-adds it to the list and
        // announces itself again. This is the other half of the idempotency guard.
        if (item.LowThreshold is null || item.Quantity > item.LowThreshold)
        {
            item.RestockedAt = null;
            item.LowNotifiedAt = null;
        }

        var wasLow = item.NeedsLowAlert();
        var restocked = await StageRestockAsync(item);
        if (wasLow) item.LowNotifiedAt = DateTimeOffset.UtcNow;

        return new StockChange(restocked, wasLow);
    }

    /// <summary>The after-commit half of <see cref="ApplyStockChangeAsync"/>.</summary>
    public async Task PublishStockChangeAsync(PantryItem item, StockChange change, string actorUserId)
    {
        if (change.Restocked is not null) await BroadcastRestockAsync(change.Restocked);
        if (change.WasLow) await alerts.PantryLowAsync(item, change.Restocked, actorUserId);
    }

    /// <summary>
    /// Ticks off the list lines this pantry item put on a shopping list, because stock arriving in
    /// the cupboard is the moment the line stops needing buying.
    /// </summary>
    public async Task<List<ListItem>> StageSourceLineCheckAsync(PantryItem item, string userId)
    {
        var lines = await ctx.ListItems
            .Where(i => i.SourcePantryItemId == item.Id && !i.IsChecked)
            .ToListAsync();

        foreach (var line in lines) line.Check(userId);

        return lines;
    }

    /// <summary>
    /// Appends <paramref name="item"/> to its pantry's restock list if it has just gone low.
    /// </summary>
    public async Task<ListItem?> StageRestockAsync(PantryItem item)
    {
        if (!item.NeedsRestock()) return null;

        var config = await ctx.PantryConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChannelId == item.ChannelId);

        if (config?.RestockListChannelId is null) return null;

        // The linked list may have been deleted or retyped since it was configured.
        var listChannel = await ctx.Channels.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == config.RestockListChannelId && c.Type == ChannelType.List);
        if (listChannel is null) return null;

        // Guard against a duplicate line that's already sitting unchecked on the list - possible
        // if RestockedAt was released by a delete while an older line survived.
        var alreadyListed = await ctx.ListItems.AnyAsync(i =>
            i.ChannelId == listChannel.Id && i.SourcePantryItemId == item.Id && !i.IsChecked);
        if (alreadyListed)
        {
            item.RestockedAt = DateTimeOffset.UtcNow;
            return null;
        }

        var maxPosition = await ctx.ListItems
            .Where(i => i.ChannelId == listChannel.Id)
            .Select(i => (int?)i.Position)
            .MaxAsync() ?? -1;

        var listItem = ListItem.Create(new CreateListItemParams
        {
            ChannelId = listChannel.Id,
            GuildId = listChannel.GuildId,
            Text = item.Name,
            Quantity = item.Unit is null ? null : $"{item.LowThreshold ?? item.Quantity} {item.Unit}".Trim(),
            Section = "Restock",
            // Attributed to whoever added the pantry item rather than to a synthetic system user:
            // Guild has no bot identity of its own here, and the client badges the line via
            // SourcePantryItemId anyway.
            AddedByUserId = item.AddedByUserId,
            Position = maxPosition + 1,
        });
        listItem.SourcePantryItemId = item.Id;

        ctx.ListItems.Add(listItem);
        item.RestockedAt = DateTimeOffset.UtcNow;

        return listItem;
    }

    /// <summary>Broadcasts a staged restock after the caller has committed.</summary>
    public async Task BroadcastRestockAsync(ListItem listItem) =>
        await household.BroadcastAsync(listItem.GuildId, listItem.ChannelId, "guild.ListItemCreated", new
        {
            GuildId = listItem.GuildId,
            ChannelId = listItem.ChannelId,
            Item = LinePayload(listItem),
        });

    /// <summary>Broadcasts a line ticked off by <see cref="StageSourceLineCheckAsync"/>, on the
    /// event the list already publishes rather than a pantry-specific one - a line going grey is
    /// the same fact to a client whoever ticked it, and a second event name would mean every client
    /// learning a second way to hear it.</summary>
    public async Task BroadcastCheckedAsync(ListItem listItem) =>
        await household.BroadcastAsync(listItem.GuildId, listItem.ChannelId, "guild.ListItemChecked", new
        {
            GuildId = listItem.GuildId,
            ChannelId = listItem.ChannelId,
            Item = LinePayload(listItem),
        });

    private static object LinePayload(ListItem listItem) => new
    {
        listItem.Id,
        listItem.ChannelId,
        listItem.Text,
        listItem.Quantity,
        listItem.Section,
        listItem.Position,
        listItem.SourcePantryItemId,
        listItem.IsChecked,
        listItem.CheckedAt,
        listItem.CheckedByUserId,
    };
}
