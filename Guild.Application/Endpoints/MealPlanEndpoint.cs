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
/// The week's plan on a <see cref="ChannelType.Meals"/> channel, and the one button the whole
/// module is built around: turning that plan into a shopping list.
/// </summary>
[Authorize]
public class MealPlanEndpoint
{
    private static MealPlanEntryDto ToDto(MealPlanEntry entry, string? recipeTitle) => new()
    {
        Id = entry.Id,
        ChannelId = entry.ChannelId,
        Date = entry.Date,
        Slot = entry.Slot,
        RecipeId = entry.RecipeId,
        RecipeTitle = recipeTitle,
        FreeText = entry.FreeText,
        CookUserId = entry.CookUserId,
        Servings = entry.Servings,
        Position = entry.Position,
    };

    // ── The board ────────────────────────────────────────────────────────────

    [WolverineGet("/api/v1/channels/{channelId}/meal-plan")]
    public async Task<IResult> GetPlanAsync(string channelId, DateOnly? from, DateOnly? to,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Meals, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        // A week from today is what a client opening the board wants, so it never has to compute
        // the default itself and no two clients disagree about where the week starts.
        var start = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var end = to ?? start.AddDays(6);

        if (end < start) return Results.BadRequest("To must not be before From");

        if (end.DayNumber - start.DayNumber >= MealPlanEntry.MaxWindowDays)
            return Results.BadRequest($"The window must be shorter than {MealPlanEntry.MaxWindowDays} days");

        var entries = await ctx.Set<MealPlanEntry>().AsNoTracking()
            .Where(e => e.ChannelId == channelId && e.Date >= start && e.Date <= end)
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Slot)
            .ThenBy(e => e.Position)
            .ToListAsync();

        var titles = await RecipeTitlesAsync(ctx, entries);

