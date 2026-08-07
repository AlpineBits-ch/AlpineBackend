using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Request;

public class RecipeIngredientInputDto
{
    /// <summary>What to buy, as the cook would write it on a shopping list: "2 onions".</summary>
    public required string Text { get; set; }

    /// <summary>Optional override for the noun this line matches on.</summary>
    public string? MatchName { get; set; }

    public bool IsOptional { get; set; }
}

public class CreateRecipeDto
{
    public required string Title { get; set; }
    public string? Description { get; set; }
    public int? Servings { get; set; }
    public int? PrepMinutes { get; set; }
    public string? SourceUrl { get; set; }
    public List<RecipeIngredientInputDto> Ingredients { get; set; } = [];
}

public class UpdateRecipeDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Servings { get; set; }
    public int? PrepMinutes { get; set; }
    public string? SourceUrl { get; set; }

    /// <summary>Replaces the whole ingredient list when present, left null keeps it.</summary>
    public List<RecipeIngredientInputDto>? Ingredients { get; set; }

    public bool ClearDescription { get; set; }
    public bool ClearPrepMinutes { get; set; }
    public bool ClearSourceUrl { get; set; }
}

public class CreateMealPlanEntryDto
{
    public required DateOnly Date { get; set; }
    public MealSlot Slot { get; set; } = MealSlot.Dinner;
    public string? RecipeId { get; set; }
    public string? FreeText { get; set; }
    public string? CookUserId { get; set; }
    public int? Servings { get; set; }
}

public class UpdateMealPlanEntryDto
{
    public DateOnly? Date { get; set; }
    public MealSlot? Slot { get; set; }
    public string? RecipeId { get; set; }
    public string? FreeText { get; set; }
    public string? CookUserId { get; set; }
    public int? Servings { get; set; }
    public int? Position { get; set; }

    public bool ClearRecipe { get; set; }
    public bool ClearFreeText { get; set; }
    public bool ClearCook { get; set; }
    public bool ClearServings { get; set; }
}

/// <summary>The plan-to-shopping-list request.</summary>
public class GenerateShoppingListDto
{
    public required DateOnly From { get; set; }
    public required DateOnly To { get; set; }

    /// <summary>Falls back to <c>MealPlanConfig.ShoppingListChannelId</c>.</summary>
    public string? ListChannelId { get; set; }

    public bool IncludeOptional { get; set; }

    /// <summary>Skips the "do we already have this" step, so everything the plan needs is added.
    /// For the house that knows its pantry is out of date and would rather buy a spare.</summary>
    public bool SkipPantry { get; set; }
}

public class UpdateMealPlanConfigDto
{
    public string? ShoppingListChannelId { get; set; }
    public string? PantryChannelId { get; set; }

    public bool ClearShoppingList { get; set; }
    public bool ClearPantry { get; set; }
}
