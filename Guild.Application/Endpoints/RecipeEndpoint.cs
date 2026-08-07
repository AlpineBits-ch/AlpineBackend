using System.Security.Claims;
using Guild.Application.Dtos.Request;
using Guild.Application.Dtos.Response;
using Guild.Application.Services;
using Guild.Domain.Entity;
using Guild.Domain.Enums;
using Guild.Domain.Services;
using Guild.Persistence.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

namespace Guild.Application.Endpoints;

/// <summary>The cookbook half of a <see cref="ChannelType.Meals"/> channel.</summary>
[Authorize]
public class RecipeEndpoint
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    [WolverineGet("/api/v1/channels/{channelId}/recipes")]
    public async Task<IResult> ListAsync(string channelId, int? limit, string? cursor,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Meals, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var query = ctx.Set<Recipe>().AsNoTracking()
            .Include(r => r.Ingredients)
            .Where(r => r.ChannelId == channelId);

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            // Composite {title}|{id}, the convention the rest of the service pages on: titles are
            // not unique and a title-only cursor would skip every recipe a house named twice.
            var separator = cursor.LastIndexOf('|');
            if (separator <= 0 || separator == cursor.Length - 1)
                return Results.BadRequest("Malformed cursor");

            var afterTitle = cursor[..separator];
            var afterId = cursor[(separator + 1)..];

            query = query.Where(r => string.Compare(r.Title, afterTitle) > 0
                                     || (r.Title == afterTitle && string.Compare(r.Id, afterId) > 0));
        }

        // One extra row is the has-more probe, so a client never makes a second request purely to
        // learn the list ended.
        var page = await query
            .OrderBy(r => r.Title)
            .ThenBy(r => r.Id)
            .Take(take + 1)
            .ToListAsync();

        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);

        return Results.Ok(new RecipePageDto
        {
            Items = page.Select(MealPlanService.ToRecipeDto).ToList(),
            NextCursor = hasMore && page.Count > 0 ? $"{page[^1].Title}|{page[^1].Id}" : null,
        });
    }

    [WolverineGet("/api/v1/recipes/{recipeId}")]
    public async Task<IResult> GetAsync(string recipeId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var recipe = await ctx.Set<Recipe>().AsNoTracking()
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.Id == recipeId);

        if (recipe is null) return Results.NotFound();

        var access = await household.ResolveAsync(recipe.ChannelId, ChannelType.Meals, userId, Permissions.ViewChannel);
        if (access.ToFailure() is { } failure) return failure;

        return Results.Ok(MealPlanService.ToRecipeDto(recipe));
    }

    [WolverinePost("/api/v1/channels/{channelId}/recipes")]
    public async Task<IResult> CreateAsync(string channelId, CreateRecipeDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var access = await household.ResolveAsync(channelId, ChannelType.Meals, userId, Permissions.PlanMeals);
        if (access.ToFailure() is { } failure) return failure;

        if (ValidateRecipe(dto.Title, dto.Servings, dto.Ingredients) is { } invalid)
            return Results.BadRequest(invalid);

        var count = await ctx.Set<Recipe>().CountAsync(r => r.ChannelId == channelId);
        if (count >= Recipe.MaxPerChannel)
            return Results.BadRequest($"A meals channel holds at most {Recipe.MaxPerChannel} recipes");

        var recipe = Recipe.Create(new CreateRecipeParams
        {
            ChannelId = channelId,
            GuildId = access.Channel!.GuildId,
            Title = dto.Title.Trim(),
            Description = dto.Description,
            Servings = dto.Servings ?? 2,
            PrepMinutes = dto.PrepMinutes,
            SourceUrl = dto.SourceUrl,
            CreatedByUserId = userId,
        });

        ApplyIngredients(recipe, dto.Ingredients);

        ctx.Set<Recipe>().Add(recipe);
        await ctx.SaveChangesAsync();

        var payload = MealPlanService.ToRecipeDto(recipe);

        await household.BroadcastAsync(recipe.GuildId, channelId, "guild.RecipeCreated",
            new { GuildId = recipe.GuildId, ChannelId = channelId, Recipe = payload });

        return Results.Ok(payload);
    }

    [WolverinePatch("/api/v1/recipes/{recipeId}")]
    public async Task<IResult> UpdateAsync(string recipeId, UpdateRecipeDto dto,
        [NotBody] HouseholdChannelService household, [NotBody] MicroserviceContext ctx,
        [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var recipe = await ctx.Set<Recipe>()
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.Id == recipeId);

        if (recipe is null) return Results.NotFound();

        // Editing your own recipe needs only PlanMeals; editing somebody else's is a moderator
        // action.
        var required = recipe.CreatedByUserId == userId ? Permissions.PlanMeals : Permissions.ManageMeals;
        var access = await household.ResolveAsync(recipe.ChannelId, ChannelType.Meals, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        if (ValidateRecipe(dto.Title ?? recipe.Title, dto.Servings, dto.Ingredients) is { } invalid)
            return Results.BadRequest(invalid);

        if (dto.Title is not null) recipe.Title = dto.Title.Trim();

        if (dto.ClearDescription) recipe.Description = null;
        else if (dto.Description is not null) recipe.Description = dto.Description;

        if (dto.ClearPrepMinutes) recipe.PrepMinutes = null;
        else if (dto.PrepMinutes is not null) recipe.PrepMinutes = dto.PrepMinutes;

        if (dto.ClearSourceUrl) recipe.SourceUrl = null;
        else if (dto.SourceUrl is not null) recipe.SourceUrl = dto.SourceUrl;

        if (dto.Servings is not null) recipe.Servings = dto.Servings.Value;

        if (dto.Ingredients is not null)
        {
            // Replaced wholesale rather than merged.
            ctx.Set<RecipeIngredient>().RemoveRange(recipe.Ingredients);
            recipe.Ingredients.Clear();
            ApplyIngredients(recipe, dto.Ingredients);
        }

        recipe.UpdatedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();

        var payload = MealPlanService.ToRecipeDto(recipe);

        await household.BroadcastAsync(recipe.GuildId, recipe.ChannelId, "guild.RecipeUpdated",
            new { GuildId = recipe.GuildId, ChannelId = recipe.ChannelId, Recipe = payload });

        return Results.Ok(payload);
    }

    [WolverineDelete("/api/v1/recipes/{recipeId}")]
    public async Task<IResult> DeleteAsync(string recipeId, [NotBody] HouseholdChannelService household,
        [NotBody] MicroserviceContext ctx, [NotBody] ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId)) return Results.Unauthorized();

        var recipe = await ctx.Set<Recipe>().FirstOrDefaultAsync(r => r.Id == recipeId);
        if (recipe is null) return Results.NotFound();

        var required = recipe.CreatedByUserId == userId ? Permissions.PlanMeals : Permissions.ManageMeals;
        var access = await household.ResolveAsync(recipe.ChannelId, ChannelType.Meals, userId, required);
        if (access.ToFailure() is { } failure) return failure;

        // The plan keeps its entries and loses the link (SetNull on the FK), so deleting a recipe
        // does not silently erase a week somebody has already agreed to cook.
        ctx.Set<Recipe>().Remove(recipe);
        await ctx.SaveChangesAsync();

        await household.BroadcastAsync(recipe.GuildId, recipe.ChannelId, "guild.RecipeDeleted",
            new { GuildId = recipe.GuildId, ChannelId = recipe.ChannelId, RecipeId = recipeId });

        return Results.NoContent();
    }

    /// <summary>Everything a recipe body can be wrong about, or null when it is fine.</summary>
    private static string? ValidateRecipe(string? title, int? servings, List<RecipeIngredientInputDto>? ingredients)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Title is required";
        if (title.Length > Recipe.MaxTitleLength)
            return $"Title must be {Recipe.MaxTitleLength} characters or fewer";

        if (servings is not null && (servings < Recipe.MinServings || servings > Recipe.MaxServings))
            return $"Servings must be between {Recipe.MinServings} and {Recipe.MaxServings}";

        if (ingredients is null) return null;

        if (ingredients.Count > Recipe.MaxIngredients)
            return $"A recipe holds at most {Recipe.MaxIngredients} ingredients";

        foreach (var ingredient in ingredients)
        {
            if (string.IsNullOrWhiteSpace(ingredient.Text)) return "Ingredient text is required";
            if (ingredient.Text.Length > Recipe.MaxIngredientLength)
                return $"Ingredient text must be {Recipe.MaxIngredientLength} characters or fewer";
        }

        return null;
    }

    /// <summary>Rebuilds the ingredient list from the request, deriving each match name.</summary>
    private static void ApplyIngredients(Recipe recipe, List<RecipeIngredientInputDto> ingredients)
    {
        for (var i = 0; i < ingredients.Count; i++)
        {
            var input = ingredients[i];
            var matchName = IngredientMatch.Normalize(
                string.IsNullOrWhiteSpace(input.MatchName) ? input.Text : input.MatchName);

            recipe.Ingredients.Add(new RecipeIngredient
            {
                RecipeId = recipe.Id,
                Position = i,
                Text = input.Text.Trim(),
                MatchName = matchName.Length == 0 ? null : matchName,
                IsOptional = input.IsOptional,
            });
        }
    }
}
