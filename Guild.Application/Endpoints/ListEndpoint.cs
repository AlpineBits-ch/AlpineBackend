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
/// Items on a <see cref="ChannelType.List"/> channel - the shopping list and the todo list, which
/// are the same rows rendered differently.
/// </summary>
[Authorize]
public class ListEndpoint
{
    private const int MaxTextLength = 200;
    private const int MaxItemsPerList = 500;

    private static ListItemDto ToDto(ListItem item) => new()
    {
        Id = item.Id,
        ChannelId = item.ChannelId,
        Text = item.Text,
        Quantity = item.Quantity,
        Note = item.Note,
        Section = item.Section,
        AssigneeUserId = item.AssigneeUserId,
        AddedByUserId = item.AddedByUserId,
        IsChecked = item.IsChecked,
        CheckedAt = item.CheckedAt,
        CheckedByUserId = item.CheckedByUserId,
        Position = item.Position,
        SourcePantryItemId = item.SourcePantryItemId,
        CreatedAt = item.CreatedAt,
    };

    [WolverineGet("/api/v1/channels/{channelId}/list-items")]
    public async Task<IResult> ListItemsAsync(string channelId, bool includeChecked,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.List, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var query = ctx.ListItems.AsNoTracking().Where(i => i.ChannelId == channelId);
        if (!includeChecked) query = query.Where(i => !i.IsChecked);

        var items = await query
            .OrderBy(i => i.IsChecked)
            .ThenBy(i => i.Position)
            .ToListAsync();

        return Results.Ok(items.Select(ToDto));
    }

    [WolverinePost("/api/v1/channels/{channelId}/list-items")]
    public async Task<IResult> CreateItemAsync(string channelId, CreateListItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.List, userId, Permissions.AddListItems);
        if (access.ToFailure() is { } failure) return failure;

        if (string.IsNullOrWhiteSpace(dto.Text)) return Results.BadRequest("Text is required");
        if (dto.Text.Length > MaxTextLength) return Results.BadRequest($"Text must be {MaxTextLength} characters or fewer");

        var count = await ctx.ListItems.CountAsync(i => i.ChannelId == channelId);
        if (count >= MaxItemsPerList) return Results.BadRequest($"A list holds at most {MaxItemsPerList} items");

        // Append to the end of the unchecked block.
        var maxPosition = await ctx.ListItems
            .Where(i => i.ChannelId == channelId)
            .Select(i => (int?)i.Position)
            .MaxAsync() ?? -1;

        var item = ListItem.Create(new CreateListItemParams
        {
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            Text = dto.Text.Trim(),
            Quantity = dto.Quantity,
            Note = dto.Note,
            Section = dto.Section,
            AssigneeUserId = dto.AssigneeUserId,
            AddedByUserId = userId,
            Position = maxPosition + 1,
        });

        ctx.ListItems.Add(item);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(access.Channel.GuildId, channelId, "guild.ListItemCreated",
            new { GuildId = access.Channel.GuildId, ChannelId = channelId, Item = ToDto(item) });

