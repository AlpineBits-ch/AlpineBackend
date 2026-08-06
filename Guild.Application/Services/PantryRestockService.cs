using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Application.Services;

/// <summary>
/// The loop that keeps a shopping list honest: when a pantry item falls to its low-stock threshold,
/// it appends itself to the linked list channel.
/// </summary>
public class PantryRestockService(MicroserviceContext ctx, HouseholdChannelService household)
{
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
    public async Task BroadcastRestockAsync(ListItem listItem)
    {
        await household.BroadcastAsync(listItem.GuildId, listItem.ChannelId, "guild.ListItemCreated", new
        {
            GuildId = listItem.GuildId,
            ChannelId = listItem.ChannelId,
            Item = new
            {
                listItem.Id,
                listItem.ChannelId,
                listItem.Text,
                listItem.Quantity,
                listItem.Section,
                listItem.Position,
                listItem.SourcePantryItemId,
                IsChecked = false,
            },
        });
    }
}
