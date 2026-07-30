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

/// <summary>Stock tracking on a <see cref="ChannelType.Pantry"/> channel.</summary>
[Authorize]
public class PantryEndpoint
{
    private const int MaxNameLength = 100;

    private static PantryItemDto ToDto(PantryItem item) => new()
    {
        Id = item.Id,
        ChannelId = item.ChannelId,
        Name = item.Name,
        Quantity = item.Quantity,
        Unit = item.Unit,
        LowThreshold = item.LowThreshold,
        ExpiresAt = item.ExpiresAt,
        IsLow = item.LowThreshold is not null && item.Quantity <= item.LowThreshold,
        RestockedAt = item.RestockedAt,
        AddedByUserId = item.AddedByUserId,
    };

    [WolverineGet("/api/v1/channels/{channelId}/pantry-items")]
    public async Task<IResult> ListAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Pantry, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var items = await ctx.PantryItems.AsNoTracking()
            .Where(i => i.ChannelId == channelId)
            .OrderBy(i => i.Name)
            .ToListAsync();

        return Results.Ok(items.Select(ToDto));
    }

    /// <summary>The "eat me first" board: what's about to go off, soonest first.</summary>
    [WolverineGet("/api/v1/guilds/{guildId}/pantry/expiring")]
    public async Task<IResult> ExpiringAsync(string guildId, int? days,
        [NotBody] GuildPermissionService permissionService, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        if (!await permissionService.IsFeatureEnabledAsync(guildId, GuildFeatures.Pantry))
            return Results.Forbid();

        if (!await ctx.GuildMembers.AnyAsync(m => m.GuildId == guildId && m.UserId == userId))
            return Results.Forbid();

        var horizon = DateTimeOffset.UtcNow.AddDays(Math.Clamp(days ?? 3, 1, 90));

        var items = await ctx.PantryItems.AsNoTracking()
            .Where(i => i.GuildId == guildId && i.ExpiresAt != null && i.ExpiresAt <= horizon)
            .OrderBy(i => i.ExpiresAt)
            .ToListAsync();

        // Filter to pantries this member can actually see - a guest with access to the kitchen
        // pantry shouldn't learn what's in a private one via this board.
        var visible = new List<PantryItem>();
        foreach (var item in items)
        {
            if (await permissionService.CanUserPerformActionAsync(userId, item.ChannelId, Permissions.ViewChannel))
                visible.Add(item);
        }

        return Results.Ok(visible.Select(ToDto));
    }

    [WolverinePost("/api/v1/channels/{channelId}/pantry-items")]
    public async Task<IResult> CreateAsync(string channelId, CreatePantryItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] PantryRestockService restock,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Pantry, userId, Permissions.ManagePantry);
        if (access.ToFailure() is { } failure) return failure;

        if (string.IsNullOrWhiteSpace(dto.Name)) return Results.BadRequest("Name is required");
        if (dto.Name.Length > MaxNameLength) return Results.BadRequest($"Name must be {MaxNameLength} characters or fewer");
        if (dto.Quantity < 0) return Results.BadRequest("Quantity cannot be negative");
        if (dto.LowThreshold is < 0) return Results.BadRequest("LowThreshold cannot be negative");

        var item = PantryItem.Create(new CreatePantryItemParams
        {
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            Name = dto.Name.Trim(),
            Quantity = dto.Quantity,
            Unit = dto.Unit,
            LowThreshold = dto.LowThreshold,
            ExpiresAt = dto.ExpiresAt,
            AddedByUserId = userId,
        });

        ctx.PantryItems.Add(item);
        var restocked = await restock.StageRestockAsync(item);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(item.GuildId, "guild.PantryItemCreated",
            new { GuildId = item.GuildId, ChannelId = channelId, Item = ToDto(item) });
        if (restocked is not null) await restock.BroadcastRestockAsync(restocked);

        return Results.Ok(ToDto(item));
    }

    [WolverinePatch("/api/v1/pantry-items/{itemId}")]
    public async Task<IResult> UpdateAsync(string itemId, UpdatePantryItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] PantryRestockService restock,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var item = await ctx.PantryItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return Results.NotFound();

        var access = await household.ResolveAsync(item.ChannelId, ChannelType.Pantry, userId, Permissions.ManagePantry);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.Name)) return Results.BadRequest("Name cannot be empty");
            if (dto.Name.Length > MaxNameLength) return Results.BadRequest($"Name must be {MaxNameLength} characters or fewer");
            item.Name = dto.Name.Trim();
        }

        if (dto.Quantity is not null)
        {
            if (dto.Quantity < 0) return Results.BadRequest("Quantity cannot be negative");
            item.Quantity = dto.Quantity.Value;
        }

        if (dto.Unit is not null) item.Unit = dto.Unit;

        if (dto.ClearLowThreshold == true) item.LowThreshold = null;
        else if (dto.LowThreshold is not null)
        {
            if (dto.LowThreshold < 0) return Results.BadRequest("LowThreshold cannot be negative");
            item.LowThreshold = dto.LowThreshold;
        }

        if (dto.ClearExpiresAt == true) item.ExpiresAt = null;
        else if (dto.ExpiresAt is not null) item.ExpiresAt = dto.ExpiresAt;

        // Restocked back above the threshold: release the stamp so the next dip re-adds it.
        if (item.LowThreshold is null || item.Quantity > item.LowThreshold) item.RestockedAt = null;

        var restocked = await restock.StageRestockAsync(item);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(item.GuildId, "guild.PantryItemUpdated",
            new { GuildId = item.GuildId, ChannelId = item.ChannelId, Item = ToDto(item) });
        if (restocked is not null) await restock.BroadcastRestockAsync(restocked);

        return Results.Ok(ToDto(item));
    }

    [WolverineDelete("/api/v1/pantry-items/{itemId}")]
    public async Task<IResult> DeleteAsync(string itemId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var item = await ctx.PantryItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return Results.NotFound();

        var access = await household.ResolveAsync(item.ChannelId, ChannelType.Pantry, userId, Permissions.ManagePantry);
        if (access.ToFailure() is { } failure) return failure;

        ctx.PantryItems.Remove(item);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(item.GuildId, "guild.PantryItemDeleted",
            new { GuildId = item.GuildId, ChannelId = item.ChannelId, ItemId = itemId });

        return Results.NoContent();
    }

    [WolverineGet("/api/v1/channels/{channelId}/pantry/config")]
    public async Task<IResult> GetConfigAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Pantry, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var config = await ctx.PantryConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.ChannelId == channelId);

        // Defaulted rather than 404 - "the config" always exists conceptually, same convention as
        // ForumConfig and GuildOnboardingConfig.
        return Results.Ok(new PantryConfigDto
        {
            ChannelId = channelId,
            RestockListChannelId = config?.RestockListChannelId,
            ExpiryWarningDays = config?.ExpiryWarningDays ?? 3,
        });
    }

    [WolverinePut("/api/v1/channels/{channelId}/pantry/config")]
    public async Task<IResult> UpdateConfigAsync(string channelId, UpdatePantryConfigDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Pantry, userId, Permissions.ManagePantry);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.ExpiryWarningDays is < 1 or > 90)
            return Results.BadRequest("ExpiryWarningDays must be between 1 and 90");

        string? restockListChannelId = null;
        if (dto.ClearRestockList != true && dto.RestockListChannelId is not null)
        {
            var listChannel = await ctx.Channels.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == dto.RestockListChannelId);

            if (listChannel is null || listChannel.Type != ChannelType.List ||
                listChannel.GuildId != access.Channel!.GuildId)
                return Results.BadRequest("RestockListChannelId must be a List channel in this guild");

            restockListChannelId = listChannel.Id;
        }

        var config = await ctx.PantryConfigs.FirstOrDefaultAsync(c => c.ChannelId == channelId);
        if (config is null)
        {
            config = new PantryConfig { ChannelId = channelId, GuildId = access.Channel!.GuildId };
            ctx.PantryConfigs.Add(config);
        }

        config.RestockListChannelId = restockListChannelId;
        config.ExpiryWarningDays = dto.ExpiryWarningDays ?? config.ExpiryWarningDays;
        config.UpdatedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();

        return Results.Ok(new PantryConfigDto
        {
            ChannelId = channelId,
            RestockListChannelId = config.RestockListChannelId,
            ExpiryWarningDays = config.ExpiryWarningDays,
        });
    }
}