        return Results.Ok(ToDto(item));
    }

    [WolverinePatch("/api/v1/list-items/{itemId}")]
    public async Task<IResult> UpdateItemAsync(string itemId, UpdateListItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var item = await ctx.ListItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return Results.NotFound();

        // Editing your own item needs only AddListItems; editing someone else's is a moderator
        // action.
        var required = item.AddedByUserId == userId ? Permissions.AddListItems : Permissions.ManageLists;
        var access = await household.ResolveAsync(item.ChannelId, ChannelType.List, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.Text is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Text)) return Results.BadRequest("Text cannot be empty");
            if (dto.Text.Length > MaxTextLength) return Results.BadRequest($"Text must be {MaxTextLength} characters or fewer");
            item.Text = dto.Text.Trim();
        }

        if (dto.Quantity is not null) item.Quantity = dto.Quantity;
        if (dto.Note is not null) item.Note = dto.Note;
        if (dto.Section is not null) item.Section = dto.Section;

        if (dto.ClearAssignee == true) item.AssigneeUserId = null;
        else if (dto.AssigneeUserId is not null) item.AssigneeUserId = dto.AssigneeUserId;

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(item.GuildId, item.ChannelId, "guild.ListItemUpdated",
            new { GuildId = item.GuildId, ChannelId = item.ChannelId, Item = ToDto(item) });

        return Results.Ok(ToDto(item));
    }

    [WolverinePost("/api/v1/list-items/{itemId}/check")]
    public Task<IResult> CheckItemAsync(string itemId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user) =>
        SetCheckedAsync(itemId, true, household, ctx, user);

    [WolverineDelete("/api/v1/list-items/{itemId}/check")]
    public Task<IResult> UncheckItemAsync(string itemId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user) =>
        SetCheckedAsync(itemId, false, household, ctx, user);

    private static async Task<IResult> SetCheckedAsync(string itemId, bool isChecked,
        HouseholdChannelService household, MicroserviceContext ctx, ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var item = await ctx.ListItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return Results.NotFound();

        var access = await household.ResolveAsync(item.ChannelId, ChannelType.List, userId, Permissions.CheckOffListItems);
        if (access.ToFailure() is { } failure) return failure;

        // Idempotent: two phones ticking the same item shouldn't produce two events with
        // different CheckedByUserId values, and the second tap is almost always a double-send.
        if (item.IsChecked == isChecked) return Results.Ok(ToDto(item));

        if (isChecked) item.Check(userId);
        else item.Uncheck();

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(item.GuildId, item.ChannelId, "guild.ListItemChecked",
            new { GuildId = item.GuildId, ChannelId = item.ChannelId, Item = ToDto(item) });

        return Results.Ok(ToDto(item));
    }

    [WolverineDelete("/api/v1/list-items/{itemId}")]
    public async Task<IResult> DeleteItemAsync(string itemId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var item = await ctx.ListItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return Results.NotFound();

        var required = item.AddedByUserId == userId ? Permissions.AddListItems : Permissions.ManageLists;
        var access = await household.ResolveAsync(item.ChannelId, ChannelType.List, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        // A list item that came from the pantry releases its restock stamp on the way out, so the
        // pantry can put it back on the list next time it's low.
        if (item.SourcePantryItemId is not null)
        {
            var pantryItem = await ctx.PantryItems.FirstOrDefaultAsync(p => p.Id == item.SourcePantryItemId);
            if (pantryItem is not null) pantryItem.RestockedAt = null;
        }

        ctx.ListItems.Remove(item);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(item.GuildId, item.ChannelId, "guild.ListItemDeleted",
            new { GuildId = item.GuildId, ChannelId = item.ChannelId, ItemId = itemId });

        return Results.NoContent();
    }

    [WolverineDelete("/api/v1/channels/{channelId}/list-items/checked")]
    public async Task<IResult> ClearCheckedAsync(string channelId,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.List, userId, Permissions.ManageLists);
        if (access.ToFailure() is { } failure) return failure;

        var checkedItems = await ctx.ListItems
            .Where(i => i.ChannelId == channelId && i.IsChecked)
            .ToListAsync();

        if (checkedItems.Count == 0) return Results.NoContent();

        // Same pantry-stamp release as single deletion above - these items were actually bought,
        // so the pantry should be free to re-add them when they run low again.
        var pantryItemIds = checkedItems
            .Where(i => i.SourcePantryItemId is not null)
            .Select(i => i.SourcePantryItemId!)
            .ToList();

        if (pantryItemIds.Count > 0)
        {
            var pantryItems = await ctx.PantryItems.Where(p => pantryItemIds.Contains(p.Id)).ToListAsync();
            foreach (var pantryItem in pantryItems) pantryItem.RestockedAt = null;
        }

        ctx.ListItems.RemoveRange(checkedItems);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(access.Channel!.GuildId, channelId, "guild.ListCleared",
            new { GuildId = access.Channel.GuildId, ChannelId = channelId, RemovedCount = checkedItems.Count });

        return Results.NoContent();
    }

    [WolverinePost("/api/v1/channels/{channelId}/list-items/reorder")]
    public async Task<IResult> ReorderAsync(string channelId, ReorderListItemsDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.List, userId, Permissions.AddListItems);
        if (access.ToFailure() is { } failure) return failure;

        var items = await ctx.ListItems.Where(i => i.ChannelId == channelId).ToListAsync();
        var byId = items.ToDictionary(i => i.Id);

        if (dto.ItemIds.Any(id => !byId.ContainsKey(id)))
            return Results.BadRequest("Every item id must belong to this list");

        for (var i = 0; i < dto.ItemIds.Count; i++) byId[dto.ItemIds[i]].Position = i;

        // Items the client didn't mention keep their relative order after the ones it did, rather
        // than colliding at position 0 - a partial reorder is a normal drag-and-drop payload.
        var next = dto.ItemIds.Count;
        foreach (var item in items.Where(i => !dto.ItemIds.Contains(i.Id)).OrderBy(i => i.Position))
            item.Position = next++;

        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(access.Channel!.GuildId, channelId, "guild.ListItemsReordered",
            new { GuildId = access.Channel.GuildId, ChannelId = channelId, ItemIds = dto.ItemIds });

        return Results.NoContent();
    }
}
