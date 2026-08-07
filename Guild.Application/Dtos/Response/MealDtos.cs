using Guild.Domain.Enums;

namespace Guild.Application.Dtos.Response;

public class RecipeIngredientDto
{
    public required int Position { get; init; }
    public required string Text { get; init; }
    public string? MatchName { get; init; }
    public required bool IsOptional { get; init; }
}

public class RecipeDto
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required int Servings { get; init; }
    public int? PrepMinutes { get; init; }
    public string? SourceUrl { get; init; }
    public required string CreatedByUserId { get; init; }
    public required IReadOnlyList<RecipeIngredientDto> Ingredients { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public class RecipePageDto
{
    public required IReadOnlyList<RecipeDto> Items { get; init; }

    /// <summary>Null when the page is the last one.</summary>
    public string? NextCursor { get; init; }
}

public class MealPlanEntryDto
{
    public required string Id { get; init; }
    public required string ChannelId { get; init; }
    public required DateOnly Date { get; init; }
    public required MealSlot Slot { get; init; }
    public string? RecipeId { get; init; }

    /// <summary>Denormalized onto the entry so a week's board renders without the client fetching
    /// every recipe it references.</summary>
    public string? RecipeTitle { get; init; }

    public string? FreeText { get; init; }
    public string? CookUserId { get; init; }
    public int? Servings { get; init; }
    public required int Position { get; init; }
}

public class MealPlanConfigDto
{
    public required string ChannelId { get; init; }
    public string? ShoppingListChannelId { get; init; }
    public string? PantryChannelId { get; init; }
}

/// <summary>What the plan-to-shopping-list button did, including what it chose not to do.</summary>
public class ShoppingListResultDto
{
    public required IReadOnlyList<ListItemDto> Added { get; init; }

    /// <summary>Ingredient lines dropped because the configured pantry already has them in
    /// stock.</summary>
    public required IReadOnlyList<string> SkippedInPantry { get; init; }

    /// <summary>Ingredient lines dropped because an unchecked line on the target list already
    /// covers them.</summary>
    public required IReadOnlyList<string> SkippedOnList { get; init; }

    /// <summary>True when the per-call line cap was hit and the plan had more to add.</summary>
    public required bool Truncated { get; init; }
}

public class CookableRecipeDto
{
    public required RecipeDto Recipe { get; init; }

    /// <summary>Ingredients the configured pantry can cover, optional ones included.</summary>
    public required int HaveCount { get; init; }

    /// <summary>Required ingredients the pantry cannot cover.</summary>
    public required int MissingCount { get; init; }

    /// <summary>How many of the covering pantry items are inside the expiry horizon.</summary>
    public required int ExpiringCount { get; init; }

    public required IReadOnlyList<string> ExpiringNames { get; init; }
    public required IReadOnlyList<string> Missing { get; init; }
}

public class CookableResultDto
{
    public required IReadOnlyList<CookableRecipeDto> Items { get; init; }

    /// <summary>Why <see cref="Items"/> is empty when it is empty: no pantry module, no configured
    /// pantry, or nothing in stock. Null when the answer is a genuine ranking. A 500 or a bare
    /// empty array would both leave the house believing the feature is broken when it is
    /// unconfigured.</summary>
    public string? Reason { get; init; }
}
