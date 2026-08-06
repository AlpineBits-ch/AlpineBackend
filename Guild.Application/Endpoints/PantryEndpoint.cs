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

        var now = DateTimeOffset.UtcNow;
        var overrideDays = days is null ? (int?)null : Math.Clamp(days.Value, 1, 90);

        // The widest horizon any pantry in this guild could ask for, so one query covers every
        // channel and the per-channel horizon is applied to the result.
        var configs = await ctx.PantryConfigs.AsNoTracking()
            .Where(c => c.GuildId == guildId)
            .ToDictionaryAsync(c => c.ChannelId, c => c.ExpiryWarningDays);

        var widestDays = overrideDays
                         ?? (configs.Count == 0 ? DefaultExpiryWarningDays : configs.Values.Max());

        var items = await ctx.PantryItems.AsNoTracking()
            .Where(i => i.GuildId == guildId
                        && i.ExpiresAt != null
                        && i.ExpiresAt <= now.AddDays(Math.Max(widestDays, DefaultExpiryWarningDays)))
            .OrderBy(i => i.ExpiresAt)
            .ToListAsync();

        if (items.Count == 0) return Results.Ok(Array.Empty<PantryItemDto>());

        // One resolve for every pantry on the board rather than one per item.
        var visibleChannels = await permissionService.FilterChannelsWithPermissionAsync(
            userId, guildId, items.Select(i => i.ChannelId).Distinct().ToList(), Permissions.ViewChannel);

        var visible = items.Where(i =>
        {
            if (!visibleChannels.Contains(i.ChannelId)) return false;

            var horizonDays = overrideDays
                              ?? (configs.TryGetValue(i.ChannelId, out var configured)
                                  ? configured
                                  : DefaultExpiryWarningDays);

            return i.ExpiresAt <= now.AddDays(horizonDays);
        });

        return Results.Ok(visible.Select(ToDto));
    }

    /// <summary>Matches PantryConfig's own default, so a pantry that was never configured behaves
    /// the same as one explicitly left at the default.</summary>
    private const int DefaultExpiryWarningDays = 3;

    [WolverinePost("/api/v1/channels/{channelId}/pantry-items")]
    public async Task<IResult> CreateAsync(string channelId, CreatePantryItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] PantryRestockService restock,
        [NotBody] HouseholdAlertService alerts, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
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

        await household.BroadcastAsync(item.GuildId, channelId, "guild.PantryItemCreated",
            new { GuildId = item.GuildId, ChannelId = channelId, Item = ToDto(item) });

        if (restocked is not null)
        {
            await restock.BroadcastRestockAsync(restocked);
            await alerts.RestockAddedAsync(restocked, userId);
        }

        return Results.Ok(ToDto(item));
    }

    [WolverinePatch("/api/v1/pantry-items/{itemId}")]
    public async Task<IResult> UpdateAsync(string itemId, UpdatePantryItemDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] PantryRestockService restock,
        [NotBody] HouseholdAlertService alerts, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
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

        // Moving the date makes any previous warning stale, so the stamp is released and the sweep
        // may warn again.
        if (dto.ClearExpiresAt == true)
        {
            item.ExpiresAt = null;
            item.ExpiryNotifiedAt = null;
        }
        else if (dto.ExpiresAt is not null && dto.ExpiresAt != item.ExpiresAt)
        {
            item.ExpiresAt = dto.ExpiresAt;
            item.ExpiryNotifiedAt = null;
        }

        // Restocked back above the threshold: release the stamp so the next dip re-adds it.
        if (item.LowThreshold is null || item.Quantity > item.LowThreshold) item.RestockedAt = null;

        var restocked = await restock.StageRestockAsync(item);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(item.GuildId, item.ChannelId, "guild.PantryItemUpdated",
            new { GuildId = item.GuildId, ChannelId = item.ChannelId, Item = ToDto(item) });

        if (restocked is not null)
        {
            await restock.BroadcastRestockAsync(restocked);
            await alerts.RestockAddedAsync(restocked, userId);
        }

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

        await household.BroadcastAsync(item.GuildId, item.ChannelId, "guild.PantryItemDeleted",
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
            ExpiryWarningDays = config?.ExpiryWarningDays ?? DefaultExpiryWarningDays,
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
