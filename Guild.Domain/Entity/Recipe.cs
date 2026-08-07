using Persistence;

namespace Guild.Domain.Entity;

public class CreateRecipeParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public int Servings { get; set; } = 2;
    public int? PrepMinutes { get; set; }
    public string? SourceUrl { get; set; }
    public string CreatedByUserId { get; set; } = null!;
}

/// <summary>
/// Something the house cooks, on a <see cref="Enums.ChannelType.Meals"/> channel.
/// </summary>
public class Recipe : BaseEntity<Recipe>, IPrefixedEntity
{
    public static string Prefix { get; } = "rcpe";

    /// <summary>A channel is one cookbook.</summary>
    public const int MaxPerChannel = 200;

    public const int MaxIngredients = 60;
    public const int MaxIngredientLength = 200;
    public const int MaxTitleLength = 150;
    public const int MinServings = 1;
    public const int MaxServings = 50;

    public string ChannelId { get; set; } = null!;

    /// <summary>Denormalized from the parent channel so authorization needs no join back to
    /// Channel - same trade-off as <see cref="ListItem.GuildId"/>.</summary>
    public string GuildId { get; set; } = null!;

    public string Title { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>How many this recipe as written feeds.</summary>
    public int Servings { get; set; } = 2;

    public int? PrepMinutes { get; set; }

    /// <summary>Where it came from, when it came from somewhere.</summary>
    public string? SourceUrl { get; set; }

    public string CreatedByUserId { get; set; } = null!;

    public virtual ICollection<RecipeIngredient> Ingredients { get; set; } = [];

    public static Recipe Create(CreateRecipeParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new Recipe
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Title = @params.Title,
            Description = @params.Description,
            Servings = @params.Servings,
            PrepMinutes = @params.PrepMinutes,
            SourceUrl = @params.SourceUrl,
            CreatedByUserId = @params.CreatedByUserId,
        };
    }
}

/// <summary>One line of a recipe's ingredient list, kept as the cook wrote it.</summary>
public class RecipeIngredient
{
    public string RecipeId { get; set; } = null!;
    public virtual Recipe Recipe { get; set; } = null!;

    /// <summary>Half the composite key.</summary>
    public int Position { get; set; }

    /// <summary>
    /// What to buy, exactly as typed: "2 onions", "a pinch of salt", "1 tin chopped tomatoes".
    /// </summary>
    public string Text { get; set; } = null!;

    /// <summary>
    /// The normalized noun <see cref="Text"/> reduces to ("onion", "salt"), used to ask the pantry
    /// "do we already have this" and to avoid re-adding something already on the shopping list.
    /// </summary>
    public string? MatchName { get; set; }

    /// <summary>Excluded from the shopping list unless the caller asks for it.</summary>
    public bool IsOptional { get; set; }
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<Recipe>(recipeBuilder =>
// {
//     recipeBuilder.HasOne<Domain.Aggregates.Channel>()
//         .WithMany()
//         .HasForeignKey(x => x.ChannelId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // The cookbook board: this channel's recipes in title order, which is also the paging key.
//     recipeBuilder.HasIndex(x => new { x.ChannelId, x.Title });
// });
//
// modelBuilder.Entity<RecipeIngredient>(ingredientBuilder =>
// {
//     ingredientBuilder.HasKey(x => new { x.RecipeId, x.Position });
//
//     ingredientBuilder.HasOne(x => x.Recipe)
//         .WithMany(x => x.Ingredients)
//         .HasForeignKey(x => x.RecipeId)
//         .OnDelete(DeleteBehavior.Cascade);
// });
//
// DbSet: public DbSet<Recipe> Recipes { get; set; }
// DbSet: public DbSet<RecipeIngredient> RecipeIngredients { get; set; }
// MapEnum: none for this file.
