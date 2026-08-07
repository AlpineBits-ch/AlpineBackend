using Guild.Domain.Enums;
using Persistence;

namespace Guild.Domain.Entity;

public class CreateMealPlanEntryParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public DateOnly Date { get; set; }
    public MealSlot Slot { get; set; } = MealSlot.Dinner;
    public string? RecipeId { get; set; }
    public string? FreeText { get; set; }
    public string? CookUserId { get; set; }
    public int? Servings { get; set; }
    public int Position { get; set; }
    public string CreatedByUserId { get; set; } = null!;
}

/// <summary>One thing the house has planned to eat on one day: a recipe, or a sentence.</summary>
public class MealPlanEntry : BaseEntity<MealPlanEntry>, IPrefixedEntity
{
    public static string Prefix { get; } = "mple";

    public const int MaxFreeTextLength = 200;

    /// <summary>The widest window <c>GET /meal-plan</c> will answer for.</summary>
    public const int MaxWindowDays = 60;

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>A plain calendar date, not an instant.</summary>
    public DateOnly Date { get; set; }

    public MealSlot Slot { get; set; } = MealSlot.Dinner;

    /// <summary>Null when this entry is <see cref="FreeText"/> instead.</summary>
    public string? RecipeId { get; set; }

    /// <summary>Null when this entry points at a <see cref="Recipe"/> instead.</summary>
    public string? FreeText { get; set; }

    /// <summary>Who is cooking, if anyone has said.</summary>
    public string? CookUserId { get; set; }

    /// <summary>How many are eating, when that differs from the recipe's own figure.</summary>
    public int? Servings { get; set; }

    /// <summary>Order within one date and slot, for a day with more than one thing on it.</summary>
    public int Position { get; set; }

    /// <summary>Who put this on the board.</summary>
    public string CreatedByUserId { get; set; } = null!;

    /// <summary>
    /// When the cook was told this is theirs today, or null if they have not been.
    /// </summary>
    public DateTimeOffset? NotifiedAt { get; set; }

    public static MealPlanEntry Create(CreateMealPlanEntryParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new MealPlanEntry
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Date = @params.Date,
            Slot = @params.Slot,
            RecipeId = @params.RecipeId,
            FreeText = @params.FreeText,
            CookUserId = @params.CookUserId,
            Servings = @params.Servings,
            Position = @params.Position,
            CreatedByUserId = @params.CreatedByUserId,
        };
    }
}

/// <summary>
/// Per-meals-channel settings, upserted and defaulted like <see cref="PantryConfig"/> - "the
/// config" always exists conceptually, defaulting to no linked list and no linked pantry.
/// </summary>
public class MealPlanConfig
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>The List channel a generated shopping list is appended to.</summary>
    public string? ShoppingListChannelId { get; set; }

    /// <summary>The Pantry channel "do we already have this" is asked of, and the stock the
    /// cookable board ranks against. Null disables both, which degrades to a shopping list that
    /// contains everything and a cookable board that returns nothing - not to an error.</summary>
    public string? PantryChannelId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

// ── Integrator: paste into MicroserviceContext.OnModelCreating ───────────────
// modelBuilder.Entity<MealPlanEntry>(entryBuilder =>
// {
//     entryBuilder.HasOne<Domain.Aggregates.Channel>()
//         .WithMany()
//         .HasForeignKey(x => x.ChannelId)
//         .OnDelete(DeleteBehavior.Cascade);
//
//     // Deleting a recipe must not delete the week's plan - the entry keeps its free text or
//     // simply loses its link, which is what a cook expects after tidying the cookbook.
//     entryBuilder.HasOne<Recipe>()
//         .WithMany()
//         .HasForeignKey(x => x.RecipeId)
//         .OnDelete(DeleteBehavior.SetNull);
//
//     // The board query: this channel's plan across a date window, in reading order.
//     entryBuilder.HasIndex(x => new { x.ChannelId, x.Date, x.Slot, x.Position });
//
//     // The cooking-today sweep's query: unnotified entries that have a cook and are due. Filtered
//     // so the index stays proportional to what is outstanding rather than to the whole plan
//     // history, which is append-only and never pruned.
//     entryBuilder.HasIndex(x => x.Date)
//         .HasFilter("notified_at IS NULL AND cook_user_id IS NOT NULL");
// });
//
// modelBuilder.Entity<MealPlanConfig>(configBuilder =>
// {
//     configBuilder.HasKey(x => x.ChannelId);
//
//     configBuilder.HasOne<Domain.Aggregates.Channel>()
//         .WithOne()
//         .HasForeignKey<MealPlanConfig>(x => x.ChannelId)
//         .OnDelete(DeleteBehavior.Cascade);
// });
//
// DbSet: public DbSet<MealPlanEntry> MealPlanEntries { get; set; }
// DbSet: public DbSet<MealPlanConfig> MealPlanConfigs { get; set; }
// MapEnum: options.MapEnum<MealSlot>();