        return Results.Ok(entries
            .Select(e => ToDto(e, e.RecipeId is null ? null : titles.GetValueOrDefault(e.RecipeId)))
            .ToList());
    }

    [WolverinePost("/api/v1/channels/{channelId}/meal-plan")]
    public async Task<IResult> CreateEntryAsync(string channelId, CreateMealPlanEntryDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Meals, userId, Permissions.PlanMeals);
        if (access.ToFailure() is { } failure) return failure;

        if (ValidateSubject(dto.RecipeId, dto.FreeText) is { } invalid) return Results.BadRequest(invalid);
        if (dto.Servings is < Recipe.MinServings or > Recipe.MaxServings)
            return Results.BadRequest($"Servings must be between {Recipe.MinServings} and {Recipe.MaxServings}");

        if (dto.RecipeId is not null)
        {
            // Same channel, not merely the same guild: a meals channel is one household's plan and
            // pulling a recipe across from another one would put a title on the board that half the
            // readers get a 403 on.
            var exists = await ctx.Set<Recipe>().AnyAsync(r => r.Id == dto.RecipeId && r.ChannelId == channelId);
            if (!exists) return Results.BadRequest("RecipeId must be a recipe on this channel");
        }

        var maxPosition = await ctx.Set<MealPlanEntry>()
            .Where(e => e.ChannelId == channelId && e.Date == dto.Date && e.Slot == dto.Slot)
            .Select(e => (int?)e.Position)
            .MaxAsync() ?? -1;

        var entry = MealPlanEntry.Create(new CreateMealPlanEntryParams
        {
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            Date = dto.Date,
            Slot = dto.Slot,
            RecipeId = dto.RecipeId,
            FreeText = dto.FreeText?.Trim(),
            CookUserId = dto.CookUserId,
            Servings = dto.Servings,
            Position = maxPosition + 1,
            CreatedByUserId = userId,
        });

        ctx.Set<MealPlanEntry>().Add(entry);
        await ctx.SaveChangesAsync();

        var titles = await RecipeTitlesAsync(ctx, [entry]);
        var payload = ToDto(entry, entry.RecipeId is null ? null : titles.GetValueOrDefault(entry.RecipeId));

        await household.BroadcastAsync(entry.GuildId, channelId, "guild.MealPlanEntryCreated",
            new { GuildId = entry.GuildId, ChannelId = channelId, Entry = payload });

        return Results.Ok(payload);
    }

    [WolverinePatch("/api/v1/meal-plan/{entryId}")]
    public async Task<IResult> UpdateEntryAsync(string entryId, UpdateMealPlanEntryDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var entry = await ctx.Set<MealPlanEntry>().FirstOrDefaultAsync(e => e.Id == entryId);
        if (entry is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            entry.ChannelId, ChannelType.Meals, userId, RequiredFor(entry, userId));

        if (access.ToFailure() is { } failure) return failure;

        var recipeId = dto.ClearRecipe ? null : dto.RecipeId ?? entry.RecipeId;
        var freeText = dto.ClearFreeText ? null : dto.FreeText?.Trim() ?? entry.FreeText;

        if (ValidateSubject(recipeId, freeText) is { } invalid) return Results.BadRequest(invalid);
        if (dto.Servings is < Recipe.MinServings or > Recipe.MaxServings)
            return Results.BadRequest($"Servings must be between {Recipe.MinServings} and {Recipe.MaxServings}");

        if (recipeId is not null && recipeId != entry.RecipeId)
        {
            var exists = await ctx.Set<Recipe>().AnyAsync(r => r.Id == recipeId && r.ChannelId == entry.ChannelId);
            if (!exists) return Results.BadRequest("RecipeId must be a recipe on this channel");
        }

        // Re-dating an entry or handing it to a different cook makes any reminder already sent
        // refer to something that is no longer true, so the at-most-once stamp is released and the
        // new cook is told about the new day.
        if ((dto.Date is not null && dto.Date != entry.Date)
            || (dto.ClearCook && entry.CookUserId is not null)
            || (dto.CookUserId is not null && dto.CookUserId != entry.CookUserId))
        {
            entry.NotifiedAt = null;
        }

        if (dto.Date is not null) entry.Date = dto.Date.Value;
        if (dto.Slot is not null) entry.Slot = dto.Slot.Value;
        if (dto.Position is not null) entry.Position = dto.Position.Value;

        entry.RecipeId = recipeId;
        entry.FreeText = freeText;

        if (dto.ClearCook) entry.CookUserId = null;
        else if (dto.CookUserId is not null) entry.CookUserId = dto.CookUserId;

        if (dto.ClearServings) entry.Servings = null;
        else if (dto.Servings is not null) entry.Servings = dto.Servings;

        entry.UpdatedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();

        var titles = await RecipeTitlesAsync(ctx, [entry]);
        var payload = ToDto(entry, entry.RecipeId is null ? null : titles.GetValueOrDefault(entry.RecipeId));

        await household.BroadcastAsync(entry.GuildId, entry.ChannelId, "guild.MealPlanEntryUpdated",
            new { GuildId = entry.GuildId, ChannelId = entry.ChannelId, Entry = payload });

        return Results.Ok(payload);
    }

    [WolverineDelete("/api/v1/meal-plan/{entryId}")]
    public async Task<IResult> DeleteEntryAsync(string entryId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var entry = await ctx.Set<MealPlanEntry>().FirstOrDefaultAsync(e => e.Id == entryId);
        if (entry is null) return Results.NotFound();

        var access = await household.ResolveAsync(
            entry.ChannelId, ChannelType.Meals, userId, RequiredFor(entry, userId));

        if (access.ToFailure() is { } failure) return failure;

        ctx.Set<MealPlanEntry>().Remove(entry);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(entry.GuildId, entry.ChannelId, "guild.MealPlanEntryDeleted",
            new { GuildId = entry.GuildId, ChannelId = entry.ChannelId, EntryId = entryId });

        return Results.NoContent();
    }

    // ── Plan to shopping list ────────────────────────────────────────────────

    /// <summary>
    /// Collapses the planned week into lines on the house's shopping list, minus whatever the
    /// pantry already has and whatever is already on the list.
    /// </summary>
    [WolverinePost("/api/v1/channels/{channelId}/meal-plan/shopping-list")]
    public async Task<IResult> GenerateShoppingListAsync(string channelId, GenerateShoppingListDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MealPlanService meals,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Meals, userId, Permissions.PlanMeals);
        if (access.ToFailure() is { } failure) return failure;

        if (dto.To < dto.From) return Results.BadRequest("To must not be before From");

        if (dto.To.DayNumber - dto.From.DayNumber >= MealPlanEntry.MaxWindowDays)
            return Results.BadRequest($"The window must be shorter than {MealPlanEntry.MaxWindowDays} days");

        var config = await ctx.Set<MealPlanConfig>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChannelId == channelId);

        var listChannelId = MealPlanService.ResolveTargetListChannelId(
            dto.ListChannelId, config?.ShoppingListChannelId);

        if (listChannelId is null)
            return Results.BadRequest("No shopping list is configured - pass ListChannelId or set one in the meals config");

        var listAccess = await household.ResolveAsync(
            listChannelId, ChannelType.List, userId, Permissions.AddListItems);

        if (listAccess.ToFailure() is { } listFailure) return listFailure;

        if (listAccess.Channel!.GuildId != access.Channel!.GuildId)
            return Results.BadRequest("ListChannelId must be a List channel in this guild");

        var result = await meals.AppendPlanToListAsync(new MealPlanService.ShoppingListPlanRequest
        {
            MealsChannelId = channelId,
            GuildId = access.Channel.GuildId,
            ListChannel = listAccess.Channel,
            PantryChannelId = config?.PantryChannelId,
            From = dto.From,
            To = dto.To,
            IncludeOptional = dto.IncludeOptional,
            SkipPantry = dto.SkipPantry,
            ActorUserId = userId,
        });

        return Results.Ok(result);
    }

    /// <summary>What the house could cook tonight out of things that are about to go off.</summary>
    [WolverineGet("/api/v1/channels/{channelId}/recipes/cookable")]
    public async Task<IResult> CookableAsync(string channelId, int? expiringDays, int? limit,
        [NotBody] HouseholdChannelService household, [NotBody] MealPlanService meals,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Meals, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var days = Math.Clamp(expiringDays ?? MealPlanService.DefaultExpiringDays, 1, 90);

        var result = await meals.RankCookableAsync(
            channelId, access.Channel!.GuildId, days, limit ?? MealPlanService.DefaultCookableLimit);

        return Results.Ok(result);
    }

    // ── Config ───────────────────────────────────────────────────────────────

    [WolverineGet("/api/v1/channels/{channelId}/meals/config")]
    public async Task<IResult> GetConfigAsync(string channelId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Meals, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var config = await ctx.Set<MealPlanConfig>().AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChannelId == channelId);

        // Defaulted rather than 404 - "the config" always exists conceptually, the same convention
        // as PantryConfig and ForumConfig.
        return Results.Ok(new MealPlanConfigDto
        {
            ChannelId = channelId,
            ShoppingListChannelId = config?.ShoppingListChannelId,
            PantryChannelId = config?.PantryChannelId,
        });
    }

    [WolverinePut("/api/v1/channels/{channelId}/meals/config")]
    public async Task<IResult> UpdateConfigAsync(string channelId, UpdateMealPlanConfigDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Meals, userId, Permissions.ManageMeals);
        if (access.ToFailure() is { } failure) return failure;

        var guildId = access.Channel!.GuildId;

        var config = await ctx.Set<MealPlanConfig>().FirstOrDefaultAsync(c => c.ChannelId == channelId);

        string? shoppingListChannelId = dto.ClearShoppingList ? null : config?.ShoppingListChannelId;
        string? pantryChannelId = dto.ClearPantry ? null : config?.PantryChannelId;

        if (!dto.ClearShoppingList && dto.ShoppingListChannelId is not null)
        {
            if (!await IsChannelOfTypeAsync(ctx, dto.ShoppingListChannelId, ChannelType.List, guildId))
                return Results.BadRequest("ShoppingListChannelId must be a List channel in this guild");

            shoppingListChannelId = dto.ShoppingListChannelId;
        }

        if (!dto.ClearPantry && dto.PantryChannelId is not null)
        {
            if (!await IsChannelOfTypeAsync(ctx, dto.PantryChannelId, ChannelType.Pantry, guildId))
                return Results.BadRequest("PantryChannelId must be a Pantry channel in this guild");

            pantryChannelId = dto.PantryChannelId;
        }

        if (config is null)
        {
            config = new MealPlanConfig { ChannelId = channelId, GuildId = guildId };
            ctx.Set<MealPlanConfig>().Add(config);
        }

        config.ShoppingListChannelId = shoppingListChannelId;
        config.PantryChannelId = pantryChannelId;
        config.UpdatedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();

        return Results.Ok(new MealPlanConfigDto
        {
            ChannelId = channelId,
            ShoppingListChannelId = config.ShoppingListChannelId,
            PantryChannelId = config.PantryChannelId,
        });
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>Editing an entry you put on the board, or one you are down to cook, needs only
    /// <see cref="Permissions.PlanMeals"/>; editing anybody else's is a moderator action. Swapping
    /// yourself out of Thursday is the single most common edit there is, and needing
    /// <see cref="Permissions.ManageMeals"/> for it would mean asking permission to not cook.</summary>
    private static Permissions RequiredFor(MealPlanEntry entry, string userId) =>
        entry.CreatedByUserId == userId || entry.CookUserId == userId
            ? Permissions.PlanMeals
            : Permissions.ManageMeals;

    /// <summary>An entry has to be about something.</summary>
    private static string? ValidateSubject(string? recipeId, string? freeText)
    {
        if (recipeId is null && string.IsNullOrWhiteSpace(freeText))
            return "An entry needs either a RecipeId or FreeText";

        if (freeText is not null && freeText.Length > MealPlanEntry.MaxFreeTextLength)
            return $"FreeText must be {MealPlanEntry.MaxFreeTextLength} characters or fewer";

        return null;
    }

    private static async Task<bool> IsChannelOfTypeAsync(
        MicroserviceContext ctx, string channelId, ChannelType type, string guildId) =>
        await ctx.Channels.AsNoTracking()
            .AnyAsync(c => c.Id == channelId && c.Type == type && c.GuildId == guildId);

    private static async Task<Dictionary<string, string>> RecipeTitlesAsync(
        MicroserviceContext ctx, IReadOnlyList<MealPlanEntry> entries)
    {
        var recipeIds = entries
            .Where(e => e.RecipeId != null)
            .Select(e => e.RecipeId!)
            .Distinct()
            .ToList();

        if (recipeIds.Count == 0) return new Dictionary<string, string>();

        return await ctx.Set<Recipe>().AsNoTracking()
            .Where(r => recipeIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Title })
            .ToDictionaryAsync(r => r.Id, r => r.Title);
    }
}
