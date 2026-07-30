using Persistence;

namespace Guild.Domain.Entity;

public class CreatePantryItemParams
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;
    public string Name { get; set; } = null!;
    public decimal Quantity { get; set; }
    public string? Unit { get; set; }
    public decimal? LowThreshold { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string AddedByUserId { get; set; } = null!;
}

/// <summary>Stock of one thing in one location. The location is the channel (Fridge, Freezer,
/// Cellar), not a column - that way per-location visibility is just channel permissions.
///
/// Shadow-side FK to Channel with no navigation property, as <see cref="ListItem"/>.</summary>
public class PantryItem : BaseEntity<PantryItem>, IPrefixedEntity
{
    public static string Prefix { get; } = "pitm";

    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>Decimal here, unlike ListItem.Quantity's free text, because this one is actually
    /// compared against <see cref="LowThreshold"/>.</summary>
    public decimal Quantity { get; set; }

    public string? Unit { get; set; }

    /// <summary>Null disables restock tracking for this item. When set, dropping to or below it
    /// appends the item to the pantry's configured restock list.</summary>
    public decimal? LowThreshold { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Stamped when a low-stock restock was appended to the list, cleared when the
    /// quantity climbs back above the threshold. This is the idempotency guard: without it every
    /// further decrement below the threshold would append a duplicate line to the shopping
    /// list.</summary>
    public DateTimeOffset? RestockedAt { get; set; }

    public string AddedByUserId { get; set; } = null!;

    public static PantryItem Create(CreatePantryItemParams @params)
    {
        var date = DateTimeOffset.UtcNow;
        return new PantryItem
        {
            Id = GenerateId(),
            CreatedAt = date,
            UpdatedAt = date,
            ChannelId = @params.ChannelId,
            GuildId = @params.GuildId,
            Name = @params.Name,
            Quantity = @params.Quantity,
            Unit = @params.Unit,
            LowThreshold = @params.LowThreshold,
            ExpiresAt = @params.ExpiresAt,
            AddedByUserId = @params.AddedByUserId,
        };
    }

    /// <summary>True when this item has just crossed from "stocked" to "needs restocking" and
    /// hasn't already been put on the list.</summary>
    public bool NeedsRestock() =>
        LowThreshold is not null && Quantity <= LowThreshold && RestockedAt is null;
}

/// <summary>Per-pantry-channel settings, upserted like ForumConfig - "the config" always exists
/// conceptually, defaulting to no linked list.</summary>
public class PantryConfig
{
    public string ChannelId { get; set; } = null!;
    public string GuildId { get; set; } = null!;

    /// <summary>The List channel low-stock items get appended to. Null means restock tracking is
    /// off for this pantry regardless of individual item thresholds.</summary>
    public string? RestockListChannelId { get; set; }

    public int ExpiryWarningDays { get; set; } = 3;

    public DateTimeOffset UpdatedAt { get; set; }
}
